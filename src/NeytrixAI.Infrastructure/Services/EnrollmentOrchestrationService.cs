using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;

namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Application service that executes the enrollment side-effects behind the
/// typed agent tools. Every method is fail-closed: eligibility is re-checked by
/// the deterministic <see cref="EligibilityEngine"/> before any registration is
/// created, duplicates are rejected, and enrollment can only complete once a
/// waiver is signed and payment is confirmed (enforced by the Registration
/// aggregate). The conversational agent NEVER writes to the database directly —
/// it goes through these permissioned operations.
/// </summary>
public sealed class EnrollmentOrchestrationService
{
    private readonly ITenantRepository _tenants;
    private readonly IGuardianRepository _guardians;
    private readonly IPlayerRepository _players;
    private readonly IProgramRepository _programs;
    private readonly IRegistrationRepository _registrations;
    private readonly IStripeAdapter _stripe;
    private readonly IGoogleCalendarAdapter _calendar;
    private readonly EligibilityEngine _eligibility;

    public EnrollmentOrchestrationService(
        ITenantRepository tenants,
        IGuardianRepository guardians,
        IPlayerRepository players,
        IProgramRepository programs,
        IRegistrationRepository registrations,
        IStripeAdapter stripe,
        IGoogleCalendarAdapter calendar,
        EligibilityEngine eligibility)
    {
        _tenants = tenants;
        _guardians = guardians;
        _players = players;
        _programs = programs;
        _registrations = registrations;
        _stripe = stripe;
        _calendar = calendar;
        _eligibility = eligibility;
    }

    public async Task<IReadOnlyList<ProgramMatch>> MatchProgramsAsync(Guid tenantId, Guid playerId, CancellationToken ct = default)
    {
        var player = await _players.GetByIdAsync(tenantId, playerId, ct)
            ?? throw new InvalidOperationException("Player not found.");

        var programs = (await _programs.GetByTenantAsync(tenantId, ct)).ToList();
        var counts = await BuildEnrollmentCountsAsync(tenantId, programs, ct);
        return _eligibility.MatchPrograms(player, programs, counts);
    }

    public async Task<EligibilityResult> CheckEligibilityAsync(Guid tenantId, Guid playerId, Guid programId, CancellationToken ct = default)
    {
        var player = await _players.GetByIdAsync(tenantId, playerId, ct);
        var program = await _programs.GetByIdAsync(tenantId, programId, ct);

        // Fail closed: missing data => ineligible, never a silent pass.
        if (player is null || program is null)
            return EligibilityResult.Ineligible(new[] { "Player or program could not be found." });

        var count = await CountActiveEnrollmentsAsync(tenantId, programId, ct);
        return _eligibility.CheckEligibility(player, program, count);
    }

    /// <summary>
    /// Creates a registration only if the deterministic eligibility check passes.
    /// Ineligible players are rejected; full programs produce a waitlisted
    /// registration rather than an enrolled one. Duplicates are rejected.
    /// </summary>
    public async Task<Registration> CreateRegistrationAsync(
        Guid tenantId, Guid guardianId, Guid playerId, Guid programId, CancellationToken ct = default)
    {
        if (await _registrations.ExistsAsync(tenantId, playerId, programId, ct))
            throw new InvalidOperationException("An active registration already exists for this player and program.");

        var eligibility = await CheckEligibilityAsync(tenantId, playerId, programId, ct);
        if (eligibility.Status == EligibilityStatus.Ineligible)
            throw new EligibilityException(eligibility.FailureReasons);

        var isWaitlist = eligibility.Status == EligibilityStatus.WaitlistOnly;
        int? waitlistPosition = isWaitlist
            ? (await CountWaitlistedAsync(tenantId, programId, ct)) + 1
            : null;

        var registration = Registration.Create(tenantId, guardianId, playerId, programId, isWaitlist, waitlistPosition);
        await _registrations.CreateAsync(registration, ct);
        return registration;
    }

    public async Task<PaymentLinkResult> CreatePaymentLinkAsync(
        Guid tenantId, Guid registrationId, bool depositOnly, string successUrl, string cancelUrl, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetByIdAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Tenant not found.");
        if (string.IsNullOrWhiteSpace(tenant.StripeAccountId))
            throw new InvalidOperationException("Tenant has no connected Stripe account; cannot take payment.");

        var registration = await _registrations.GetByIdAsync(tenantId, registrationId, ct)
            ?? throw new InvalidOperationException("Registration not found.");
        if (registration.IsWaitlisted)
            throw new InvalidOperationException("Waitlisted registrations cannot be paid until a spot opens.");

        var program = await _programs.GetByIdAsync(tenantId, registration.ProgramId, ct)
            ?? throw new InvalidOperationException("Program not found.");

        var amountCents = depositOnly && program.DepositCents > 0 ? program.DepositCents : program.PriceCents;

        var result = await _stripe.CreateCheckoutSessionAsync(
            tenant.StripeAccountId, registrationId, amountCents, program.Currency,
            successUrl, cancelUrl, depositOnly, ct);

        registration.MarkPaymentPending(result.CheckoutSessionId);
        await _registrations.UpdateAsync(registration, ct);
        return result;
    }

    public async Task<BookedEvent> BookAssessmentAsync(
        Guid tenantId, Guid registrationId, string slotId, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetByIdAsync(tenantId, ct)
            ?? throw new InvalidOperationException("Tenant not found.");
        if (string.IsNullOrWhiteSpace(tenant.GoogleCalendarId))
            throw new InvalidOperationException("Tenant has no connected Google Calendar; cannot book assessment.");

        var registration = await _registrations.GetByIdAsync(tenantId, registrationId, ct)
            ?? throw new InvalidOperationException("Registration not found.");
        var program = await _programs.GetByIdAsync(tenantId, registration.ProgramId, ct)
            ?? throw new InvalidOperationException("Program not found.");
        var player = await _players.GetByIdAsync(tenantId, registration.PlayerId, ct)
            ?? throw new InvalidOperationException("Player not found.");
        var guardian = await _guardians.GetByIdAsync(tenantId, registration.GuardianId, ct)
            ?? throw new InvalidOperationException("Guardian not found.");

        var booked = await _calendar.BookSlotAsync(
            tenant.GoogleCalendarId, slotId, guardian.FullName, guardian.Email,
            player.FullName, program.Name, ct);

        registration.MarkAssessmentScheduled();
        await _registrations.UpdateAsync(registration, ct);
        return booked;
    }

    private async Task<IDictionary<Guid, int>> BuildEnrollmentCountsAsync(
        Guid tenantId, IEnumerable<Program> programs, CancellationToken ct)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var program in programs)
            counts[program.Id] = await CountActiveEnrollmentsAsync(tenantId, program.Id, ct);
        return counts;
    }

    private async Task<int> CountActiveEnrollmentsAsync(Guid tenantId, Guid programId, CancellationToken ct)
    {
        var registrations = await _registrations.GetByProgramAsync(tenantId, programId, ct);
        return registrations.Count(r => r.Status is not RegistrationStatus.Cancelled and not RegistrationStatus.Waitlisted);
    }

    private async Task<int> CountWaitlistedAsync(Guid tenantId, Guid programId, CancellationToken ct)
    {
        var registrations = await _registrations.GetByProgramAsync(tenantId, programId, ct);
        return registrations.Count(r => r.Status == RegistrationStatus.Waitlisted);
    }
}

public sealed class EligibilityException : Exception
{
    public IReadOnlyList<string> Reasons { get; }

    public EligibilityException(IReadOnlyList<string> reasons)
        : base("Player is not eligible: " + string.Join(" ", reasons))
    {
        Reasons = reasons;
    }
}

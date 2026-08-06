using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Adapters;

namespace NeytrixAI.Infrastructure.Services;

/// <summary>Permissioned dispatcher for the legacy enrollment tool names.</summary>
public sealed class EnrollmentOrchestrationService
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IGuardianRepository _guardianRepository;
    private readonly IPlayerRepository _playerRepository;
    private readonly IProgramRepository _programRepository;
    private readonly IRegistrationRepository _registrationRepository;
    private readonly IStripeAdapter _stripeAdapter;
    private readonly IGoogleCalendarAdapter _calendarAdapter;
    private readonly StripeOptions _stripeOptions;

    public EnrollmentOrchestrationService(
        ITenantRepository tenantRepository,
        IGuardianRepository guardianRepository,
        IPlayerRepository playerRepository,
        IProgramRepository programRepository,
        IRegistrationRepository registrationRepository,
        IStripeAdapter stripeAdapter,
        IGoogleCalendarAdapter calendarAdapter,
        IOptions<StripeOptions> stripeOptions)
    {
        _tenantRepository = tenantRepository;
        _guardianRepository = guardianRepository;
        _playerRepository = playerRepository;
        _programRepository = programRepository;
        _registrationRepository = registrationRepository;
        _stripeAdapter = stripeAdapter;
        _calendarAdapter = calendarAdapter;
        _stripeOptions = stripeOptions.Value;
    }

    public Task<string> HandleToolCallAsync(string toolName, Dictionary<string, object> parameters, Guid tenantId) =>
        toolName switch
        {
            "search_programs" => SearchProgramsAsync(parameters, tenantId),
            "check_eligibility" => CheckEligibilityAsync(parameters, tenantId),
            "create_registration" => CreateRegistrationAsync(parameters, tenantId),
            "process_payment" => ProcessPaymentAsync(parameters, tenantId),
            "book_calendar_event" => BookCalendarEventAsync(parameters, tenantId),
            _ => Task.FromResult(JsonSerializer.Serialize(new { error = "Unknown tool", tool = toolName }))
        };

    private async Task<string> SearchProgramsAsync(IReadOnlyDictionary<string, object> parameters, Guid tenantId)
    {
        var programs = await _programRepository.GetByTenantAsync(tenantId);
        var sport = GetString(parameters, "sport") ?? GetString(parameters, "sport_type");
        var age = GetInt(parameters, "player_age") ?? GetInt(parameters, "min_age");
        var matches = programs.Where(p => p.IsRegistrationOpen)
            .Where(p => sport is null || string.Equals(p.Sport, sport, StringComparison.OrdinalIgnoreCase))
            .Where(p => age is null || (p.MinAgeYears <= age && p.MaxAgeYears >= age))
            .Select(p => new { id = p.Id, name = p.Name, description = p.Description, sport = p.Sport,
                min_age_years = p.MinAgeYears, max_age_years = p.MaxAgeYears, price_cents = p.PriceCents,
                deposit_cents = p.DepositCents, currency = p.Currency, start_date = p.StartDate,
                end_date = p.EndDate, location = p.Location });
        return JsonSerializer.Serialize(new { programs = matches });
    }

    private async Task<string> CheckEligibilityAsync(IReadOnlyDictionary<string, object> parameters, Guid tenantId)
    {
        if (!TryGetGuid(parameters, "program_id", out var programId) || GetInt(parameters, "player_age") is not { } playerAge)
            return JsonSerializer.Serialize(new { eligible = false, reason = "program_id and player_age are required." });

        var program = await _programRepository.GetByIdAsync(tenantId, programId);
        if (program is null)
            return JsonSerializer.Serialize(new { eligible = false, reason = "Program not found." });

        var ageEligible = program.IsRegistrationOpen && playerAge >= program.MinAgeYears && playerAge <= program.MaxAgeYears;
        var hasCapacity = await _programRepository.HasCapacityAsync(tenantId, programId);
        return JsonSerializer.Serialize(new
        {
            eligible = ageEligible,
            has_capacity = hasCapacity,
            waitlist_only = ageEligible && !hasCapacity,
            program_name = program.Name,
            reason = ageEligible ? "Player meets the program's age and registration-window requirements." : "Player does not meet the program's eligibility requirements."
        });
    }

    private async Task<string> CreateRegistrationAsync(IReadOnlyDictionary<string, object> parameters, Guid tenantId)
    {
        if (!TryGetGuid(parameters, "guardian_id", out var guardianId) || !TryGetGuid(parameters, "player_id", out var playerId) || !TryGetGuid(parameters, "program_id", out var programId))
            return JsonSerializer.Serialize(new { success = false, error = "guardian_id, player_id, and program_id are required." });

        if (await _registrationRepository.ExistsAsync(tenantId, playerId, programId))
            return JsonSerializer.Serialize(new { success = false, error = "A registration already exists for this player and program." });

        var guardian = await _guardianRepository.GetByIdAsync(tenantId, guardianId);
        var player = await _playerRepository.GetByIdAsync(tenantId, playerId);
        var program = await _programRepository.GetByIdAsync(tenantId, programId);
        if (guardian is null || player is null || program is null || player.GuardianId != guardian.Id)
            return JsonSerializer.Serialize(new { success = false, error = "Guardian, player, or program was not found for this tenant." });

        var hasCapacity = await _programRepository.HasCapacityAsync(tenantId, programId);
        var registration = Registration.Create(tenantId, guardianId, playerId, programId, waitlisted: !hasCapacity, waitlistPosition: hasCapacity ? null : 1);
        var registrationId = await _registrationRepository.CreateAsync(registration);
        return JsonSerializer.Serialize(new { success = true, registration_id = registrationId, registration.Status, is_waitlisted = registration.IsWaitlisted, registration.WaitlistPosition });
    }

    private async Task<string> ProcessPaymentAsync(IReadOnlyDictionary<string, object> parameters, Guid tenantId)
    {
        if (!TryGetGuid(parameters, "registration_id", out var registrationId))
            return JsonSerializer.Serialize(new { success = false, error = "registration_id is required." });

        var registration = await _registrationRepository.GetByIdAsync(tenantId, registrationId);
        if (registration is null)
            return JsonSerializer.Serialize(new { success = false, error = "Registration not found." });

        var program = await _programRepository.GetByIdAsync(tenantId, registration.ProgramId);
        var tenant = await _tenantRepository.GetByIdAsync(tenantId);
        if (program is null || tenant?.StripeAccountId is null)
            return JsonSerializer.Serialize(new { success = false, error = "Program or tenant Stripe configuration is unavailable." });

        var depositOnly = GetBool(parameters, "deposit_only");
        var amount = GetLong(parameters, "amount_cents") ?? (depositOnly && program.DepositCents > 0 ? program.DepositCents : program.PriceCents);
        var successUrl = ExpandCheckoutUrl(_stripeOptions.SuccessUrlTemplate, registration.Id);
        var cancelUrl = ExpandCheckoutUrl(_stripeOptions.CancelUrlTemplate, registration.Id);
        var link = await _stripeAdapter.CreateCheckoutSessionAsync(tenant.StripeAccountId, registration.Id, amount, program.Currency, successUrl, cancelUrl, depositOnly, CancellationToken.None);
        registration.AttachCheckoutSession(link.CheckoutSessionId);
        await _registrationRepository.UpdateAsync(registration);
        return JsonSerializer.Serialize(new { success = true, checkout_session_id = link.CheckoutSessionId, payment_url = link.PaymentUrl, amount_cents = link.AmountCents, currency = link.Currency, expires_at = link.ExpiresAt });
    }

    private async Task<string> BookCalendarEventAsync(IReadOnlyDictionary<string, object> parameters, Guid tenantId)
    {
        if (!TryGetGuid(parameters, "registration_id", out var registrationId) || GetString(parameters, "slot_id") is not { } slotId)
            return JsonSerializer.Serialize(new { success = false, error = "registration_id and slot_id are required." });

        var registration = await _registrationRepository.GetByIdAsync(tenantId, registrationId);
        if (registration is null)
            return JsonSerializer.Serialize(new { success = false, error = "Registration not found." });

        var tenant = await _tenantRepository.GetByIdAsync(tenantId);
        var guardian = await _guardianRepository.GetByIdAsync(tenantId, registration.GuardianId);
        var player = await _playerRepository.GetByIdAsync(tenantId, registration.PlayerId);
        var program = await _programRepository.GetByIdAsync(tenantId, registration.ProgramId);
        if (tenant?.GoogleCalendarId is null || guardian is null || player is null || program is null)
            return JsonSerializer.Serialize(new { success = false, error = "Calendar or registration details are unavailable." });

        var booked = await _calendarAdapter.BookSlotAsync(tenant.GoogleCalendarId, slotId, guardian.FullName, guardian.Email, player.FullName, program.Name, CancellationToken.None);
        return JsonSerializer.Serialize(new { success = true, event_id = booked.EventId, scheduled_at = booked.StartsAt, html_link = booked.HtmlLink });
    }

    private static string ExpandCheckoutUrl(string template, Guid registrationId) =>
        string.IsNullOrWhiteSpace(template) ? $"https://example.invalid/registration/{registrationId}" : template.Replace("{registrationId}", registrationId.ToString(), StringComparison.Ordinal);

    private static string? GetString(IReadOnlyDictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) ? value switch { JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(), null => null, _ => Convert.ToString(value, CultureInfo.InvariantCulture) } : null;
    private static int? GetInt(IReadOnlyDictionary<string, object> values, string key) => int.TryParse(GetString(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long? GetLong(IReadOnlyDictionary<string, object> values, string key) => long.TryParse(GetString(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool GetBool(IReadOnlyDictionary<string, object> values, string key) => bool.TryParse(GetString(values, key), out var value) && value;
    private static bool TryGetGuid(IReadOnlyDictionary<string, object> values, string key, out Guid value) => Guid.TryParse(GetString(values, key), out value);
}

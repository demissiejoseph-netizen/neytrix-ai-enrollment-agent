using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Services;
using NeytrixAI.Tests.Fakes;
using Xunit;
using DomainProgram = NeytrixAI.Domain.Entities.Program;

namespace NeytrixAI.Tests;

// Behavioural tests for the permissioned enrollment side-effects. These assert the
// fail-closed guarantees the conversational agent depends on: ineligible players
// are rejected, duplicates are rejected, full programs waitlist (never silently
// enrol), and payment is blocked for waitlisted/misconfigured tenants. Includes
// the required failure path: payment failing after a waiver was already signed.
public sealed class EnrollmentOrchestrationServiceTests
{
    private sealed class Harness
    {
        public required EnrollmentOrchestrationService Service { get; init; }
        public required InMemoryGuardianRepository Guardians { get; init; }
        public required InMemoryPlayerRepository Players { get; init; }
        public required InMemoryProgramRepository Programs { get; init; }
        public required InMemoryRegistrationRepository Registrations { get; init; }
        public required InMemoryTenantRepository Tenants { get; init; }
        public required FakeStripeAdapter Stripe { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid GuardianId { get; init; }
        public required Guid PlayerId { get; init; }
    }

    private static Harness BuildHarness(
        bool withStripeAccount = true,
        int capacity = 20,
        int existingEnrolled = 0)
    {
        var guardians = new InMemoryGuardianRepository();
        var players = new InMemoryPlayerRepository();
        var programs = new InMemoryProgramRepository();
        var registrations = new InMemoryRegistrationRepository();
        var tenants = new InMemoryTenantRepository();
        var stripe = new FakeStripeAdapter();

        var tenant = Tenant.Create("acme", "Acme Sports");
        if (withStripeAccount) tenant.ConfigureStripe("acct_test_123");
        tenants.Store[tenant.Id] = tenant;
        var tenantId = tenant.Id;

        var guardian = Guardian.Create(tenantId, "Jane", "Doe", "jane@example.com");
        guardian.RecordGdprConsent();
        guardians.Store[guardian.Id] = guardian;

        var player = Player.Create(tenantId, guardian.Id, "Sam", "Doe", new DateOnly(2015, 6, 1), "male");
        players.Store[player.Id] = player;

        var program = DomainProgram.Create(
            tenantId, "Youth Soccer", "soccer",
            minAge: 6, maxAge: 14, capacity: capacity, priceCents: 10000,
            startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 12, 1),
            depositCents: 2500);
        programs.Store[program.Id] = program;

        for (var i = 0; i < existingEnrolled; i++)
        {
            var reg = Registration.Create(tenantId, guardian.Id, Guid.NewGuid(), program.Id);
            reg.MarkPaymentComplete(10000, "pi_seed");
            reg.MarkWaiverSigned();
            registrations.Store[reg.Id] = reg;
        }

        var eligibility = new EligibilityEngine();
        var service = new EnrollmentOrchestrationService(
            tenants, guardians, players, programs, registrations,
            stripe, new FakeCalendarAdapter(), eligibility);

        return new Harness
        {
            Service = service, Guardians = guardians, Players = players, Programs = programs,
            Registrations = registrations, Tenants = tenants, Stripe = stripe,
            TenantId = tenantId, GuardianId = guardian.Id, PlayerId = player.Id
        };
    }

    private static Guid ProgramId(Harness h) => h.Programs.Store.Values.First().Id;

    [Fact]
    public async Task CreateRegistration_EligiblePlayer_CreatesInquiry()
    {
        var h = BuildHarness();
        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));

        Assert.Equal(RegistrationStatus.Inquiry, reg.Status);
        Assert.False(reg.IsWaitlisted);
        Assert.Single(h.Registrations.Store.Values);
    }

    [Fact]
    public async Task CreateRegistration_FullProgram_Waitlists_NeverAutoEnrols()
    {
        var h = BuildHarness(capacity: 1, existingEnrolled: 1);
        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));

        Assert.True(reg.IsWaitlisted);
        Assert.False(reg.IsEnrolled);
        Assert.NotNull(reg.WaitlistPosition);
    }

    [Fact]
    public async Task CreateRegistration_Duplicate_IsRejected()
    {
        var h = BuildHarness();
        await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h)));
    }

    [Fact]
    public async Task CheckEligibility_MissingPlayerOrProgram_IsIneligible_FailClosed()
    {
        var h = BuildHarness();
        var result = await h.Service.CheckEligibilityAsync(h.TenantId, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public async Task CreateRegistration_IneligibleAge_ThrowsEligibilityException()
    {
        var h = BuildHarness();
        // A toddler is below the program's minimum age.
        var toddler = Player.Create(h.TenantId, h.GuardianId, "Tiny", "Doe", new DateOnly(2024, 1, 1), "female");
        h.Players.Store[toddler.Id] = toddler;

        await Assert.ThrowsAsync<EligibilityException>(() =>
            h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, toddler.Id, ProgramId(h)));
    }

    [Fact]
    public async Task CreatePaymentLink_Waitlisted_IsBlocked()
    {
        var h = BuildHarness(capacity: 1, existingEnrolled: 1);
        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));
        Assert.True(reg.IsWaitlisted);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreatePaymentLinkAsync(h.TenantId, reg.Id, false, "https://ok", "https://cancel"));
    }

    [Fact]
    public async Task CreatePaymentLink_NoStripeAccount_Throws()
    {
        var h = BuildHarness(withStripeAccount: false);
        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreatePaymentLinkAsync(h.TenantId, reg.Id, false, "https://ok", "https://cancel"));
    }

    [Fact]
    public async Task CreatePaymentLink_Eligible_ReturnsLink_AndMarksPending()
    {
        var h = BuildHarness();
        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));

        var link = await h.Service.CreatePaymentLinkAsync(h.TenantId, reg.Id, true, "https://ok", "https://cancel");

        Assert.False(string.IsNullOrEmpty(link.PaymentUrl));
        var stored = await h.Registrations.GetByIdAsync(h.TenantId, reg.Id);
        Assert.Equal(RegistrationStatus.PaymentPending, stored!.Status);
    }

    [Fact]
    public async Task PaymentFailsAfterWaiverSigned_LeavesRegistrationUnenrolled_FailClosed()
    {
        // Failure path: the guardian has signed the waiver, then the payment provider
        // is down. The registration must NOT progress to enrolled — it must remain in
        // a pre-payment state so a human/retry can resolve it. Never silent enrol.
        var h = BuildHarness();
        h.Stripe.FailCheckout = true;

        var reg = await h.Service.CreateRegistrationAsync(h.TenantId, h.GuardianId, h.PlayerId, ProgramId(h));
        reg.MarkWaiverSigned();
        await h.Registrations.UpdateAsync(reg);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreatePaymentLinkAsync(h.TenantId, reg.Id, false, "https://ok", "https://cancel"));

        var stored = await h.Registrations.GetByIdAsync(h.TenantId, reg.Id);
        Assert.False(stored!.IsEnrolled);
        Assert.False(stored.PaymentComplete);
        // CompleteEnrollment must still refuse because payment never completed.
        Assert.Throws<InvalidOperationException>(() => stored.CompleteEnrollment());
    }

    [Fact]
    public async Task MatchPrograms_ReturnsEligibleProgram()
    {
        var h = BuildHarness();
        var matches = await h.Service.MatchProgramsAsync(h.TenantId, h.PlayerId);

        Assert.Single(matches);
        Assert.Equal(EligibilityStatus.Eligible, matches[0].EligibilityResult.Status);
    }
}

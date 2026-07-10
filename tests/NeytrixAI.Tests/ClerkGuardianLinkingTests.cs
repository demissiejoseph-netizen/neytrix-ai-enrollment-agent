using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Api.Middleware;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Auth;
using NeytrixAI.Infrastructure.Services;
using NeytrixAI.Tests.Fakes;
using Xunit;
using DomainProgram = NeytrixAI.Domain.Entities.Program;

namespace NeytrixAI.Tests;

// Tests the OPTIONAL Clerk guardian linkage. Three things are asserted:
//   1. The anonymous flow is completely unchanged (guardian created with a NULL
//      clerk_user_id, exactly as before).
//   2. A first-time Clerk user's guardian row is created — and stamped with the
//      clerk_user_id — during the normal consent step of intake (never before
//      consent, so the fail-closed GDPR write gate is respected).
//   3. A returning Clerk user's session is linked to their existing guardian at
//      session start, and intake never creates a duplicate row.
public sealed class ClerkGuardianLinkingTests
{
    private sealed class Harness
    {
        public required AgentOrchestrationService Service { get; init; }
        public required InMemoryGuardianRepository Guardians { get; init; }
        public required InMemoryPlayerRepository Players { get; init; }
        public required Guid TenantId { get; init; }
    }

    private static Harness BuildHarness(ClerkIdentity? clerk = null)
    {
        var guardians = new InMemoryGuardianRepository();
        var players = new InMemoryPlayerRepository();
        var programs = new InMemoryProgramRepository();
        var registrations = new InMemoryRegistrationRepository();
        var tenants = new InMemoryTenantRepository();

        var tenant = Tenant.Create("acme", "Acme Sports");
        tenants.Store[tenant.Id] = tenant;
        var tenantId = tenant.Id;

        var program = DomainProgram.Create(
            tenantId, "Youth Soccer", "soccer",
            minAge: 6, maxAge: 14, capacity: 20, priceCents: 10000,
            startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 12, 1));
        programs.Store[program.Id] = program;

        var enrollment = new EnrollmentOrchestrationService(
            tenants, guardians, players, programs, registrations,
            new FakeStripeAdapter(), new FakeCalendarAdapter(), new EligibilityEngine());

        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext!.Items["TenantId"] = tenantId;
        if (clerk is not null)
            http.HttpContext.Items[ClerkAuthenticationMiddleware.ClerkIdentityItemKey] = clerk;

        var service = new AgentOrchestrationService(
            guardians, players, enrollment, http, NullLogger<AgentOrchestrationService>.Instance);

        return new Harness { Service = service, Guardians = guardians, Players = players, TenantId = tenantId };
    }

    private static Task<ChatMessageResponse?> Send(Harness h, string token, string content)
        => h.Service.ProcessMessageAsync(token, new SendMessageRequest(content), CancellationToken.None);

    private static async Task<string> WalkToConsentAndAgree(Harness h)
    {
        var start = await h.Service.StartSessionAsync(new StartSessionRequest(null), CancellationToken.None);
        var token = start.SessionToken;
        await Send(h, token, "Jane Doe");
        await Send(h, token, "jane@example.com");
        await Send(h, token, "skip");
        await Send(h, token, "yes"); // consent
        return token;
    }

    [Fact]
    public async Task AnonymousSession_CreatesGuardian_WithNullClerkId()
    {
        var h = BuildHarness(clerk: null);
        await WalkToConsentAndAgree(h);

        var guardian = Assert.Single(h.Guardians.Store.Values);
        Assert.Null(guardian.ClerkUserId);
        Assert.NotNull(guardian.GdprConsentedAt);
    }

    [Fact]
    public async Task FirstTimeClerkUser_HasNoRowBeforeConsent_ThenStampsClerkUserId()
    {
        var clerk = new ClerkIdentity("user_first", "clerk@example.com", "Clerky", "McNew");
        var h = BuildHarness(clerk);

        var start = await h.Service.StartSessionAsync(new StartSessionRequest(null), CancellationToken.None);
        var token = start.SessionToken;

        // Fail-closed GDPR: no guardian row is fabricated up front for a new Clerk user.
        Assert.Empty(h.Guardians.Store);
        Assert.Null((await h.Service.GetSessionStateAsync(token, CancellationToken.None))!.GuardianId);

        await Send(h, token, "Jane Doe");
        await Send(h, token, "jane@example.com");
        await Send(h, token, "skip");
        await Send(h, token, "yes"); // consent -> row created here

        var guardian = Assert.Single(h.Guardians.Store.Values);
        Assert.Equal("user_first", guardian.ClerkUserId);
        Assert.NotNull(guardian.GdprConsentedAt);

        // Session is now linked to the freshly-created guardian.
        var state = await h.Service.GetSessionStateAsync(token, CancellationToken.None);
        Assert.Equal(guardian.Id, state!.GuardianId);
    }

    [Fact]
    public async Task ReturningClerkUser_LinksSessionAtStart()
    {
        var h = BuildHarness(new ClerkIdentity("user_ret", null, null, null));
        var seeded = Guardian.Create(h.TenantId, "Existing", "Parent", "existing@example.com", clerkUserId: "user_ret");
        seeded.RecordGdprConsent();
        await h.Guardians.CreateAsync(seeded, CancellationToken.None);

        var start = await h.Service.StartSessionAsync(new StartSessionRequest(null), CancellationToken.None);
        var state = await h.Service.GetSessionStateAsync(start.SessionToken, CancellationToken.None);

        Assert.Equal(seeded.Id, state!.GuardianId);
    }

    [Fact]
    public async Task ReturningClerkUser_WalkingIntake_DoesNotCreateDuplicateGuardian()
    {
        var h = BuildHarness(new ClerkIdentity("user_ret", null, null, null));
        var seeded = Guardian.Create(h.TenantId, "Existing", "Parent", "existing@example.com", clerkUserId: "user_ret");
        seeded.RecordGdprConsent();
        await h.Guardians.CreateAsync(seeded, CancellationToken.None);

        var token = await WalkToConsentAndAgree(h);

        // Still exactly one guardian: the pre-existing one. No duplicate created and
        // it remains the linked guardian for the session.
        var only = Assert.Single(h.Guardians.Store.Values);
        Assert.Equal(seeded.Id, only.Id);
        Assert.Equal("user_ret", only.ClerkUserId);

        var reply = await Send(h, token, "Sam Doe"); // proceeds into player intake normally
        Assert.NotNull(reply);
        Assert.Equal(nameof(ConversationState.CollectingPlayerDob), reply!.NewState);
    }

    [Fact]
    public async Task GuardianRepository_ResolvesByClerkUserId_TenantScoped()
    {
        var h = BuildHarness();
        var g = Guardian.Create(h.TenantId, "Jane", "Doe", "jane@example.com", clerkUserId: "user_lookup");
        g.RecordGdprConsent();
        await h.Guardians.CreateAsync(g, CancellationToken.None);

        var found = await h.Guardians.GetByClerkUserIdAsync(h.TenantId, "user_lookup", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(g.Id, found!.Id);

        // Wrong tenant must not resolve it (tenant isolation).
        var otherTenant = await h.Guardians.GetByClerkUserIdAsync(Guid.NewGuid(), "user_lookup", CancellationToken.None);
        Assert.Null(otherTenant);

        // Unknown clerk id resolves to nothing.
        Assert.Null(await h.Guardians.GetByClerkUserIdAsync(h.TenantId, "user_absent", CancellationToken.None));
    }

    [Fact]
    public async Task CreatingClerkGuardian_WithoutConsent_StillFailsClosed()
    {
        // Stamping a clerk_user_id must NOT provide a way around the GDPR write gate.
        var h = BuildHarness();
        var g = Guardian.Create(h.TenantId, "Jane", "Doe", "jane@example.com", clerkUserId: "user_noconsent");
        // No RecordGdprConsent() call.

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Guardians.CreateAsync(g, CancellationToken.None));
    }
}

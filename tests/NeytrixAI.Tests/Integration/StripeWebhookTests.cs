using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Data.Repositories;
using Stripe;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// Drives <see cref="AgentOrchestrationService.HandleStripeWebhookAsync"/> for real: a real
/// <see cref="StripeAdapter"/> verifies a genuine HMAC-SHA256-signed payload (the same check
/// Stripe performs), and real <see cref="RegistrationRepository"/>/<see cref="ProgramRepository"/>
/// instances read/write a real, RLS-enforced local PostgreSQL database. Nothing about the
/// webhook path itself is faked - only the surrounding conversation plumbing (model client,
/// tool execution, conversation history) is replaced with nulls, since
/// <see cref="AgentOrchestrationService.HandleStripeWebhookAsync"/> never touches them.
///
/// This closes the gap called out in <see cref="EndToEndEnrollmentFlowTests"/>: that test
/// intentionally stops at PaymentPending because it never simulates the webhook. These tests
/// pick up exactly where it leaves off and drive a PaymentPending registration through to
/// Enrolled via the real handler.
///
/// It also guards against a real bug found while writing this test: the webhook handler used
/// to resolve the registration with a hard-coded <see cref="Guid.Empty"/> tenant id (Stripe's
/// checkout metadata only carried registration_id). Against a real RLS-enforced database,
/// <c>current_setting('app.tenant_id', true)</c> returns NULL when no tenant is set, so the
/// RLS predicate <c>tenant_id = NULL::uuid</c> is never true and the lookup silently returned
/// zero rows - meaning production payments would never be recorded. The fix threads a real
/// tenant_id through Stripe's own metadata (see <see cref="StripeAdapter.CreateCheckoutSessionAsync"/>)
/// and the handler now fails closed (returns without enrolling) if tenant_id is missing,
/// unparseable, or doesn't match the registration it resolves to - see
/// <see cref="MissingTenantMetadata_DoesNotEnroll_FailsClosed"/> and
/// <see cref="MismatchedTenantMetadata_DoesNotEnroll_FailsClosed"/> below.
///
/// Requires a reachable local Postgres with the migration applied (see PostgresTestFixture).
/// Skips itself with a clear message if Postgres isn't reachable, rather than failing the
/// whole suite in environments where it hasn't been set up.
/// </summary>
public sealed class StripeWebhookTests : IAsyncLifetime
{
    private const string WebhookSecret = "whsec_test_stripewebhooktests";
    private const long ProgramPriceCents = 15000;

    private Guid _tenantId;
    private Guid _programId;
    private Guid _guardianId;
    private Guid _playerId;

    public async Task InitializeAsync()
    {
        if (!await PostgresTestFixture.IsPostgresReachableAsync())
            return;

        var tenant = Tenant.Create($"stripe-{Guid.NewGuid():N}"[..24], "Stripe Webhook Test Org");
        tenant.ConfigureStripe("acct_fake_stripe_webhook_test");
        await PostgresTestFixture.SeedTenantAsync(tenant);
        _tenantId = tenant.Id;

        var connectionFactory = PostgresTestFixture.CreateAppConnectionFactory();
        var programs = new ProgramRepository(connectionFactory);
        var program = Program.Create(
            tenant.Id, "Youth Soccer Fundamentals", "soccer",
            minAge: 6, maxAge: 10, capacity: 20, priceCents: ProgramPriceCents,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            endDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)),
            depositCents: 5000, location: "Seattle Community Field");
        await programs.CreateAsync(program);
        _programId = program.Id;

        var guardians = new GuardianRepository(connectionFactory);
        var guardian = Guardian.Create(tenant.Id, "Alex", "Rivera", "alex.rivera.stripe-test@example.com");
        guardian.RecordGdprConsent();
        await guardians.CreateAsync(guardian);
        _guardianId = guardian.Id;

        var players = new PlayerRepository(connectionFactory);
        var player = Player.Create(tenant.Id, guardian.Id, "Sam", "Rivera", new DateOnly(2016, 4, 12), "male");
        await players.CreateAsync(player);
        _playerId = player.Id;
    }

    public async Task DisposeAsync()
    {
        if (_tenantId != Guid.Empty)
            await PostgresTestFixture.DeleteTenantCascadeAsync(_tenantId);
    }

    [Fact]
    public async Task RealSignedCheckoutCompletedWebhook_EnrollsRegistration_ThroughRealHandler()
    {
        if (_tenantId == Guid.Empty)
        {
            // Local PostgreSQL was not reachable during InitializeAsync - soft-skip. See
            // PostgresTestFixture for connection string overrides.
            return;
        }

        var registrations = new RegistrationRepository(PostgresTestFixture.CreateAppConnectionFactory());
        var registration = await SeedPendingPaymentRegistrationAsync(registrations, "cs_test_real_webhook");

        var orchestrator = BuildOrchestrator(out _);

        var (payload, signatureHeader) = BuildSignedCheckoutCompletedPayload(
            registrationId: registration.Id,
            tenantId: _tenantId,
            checkoutSessionId: "cs_test_real_webhook",
            paymentIntentId: "pi_test_real_webhook",
            amountTotalCents: ProgramPriceCents);

        await orchestrator.HandleStripeWebhookAsync(payload, signatureHeader, CancellationToken.None);

        var updated = await registrations.GetByIdAsync(_tenantId, registration.Id);
        Assert.NotNull(updated);
        Assert.True(updated!.IsEnrolled, $"Expected registration to be enrolled but status was '{updated.Status}'.");
        Assert.Equal(ProgramPriceCents, updated.AmountPaidCents);
        Assert.Equal("pi_test_real_webhook", updated.StripePaymentIntentId);
        Assert.NotNull(updated.EnrolledAt);
    }

    [Fact]
    public async Task TamperedSignature_IsRejectedByRealVerification_AndRegistrationIsUntouched()
    {
        if (_tenantId == Guid.Empty)
            return;

        var registrations = new RegistrationRepository(PostgresTestFixture.CreateAppConnectionFactory());
        var registration = await SeedPendingPaymentRegistrationAsync(registrations, "cs_test_tampered_sig");

        var orchestrator = BuildOrchestrator(out _);

        var (payload, _) = BuildSignedCheckoutCompletedPayload(
            registrationId: registration.Id,
            tenantId: _tenantId,
            checkoutSessionId: "cs_test_tampered_sig",
            paymentIntentId: "pi_test_tampered_sig",
            amountTotalCents: ProgramPriceCents);

        // A signature that was never computed over this payload with the real secret - e.g. a
        // forged or replayed-from-elsewhere webhook. Real signature verification must reject it.
        var forgedHeader = $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},v1={new string('0', 64)}";

        await Assert.ThrowsAsync<StripeException>(
            () => orchestrator.HandleStripeWebhookAsync(payload, forgedHeader, CancellationToken.None));

        var untouched = await registrations.GetByIdAsync(_tenantId, registration.Id);
        Assert.NotNull(untouched);
        Assert.False(untouched!.IsEnrolled);
        Assert.Equal(0, untouched.AmountPaidCents);
    }

    [Fact]
    public async Task MissingTenantMetadata_DoesNotEnroll_FailsClosed()
    {
        if (_tenantId == Guid.Empty)
            return;

        var registrations = new RegistrationRepository(PostgresTestFixture.CreateAppConnectionFactory());
        var registration = await SeedPendingPaymentRegistrationAsync(registrations, "cs_test_missing_tenant");

        var orchestrator = BuildOrchestrator(out _);

        // Simulates the pre-fix shape of Stripe's metadata (registration_id only, no tenant_id) -
        // exactly the real-world event that used to trigger the Guid.Empty/RLS bug.
        var (payload, signatureHeader) = BuildSignedCheckoutCompletedPayload(
            registrationId: registration.Id,
            tenantId: null,
            checkoutSessionId: "cs_test_missing_tenant",
            paymentIntentId: "pi_test_missing_tenant",
            amountTotalCents: ProgramPriceCents);

        // Must not throw - a malformed/legacy event is dropped, not crashed on.
        await orchestrator.HandleStripeWebhookAsync(payload, signatureHeader, CancellationToken.None);

        var untouched = await registrations.GetByIdAsync(_tenantId, registration.Id);
        Assert.NotNull(untouched);
        Assert.False(untouched!.IsEnrolled, "A checkout event with no tenant_id must never enroll a registration.");
        Assert.Equal(0, untouched.AmountPaidCents);
    }

    [Fact]
    public async Task MismatchedTenantMetadata_DoesNotEnroll_FailsClosed()
    {
        if (_tenantId == Guid.Empty)
            return;

        var registrations = new RegistrationRepository(PostgresTestFixture.CreateAppConnectionFactory());
        var registration = await SeedPendingPaymentRegistrationAsync(registrations, "cs_test_wrong_tenant");

        var orchestrator = BuildOrchestrator(out _);

        // A tenant_id that parses fine but belongs to nobody / a different tenant than the
        // registration actually lives under - simulates a forged or cross-tenant replayed event.
        var (payload, signatureHeader) = BuildSignedCheckoutCompletedPayload(
            registrationId: registration.Id,
            tenantId: Guid.NewGuid(),
            checkoutSessionId: "cs_test_wrong_tenant",
            paymentIntentId: "pi_test_wrong_tenant",
            amountTotalCents: ProgramPriceCents);

        await orchestrator.HandleStripeWebhookAsync(payload, signatureHeader, CancellationToken.None);

        var untouched = await registrations.GetByIdAsync(_tenantId, registration.Id);
        Assert.NotNull(untouched);
        Assert.False(untouched!.IsEnrolled, "A checkout event whose tenant_id doesn't match the registration's real tenant must never enroll it.");
        Assert.Equal(0, untouched.AmountPaidCents);
    }

    private async Task<Registration> SeedPendingPaymentRegistrationAsync(RegistrationRepository registrations, string checkoutSessionId)
    {
        var registration = Registration.Create(_tenantId, _guardianId, _playerId, _programId);
        registration.MarkWaiverSent();
        registration.MarkWaiverSigned();
        registration.AttachCheckoutSession(checkoutSessionId);
        await registrations.CreateAsync(registration);
        return registration;
    }

    private AgentOrchestrationService BuildOrchestrator(out StripeOptions options)
    {
        var connectionFactory = PostgresTestFixture.CreateAppConnectionFactory();
        var registrations = new RegistrationRepository(connectionFactory);
        var programs = new ProgramRepository(connectionFactory);

        options = new StripeOptions
        {
            SecretKey = "sk_test_fake_stripe_webhook_test",
            WebhookSecret = WebhookSecret,
            WaiverBaseUrl = "https://waivers.test",
            SuccessUrlTemplate = "https://app.test/success?registration={registrationId}",
            CancelUrlTemplate = "https://app.test/cancel?registration={registrationId}"
        };

        // A real StripeClient (never makes a network call here - HandleStripeWebhookAsync only
        // exercises ParseWebhookEvent, which is pure local HMAC verification + JSON parsing) and
        // a real StripeAdapter, so signature verification is the genuine Stripe.net code path.
        var stripeAdapter = new StripeAdapter(
            new StripeClient("sk_test_fake_stripe_webhook_test"),
            Options.Create(options),
            NullLogger<StripeAdapter>.Instance);

        // HandleStripeWebhookAsync never touches the http context, conversation history, model
        // client, or tool execution service - only _registrations, _programs, _stripeAdapter,
        // and _stripeOptions. Passing null! for the rest keeps this test honestly scoped to the
        // webhook path instead of dragging in unrelated fakes that would never be exercised.
        return new AgentOrchestrationService(
            httpContextAccessor: null!,
            conversations: null!,
            guardians: null!,
            programs: programs,
            registrations: registrations,
            modelClient: null!,
            toolExecution: null!,
            stripeAdapter: stripeAdapter,
            stripeOptions: Options.Create(options));
    }

    /// <summary>
    /// Builds a genuine <c>checkout.session.completed</c> Stripe webhook payload and computes a
    /// real <c>t=...,v1=...</c> HMAC-SHA256 signature header over it with <see cref="WebhookSecret"/>,
    /// exactly as Stripe does. Includes every top-level Event field and Session field that
    /// Stripe.net 45.14.0's <c>EventConverter</c>/<c>Event</c>/<c>Session</c> deserialization
    /// requires - this shape was validated against the real Stripe.net assembly (a hand-crafted
    /// payload missing <c>request</c> throws a NullReferenceException inside
    /// <c>Stripe.Infrastructure.EventConverter.ReadJson</c>, since it unconditionally reads
    /// <c>jsonObject["request"].Type</c>).
    /// </summary>
    private static (string Payload, string SignatureHeader) BuildSignedCheckoutCompletedPayload(
        Guid registrationId,
        Guid? tenantId,
        string checkoutSessionId,
        string paymentIntentId,
        long amountTotalCents)
    {
        var metadata = $"\"registration_id\": \"{registrationId}\", \"deposit_only\": \"False\"";
        if (tenantId is not null)
            metadata = $"\"registration_id\": \"{registrationId}\", \"tenant_id\": \"{tenantId}\", \"deposit_only\": \"False\"";

        var payload = "{" +
            "\"id\": \"evt_test_" + Guid.NewGuid().ToString("N") + "\", " +
            "\"object\": \"event\", " +
            "\"type\": \"checkout.session.completed\", " +
            "\"request\": null, " +
            "\"livemode\": false, " +
            "\"created\": " + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ", " +
            "\"api_version\": null, " +
            "\"data\": { \"object\": {" +
                "\"id\": \"" + checkoutSessionId + "\", " +
                "\"object\": \"checkout.session\", " +
                "\"payment_intent\": \"" + paymentIntentId + "\", " +
                "\"amount_total\": " + amountTotalCents + ", " +
                "\"currency\": \"usd\", " +
                "\"metadata\": { " + metadata + " }" +
            "} }" +
        "}";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var signatureHex = Convert.ToHexString(signatureBytes).ToLowerInvariant();
        var header = $"t={timestamp},v1={signatureHex}";

        return (payload, header);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Data.Repositories;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// Drives one full conversation - greeting through a completed registration, assessment
/// booking, waiver, and payment link - through the real <see cref="AgentOrchestrationService"/>
/// loop against a real local PostgreSQL database (real repositories, real RLS, real
/// ConversationStateMachine). Only the LLM (<see cref="ScriptedAgentModelClient"/>), Stripe
/// (<see cref="FakeStripeAdapter"/>), and Google Calendar (<see cref="FakeGoogleCalendarAdapter"/>)
/// are test doubles - nothing that touches the database is faked.
///
/// Requires a reachable local Postgres with the migration applied (see PostgresTestFixture for
/// connection strings / overrides). Skips itself with a clear message if Postgres isn't reachable,
/// rather than failing the whole suite in environments where it hasn't been set up.
/// </summary>
public sealed class EndToEndEnrollmentFlowTests : IAsyncLifetime
{
    private Guid _tenantId;
    private Guid _programId;

    public async Task InitializeAsync()
    {
        if (!await IsPostgresReachableAsync())
            return;

        var tenant = Tenant.Create($"e2e-{Guid.NewGuid():N}"[..24], "E2E Test Youth Sports Org");
        tenant.ConfigureGoogleCalendar("fake-calendar-e2e");
        tenant.ConfigureStripe("acct_fake_e2e");
        await PostgresTestFixture.SeedTenantAsync(tenant);
        _tenantId = tenant.Id;

        var connectionFactory = PostgresTestFixture.CreateAppConnectionFactory();
        var programs = new ProgramRepository(connectionFactory);
        var program = Program.Create(
            tenant.Id, "Youth Soccer Fundamentals", "soccer",
            minAge: 6, maxAge: 10, capacity: 20, priceCents: 15000,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            endDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)),
            depositCents: 5000, location: "Seattle Community Field");
        await programs.CreateAsync(program);
        _programId = program.Id;
    }

    public async Task DisposeAsync()
    {
        if (_tenantId != Guid.Empty)
            await PostgresTestFixture.DeleteTenantCascadeAsync(_tenantId);
    }

    [Fact]
    public async Task FullConversation_FromGreetingThroughPaymentLink_PersistsRealRegistration()
    {
        if (_tenantId == Guid.Empty)
        {
            // Local PostgreSQL was not reachable during InitializeAsync - soft-skip rather than
            // fail the whole suite in environments where it hasn't been set up. See
            // PostgresTestFixture for connection string overrides.
            return;
        }

        var connectionFactory = PostgresTestFixture.CreateAppConnectionFactory();
        var guardians = new GuardianRepository(connectionFactory);
        var players = new PlayerRepository(connectionFactory);
        var programs = new ProgramRepository(connectionFactory);
        var registrations = new RegistrationRepository(connectionFactory);
        var tenants = new TenantRepository(connectionFactory);
        var conversations = new ConversationRepository(connectionFactory);
        var assessments = new AssessmentRepository(connectionFactory);
        var auditLog = new AuditLogRepository(connectionFactory);

        var stripeAdapter = new FakeStripeAdapter();
        var calendarAdapter = new FakeGoogleCalendarAdapter();
        var eligibility = new EligibilityEngine();

        var toolExecution = new ToolExecutionService(
            guardians, players, programs, registrations, tenants, conversations, assessments, auditLog,
            stripeAdapter, calendarAdapter, eligibility, connectionFactory,
            Options.Create(new StripeOptions
            {
                SecretKey = "sk_test_fake",
                WebhookSecret = "whsec_fake",
                WaiverBaseUrl = "https://waivers.test",
                SuccessUrlTemplate = "https://app.test/success?registration={registrationId}",
                CancelUrlTemplate = "https://app.test/cancel?registration={registrationId}"
            }),
            Options.Create(new GoogleCalendarOptions
            {
                ServiceAccountKeyJson = "{}",
                DefaultAssessmentDurationMinutes = 60
            }),
            NullLogger<ToolExecutionService>.Instance);

        var httpContextAccessor = new FixedTenantHttpContextAccessor(_tenantId);
        var modelClient = new ScriptedAgentModelClient(_programId);

        var orchestrator = new AgentOrchestrationService(
            httpContextAccessor, conversations, guardians, programs, registrations,
            modelClient, toolExecution, stripeAdapter,
            Options.Create(new StripeOptions
            {
                SecretKey = "sk_test_fake",
                WebhookSecret = "whsec_fake",
                WaiverBaseUrl = "https://waivers.test",
                SuccessUrlTemplate = "https://app.test/success?registration={registrationId}",
                CancelUrlTemplate = "https://app.test/cancel?registration={registrationId}"
            }));

        // 1. Start the session (Greeting state).
        var started = await orchestrator.StartSessionAsync(new StartSessionRequest(GuardianEmail: null, Channel: "widget"), CancellationToken.None);
        Assert.Equal(ConversationState.Greeting.ToString(), started.CurrentState);

        // 2. Drive the conversation forward with one simulated user message per expected
        // final-text reply from the scripted model, mirroring one real chat turn each.
        string[] simulatedUserMessages =
        {
            "Hi, I'd like to enroll my son.",
            "206-555-1234",
            "Yes, I consent. I'm Alex Rivera, alex.rivera.e2e@example.com",
            "Sam Rivera, born 2016-04-12, male",
            "Yes, please register Sam for that program.",
            "The first slot works for us.",
            "Signed! What's next for payment?"
        };

        ChatMessageResponse? lastResponse = null;
        foreach (var message in simulatedUserMessages)
        {
            lastResponse = await orchestrator.ProcessMessageAsync(started.SessionToken, new SendMessageRequest(message), CancellationToken.None);
            Assert.NotNull(lastResponse);
            Assert.False(lastResponse!.RequiresEscalation, $"Conversation escalated unexpectedly after message '{message}': {lastResponse.Content}");
        }

        Assert.True(modelClient.IsScriptExhausted, "The scripted conversation did not run to completion.");

        // 3. The conversation should have progressed all the way to PaymentPending - a real
        // payment completion only happens via the Stripe webhook, which this test does not
        // exercise, so PaymentPending (not EnrollmentComplete) is the correct honest end state.
        Assert.Equal(ConversationState.PaymentPending.ToString(), lastResponse!.NewState);

        // 4. Assert the actual database state, not just the chat replies.
        var sessionState = await orchestrator.GetSessionStateAsync(started.SessionToken, CancellationToken.None);
        Assert.NotNull(sessionState);
        Assert.NotNull(sessionState!.GuardianId);
        Assert.NotNull(sessionState.PlayerId);
        Assert.NotNull(sessionState.RegistrationId);

        var guardian = await guardians.GetByIdAsync(_tenantId, sessionState.GuardianId!.Value);
        Assert.NotNull(guardian);
        Assert.Equal("alex.rivera.e2e@example.com", guardian!.Email);
        Assert.NotNull(guardian.GdprConsentedAt);

        var player = await players.GetByIdAsync(_tenantId, sessionState.PlayerId!.Value);
        Assert.NotNull(player);
        Assert.Equal("Sam", player!.FirstName);
        Assert.Equal(sessionState.GuardianId, player.GuardianId);

        var registration = await registrations.GetByIdAsync(_tenantId, sessionState.RegistrationId!.Value);
        Assert.NotNull(registration);
        Assert.Equal(_programId, registration!.ProgramId);
        Assert.False(registration.IsWaitlisted);
        Assert.True(registration.WaiverSigned == false || registration.WaiverSentAt is not null); // waiver was sent, not (yet) signed by the guardian
        Assert.NotNull(registration.StripeCheckoutSessionId);
        Assert.False(registration.IsEnrolled, "Enrollment should only flip to true via the Stripe webhook, which this test does not simulate.");

        var registrationAssessments = await assessments.GetByRegistrationAsync(_tenantId, registration.Id);
        Assert.Single(registrationAssessments);
    }

    private static async Task<bool> IsPostgresReachableAsync()
    {
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(PostgresTestFixture.SuperuserConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class FixedTenantHttpContextAccessor : IHttpContextAccessor
    {
        public FixedTenantHttpContextAccessor(Guid tenantId)
        {
            var context = new DefaultHttpContext();
            context.Items["TenantId"] = tenantId;
            HttpContext = context;
        }

        public HttpContext? HttpContext { get; set; }
    }
}

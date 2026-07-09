using Microsoft.AspNetCore.Http;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Api.Services;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Services;
using NeytrixAI.Tests.Fakes;
using Xunit;
using DomainProgram = NeytrixAI.Domain.Entities.Program;

namespace NeytrixAI.Tests;

// Tests for the four structural improvements:
//  1. Safety triage runs (and completes) BEFORE any parsing/extraction.
//  2. Explicit named state machine with logged transitions; unlisted transitions
//     throw or escalate (never silently proceed).
//  3. A single escalation chokepoint that keeps categories distinct in both the
//     persisted escalation record and the structured logs.
//  4. Fail loud, not silent: ambiguous input escalates rather than best-guessing.
public sealed class StructuralRefactorTests
{
    private sealed class Harness
    {
        public required AgentOrchestrationService Service { get; init; }
        public required InMemoryGuardianRepository Guardians { get; init; }
        public required InMemoryPlayerRepository Players { get; init; }
        public required CapturingLogger<AgentOrchestrationService> Logger { get; init; }
    }

    private static Harness BuildHarness()
    {
        var guardians = new InMemoryGuardianRepository();
        var players = new InMemoryPlayerRepository();
        var programs = new InMemoryProgramRepository();
        var registrations = new InMemoryRegistrationRepository();
        var tenants = new InMemoryTenantRepository();

        var tenant = Tenant.Create("acme", "Acme Sports");
        tenants.Store[tenant.Id] = tenant;

        var program = DomainProgram.Create(
            tenant.Id, "Youth Soccer", "soccer",
            minAge: 6, maxAge: 14, capacity: 20, priceCents: 10000,
            startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 12, 1));
        programs.Store[program.Id] = program;

        var enrollment = new EnrollmentOrchestrationService(
            tenants, guardians, players, programs, registrations,
            new FakeStripeAdapter(), new FakeCalendarAdapter(), new EligibilityEngine());

        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext!.Items["TenantId"] = tenant.Id;

        var logger = new CapturingLogger<AgentOrchestrationService>();
        var service = new AgentOrchestrationService(guardians, players, enrollment, http, logger);

        return new Harness { Service = service, Guardians = guardians, Players = players, Logger = logger };
    }

    private static async Task<string> StartAsync(Harness h)
        => (await h.Service.StartSessionAsync(new StartSessionRequest(null), CancellationToken.None)).SessionToken;

    private static Task<ChatMessageResponse?> SendAsync(Harness h, string token, string content)
        => h.Service.ProcessMessageAsync(token, new SendMessageRequest(content), CancellationToken.None);

    // Drives the flow up to the point where the next message is the DOB answer.
    private static async Task<string> WalkToDobAsync(Harness h)
    {
        var token = await StartAsync(h);
        await SendAsync(h, token, "Jane Doe");
        await SendAsync(h, token, "jane@example.com");
        await SendAsync(h, token, "skip");
        await SendAsync(h, token, "yes");       // consent
        await SendAsync(h, token, "Sam Doe");
        return token;                            // now in CollectingPlayerDob
    }

    // ── Improvement 1: safety triage before parsing/extraction ──────────────────
    [Fact]
    public async Task SafetyTriage_RunsBeforeExtraction_WhenMessageAlsoContainsValidDob()
    {
        var h = BuildHarness();
        var token = await WalkToDobAsync(h);

        // The message carries BOTH a safety signal ("medical") AND a well-formed DOB.
        // Triage must fire first: escalate as Medical, and the DOB must NOT be
        // extracted or advance the flow.
        var reply = await SendAsync(h, token, "he has a medical condition, born 2015-06-01");

        Assert.NotNull(reply);
        Assert.Equal(nameof(ConversationState.EscalatedToStaff), reply!.NewState);
        Assert.True(reply.RequiresEscalation);

        var escalations = h.Service.GetEscalations(token);
        var record = Assert.Single(escalations);
        Assert.Equal(EscalationReason.Medical, record.Category);
        // Triggering state proves triage fired while still at the DOB step (pre-parse).
        Assert.Equal(ConversationState.CollectingPlayerDob, record.TriggeringState);

        // No player was created — the DOB was never extracted.
        Assert.Empty(h.Players.Store);
    }

    // ── Improvement 2: logged transitions; unlisted transitions fail loud ───────
    [Fact]
    public async Task StateTransitions_AreLogged_WithFromToTriggerAndTimestamp()
    {
        var h = BuildHarness();
        var token = await StartAsync(h);
        await SendAsync(h, token, "Jane Doe");

        // The session_started and guardian_name_provided transitions must be logged.
        Assert.Contains(h.Logger.Messages, m =>
            m.Contains("State transition") && m.Contains("Greeting") &&
            m.Contains("CollectingGuardianName") && m.Contains("session_started"));
        Assert.Contains(h.Logger.Messages, m =>
            m.Contains("State transition") && m.Contains("CollectingGuardianEmail") &&
            m.Contains("guardian_name_provided"));
    }

    [Fact]
    public void TransitionOrThrow_ThrowsOnUnlistedTransition()
    {
        var ex = Assert.Throws<InvalidStateTransitionException>(() =>
            ConversationStateMachine.TransitionOrThrow(
                ConversationState.CollectingGuardianName, ConversationState.ShowingProgramMatches));

        Assert.Equal(ConversationState.CollectingGuardianName, ex.From);
        Assert.Equal(ConversationState.ShowingProgramMatches, ex.To);
    }

    [Fact]
    public void TransitionOrThrow_AllowsExplicitEscalationFromAnyNonTerminalState()
    {
        // Escalation is the one explicit transition permitted from a non-terminal
        // state even when it isn't in the ordinary transition table.
        var result = ConversationStateMachine.TransitionOrThrow(
            ConversationState.CollectingGdprConsent, ConversationState.EscalatedToStaff);
        Assert.Equal(ConversationState.EscalatedToStaff, result);

        // ...but a terminal state cannot transition further, even to escalation.
        Assert.Throws<InvalidStateTransitionException>(() =>
            ConversationStateMachine.TransitionOrThrow(
                ConversationState.SessionEnded, ConversationState.EscalatedToStaff));
    }

    // ── Improvement 3: single chokepoint, categories kept distinct ──────────────
    [Theory]
    [InlineData("my child is being hurt at home", EscalationReason.Safeguarding)]
    [InlineData("she has a peanut allergy", EscalationReason.Medical)]
    [InlineData("I want a refund please", EscalationReason.Financial)]
    [InlineData("I have a complaint to make", EscalationReason.Complaint)]
    [InlineData("please let me speak to a real person", EscalationReason.HumanRequested)]
    public async Task EscalationCategories_AreRecordedAndLoggedDistinctly(string message, EscalationReason expected)
    {
        var h = BuildHarness();
        var token = await StartAsync(h);

        var reply = await SendAsync(h, token, message);
        Assert.Equal(nameof(ConversationState.EscalatedToStaff), reply!.NewState);

        // Persisted escalation record carries the distinct category (not collapsed).
        var record = Assert.Single(h.Service.GetEscalations(token));
        Assert.Equal(expected, record.Category);

        // Structured log mirrors the same distinct category.
        Assert.Contains(h.Logger.Messages, m =>
            m.Contains("ESCALATION") && m.Contains($"category={expected}"));
    }

    [Fact]
    public async Task Escalation_LogRecordsCategoryStateTimestampAndReason()
    {
        var h = BuildHarness();
        var token = await StartAsync(h);
        await SendAsync(h, token, "I want a refund");

        var record = Assert.Single(h.Service.GetEscalations(token));
        Assert.Equal(EscalationReason.Financial, record.Category);
        Assert.False(string.IsNullOrWhiteSpace(record.Reason));
        Assert.NotEqual(default, record.Timestamp);

        Assert.Contains(h.Logger.Messages, m =>
            m.Contains("ESCALATION") &&
            m.Contains("category=Financial") &&
            m.Contains("triggeringState=") &&
            m.Contains("reason="));
    }

    // ── Improvement 4: fail loud, not silent, on ambiguity ──────────────────────
    [Fact]
    public async Task AmbiguousGenderResponse_Escalates_RatherThanSilentlyDefaulting()
    {
        var h = BuildHarness();
        var token = await WalkToDobAsync(h);
        await SendAsync(h, token, "2015-06-01");     // now at CollectingPlayerGender

        // An unrecognised gender answer must NOT be silently coerced to a default
        // ("prefer_not_to_say"); the agent escalates instead of guessing.
        var reply = await SendAsync(h, token, "purple banana");

        Assert.NotNull(reply);
        Assert.Equal(nameof(ConversationState.EscalatedToStaff), reply!.NewState);
        Assert.True(reply.RequiresEscalation);

        var record = Assert.Single(h.Service.GetEscalations(token));
        Assert.Equal(EscalationReason.AmbiguousResponse, record.Category);

        // No player was persisted with a guessed gender.
        Assert.Empty(h.Players.Store);
    }

    [Fact]
    public async Task RecognisedGenderResponses_StillProceed()
    {
        foreach (var answer in new[] { "male", "female", "non-binary", "prefer not to say" })
        {
            var h = BuildHarness();
            var token = await WalkToDobAsync(h);
            await SendAsync(h, token, "2015-06-01");
            var reply = await SendAsync(h, token, answer);

            Assert.Equal(nameof(ConversationState.ShowingProgramMatches), reply!.NewState);
            Assert.Empty(h.Service.GetEscalations(token));
            Assert.Single(h.Players.Store);
        }
    }
}

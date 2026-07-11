using NeytrixAI.Domain.Services;
using Xunit;

namespace NeytrixAI.Tests;

public class ConversationStateMachineTests
{
    [Fact]
    public void CannotSkipConsentStep()
    {
        // Jumping straight from name collection to program matches is not allowed.
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianName,
            ConversationState.ShowingProgramMatches);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CannotSkipConsentFromPhoneToPlayerIntake()
    {
        // GDPR consent is mandatory: phone collection must route through consent,
        // never directly into player intake. This closes a fail-open hole where a
        // driven transition could bypass consent.
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianPhone,
            ConversationState.CollectingPlayerName);

        Assert.False(result.IsValid);

        // The only permitted onward transition is to consent collection.
        Assert.True(ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianPhone,
            ConversationState.CollectingGdprConsent).IsValid);
    }

    [Fact]
    public void CannotSkipPlayerIntakeToEnrollment()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingPlayerName,
            ConversationState.EnrollmentComplete);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AllowsDefinedGuardianIntakeSequence()
    {
        Assert.True(ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianName,
            ConversationState.CollectingGuardianEmail).IsValid);

        Assert.True(ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianEmail,
            ConversationState.CollectingGuardianPhone).IsValid);
    }

    [Fact]
    public void ConsentCanEndSession()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGdprConsent,
            ConversationState.SessionEnded);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EnrollmentRequiresPaymentPendingPath()
    {
        Assert.True(ConversationStateMachine.Transition(
            ConversationState.PaymentPending,
            ConversationState.EnrollmentComplete).IsValid);

        // But cannot go straight from waiver to enrollment.
        Assert.False(ConversationStateMachine.Transition(
            ConversationState.SendingWaiver,
            ConversationState.EnrollmentComplete).IsValid);
    }

    [Fact]
    public void TerminalStatesAreTerminal()
    {
        Assert.True(ConversationStateMachine.IsTerminal(ConversationState.SessionEnded));
        Assert.True(ConversationStateMachine.IsTerminal(ConversationState.EscalatedToStaff));
        Assert.False(ConversationStateMachine.IsTerminal(ConversationState.Greeting));
    }

    [Fact]
    public void EscalationIsReachableFromMostStates()
    {
        Assert.True(ConversationStateMachine.Transition(
            ConversationState.ShowingProgramMatches,
            ConversationState.EscalatedToStaff).IsValid);
    }

    [Fact]
    public void SessionEndedHasNoOnwardTransitions()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.SessionEnded,
            ConversationState.Greeting);

        Assert.False(result.IsValid);
    }
}

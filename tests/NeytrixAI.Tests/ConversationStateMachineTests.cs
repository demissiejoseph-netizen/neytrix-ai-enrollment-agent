using NeytrixAI.Domain.Services;
using Xunit;

namespace NeytrixAI.Tests;

public class ConversationStateMachineTests
{
    [Fact]
    public void CannotSkipConsentStep()
    {
        // Guardian phone -> player name directly (skipping consent) is a defined
        // path, but jumping straight from name collection to program matches is not.
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianName,
            ConversationState.ShowingProgramMatches);

        Assert.False(result.IsValid);
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

using NeytrixAI.Domain.Services;

namespace NeytrixAI.Tests;

/// <summary>
/// Covers GAP-16: guardians must pass through explicit GDPR consent before player intake
/// begins. Before the fix, CollectingGuardianPhone could transition directly past consent;
/// these tests pin the corrected transition table so any regression fails loudly.
/// </summary>
public class ConversationStateMachineTests
{
    [Fact]
    public void GuardianPhone_OnlyAllowsTransitionToGdprConsent()
    {
        var allowed = ConversationStateMachine.AllowedTransitions(ConversationState.CollectingGuardianPhone);

        Assert.Single(allowed);
        Assert.Contains(ConversationState.CollectingGdprConsent, allowed);
    }

    [Fact]
    public void GuardianPhone_CannotSkipConsentDirectlyToPlayerIntake()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGuardianPhone,
            ConversationState.CollectingPlayerName);

        Assert.False(result.IsValid);
        Assert.Equal(ConversationState.CollectingGuardianPhone, result.NewState);
        Assert.Contains("not permitted", result.ErrorMessage);
    }

    [Fact]
    public void GdprConsent_ToPlayerName_IsValid()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGdprConsent,
            ConversationState.CollectingPlayerName);

        Assert.True(result.IsValid);
        Assert.Equal(ConversationState.CollectingPlayerName, result.NewState);
    }

    [Fact]
    public void GdprConsent_CanEndSessionIfGuardianDeclines()
    {
        var result = ConversationStateMachine.Transition(
            ConversationState.CollectingGdprConsent,
            ConversationState.SessionEnded);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(ConversationState.SessionEnded)]
    [InlineData(ConversationState.EscalatedToStaff)]
    public void TerminalStates_AreReportedAsTerminal(ConversationState state)
    {
        Assert.True(ConversationStateMachine.IsTerminal(state));
    }

    [Fact]
    public void NonTerminalState_IsNotReportedAsTerminal()
    {
        Assert.False(ConversationStateMachine.IsTerminal(ConversationState.Greeting));
    }

    [Fact]
    public void EscalatedToStaff_RequiresEscalation()
    {
        Assert.True(ConversationStateMachine.RequiresEscalation(ConversationState.EscalatedToStaff));
        Assert.False(ConversationStateMachine.RequiresEscalation(ConversationState.Greeting));
    }

    [Fact]
    public void UnknownTransition_FromTerminalState_IsRejected()
    {
        // SessionEnded has no outgoing transitions at all.
        var result = ConversationStateMachine.Transition(ConversationState.SessionEnded, ConversationState.Greeting);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void EveryNonTerminalState_HasAtLeastOneOutgoingTransition()
    {
        foreach (var state in Enum.GetValues<ConversationState>())
        {
            if (ConversationStateMachine.IsTerminal(state))
                continue;

            var allowed = ConversationStateMachine.AllowedTransitions(state);
            Assert.True(allowed.Count > 0, $"{state} has no outgoing transitions and is not marked terminal.");
        }
    }
}

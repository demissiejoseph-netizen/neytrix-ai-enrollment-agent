using NeytrixAI.Domain.Services;
using Xunit;

namespace NeytrixAI.Tests;

public class SafetyTriageTests
{
    [Theory]
    [InlineData("my child was abused at practice", EscalationReason.Safeguarding)]
    [InlineData("this feels unsafe", EscalationReason.Safeguarding)]
    [InlineData("it's an emergency", EscalationReason.Safeguarding)]
    [InlineData("he has a peanut allergy", EscalationReason.Medical)]
    [InlineData("she takes medication daily", EscalationReason.Medical)]
    [InlineData("I want a refund", EscalationReason.Financial)]
    [InlineData("I'm filing a chargeback", EscalationReason.Financial)]
    [InlineData("I have a complaint", EscalationReason.Complaint)]
    [InlineData("I want to talk to a human", EscalationReason.HumanRequested)]
    [InlineData("can I speak to someone", EscalationReason.HumanRequested)]
    public void EscalatesOnSafetySignals(string message, EscalationReason expected)
    {
        var shouldEscalate = SafetyTriage.ShouldEscalate(message, out var reason);

        Assert.True(shouldEscalate);
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData("Jane Doe")]
    [InlineData("jane@example.com")]
    [InlineData("yes")]
    [InlineData("2015-06-01")]
    public void DoesNotEscalateOnNormalIntake(string message)
    {
        var shouldEscalate = SafetyTriage.ShouldEscalate(message, out var reason);

        Assert.False(shouldEscalate);
        Assert.Equal(EscalationReason.None, reason);
    }

    [Fact]
    public void EmptyMessageDoesNotEscalate()
    {
        Assert.False(SafetyTriage.ShouldEscalate("", out _));
        Assert.False(SafetyTriage.ShouldEscalate(null, out _));
    }
}

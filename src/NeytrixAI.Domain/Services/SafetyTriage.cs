namespace NeytrixAI.Domain.Services;

/// <summary>
/// Deterministic, fail-closed safety triage for guardian messages. If a message
/// contains any signal that a human should handle (safeguarding, medical,
/// financial dispute, or an explicit request for a human), the agent must
/// escalate rather than continue automated handling. This is intentionally
/// broad: false positives cost a staff review; false negatives risk a child's
/// safety, so we err toward escalation.
/// </summary>
public static class SafetyTriage
{
    private static readonly (string Keyword, EscalationReason Reason)[] Signals =
    {
        ("abuse", EscalationReason.Safeguarding),
        ("unsafe", EscalationReason.Safeguarding),
        ("hurt", EscalationReason.Safeguarding),
        ("harm", EscalationReason.Safeguarding),
        ("safeguard", EscalationReason.Safeguarding),
        ("emergency", EscalationReason.Safeguarding),
        ("allerg", EscalationReason.Medical),
        ("medical", EscalationReason.Medical),
        ("medication", EscalationReason.Medical),
        ("disab", EscalationReason.Medical),
        ("injur", EscalationReason.Medical),
        ("refund", EscalationReason.Financial),
        ("chargeback", EscalationReason.Financial),
        ("dispute", EscalationReason.Financial),
        ("complaint", EscalationReason.Complaint),
        ("lawyer", EscalationReason.Complaint),
        ("human", EscalationReason.HumanRequested),
        ("agent", EscalationReason.HumanRequested),
        ("speak to someone", EscalationReason.HumanRequested),
        ("real person", EscalationReason.HumanRequested),
    };

    public static bool ShouldEscalate(string? message, out EscalationReason reason)
    {
        reason = EscalationReason.None;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized = message.ToLowerInvariant();
        foreach (var (keyword, signalReason) in Signals)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal))
            {
                reason = signalReason;
                return true;
            }
        }

        return false;
    }
}

public enum EscalationReason
{
    None,
    Safeguarding,
    Medical,
    Financial,
    Complaint,
    HumanRequested
}

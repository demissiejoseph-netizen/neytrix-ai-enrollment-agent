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

/// <summary>
/// The category of a human hand-off. Categories are kept distinct end-to-end
/// (structured logs and the persisted <see cref="EscalationRecord"/>) so that
/// safeguarding, medical, billing, consent, ambiguity and technical failures can
/// be triaged and audited separately — they are never collapsed into one generic
/// bucket.
/// </summary>
public enum EscalationReason
{
    None,
    Safeguarding,
    Medical,
    Financial,
    Complaint,
    HumanRequested,
    // Consent-related hand-off (kept distinct for auditing; the consent-refusal
    // flow itself ends the session rather than escalating, by design).
    Consent,
    // The guardian's response was ambiguous / low-confidence and the agent must
    // not guess — it escalates instead of silently proceeding with a best guess.
    AmbiguousResponse,
    // An unexpected/malformed internal condition (invalid state transition,
    // unhandled exception, unexpected API result). Fail loud, not silent.
    TechnicalFailure
}

/// <summary>
/// An audit record for a single human hand-off. Persisted per session (in-memory
/// for the single-instance MVP) and mirrored into structured logs so every
/// escalation is reviewable with its category, the state it fired from, when it
/// happened, and a human-readable reason.
/// </summary>
public sealed record EscalationRecord(
    EscalationReason Category,
    ConversationState TriggeringState,
    DateTimeOffset Timestamp,
    string Reason);

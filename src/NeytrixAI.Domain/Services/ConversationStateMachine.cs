namespace NeytrixAI.Domain.Services;

/// <summary>
/// Implements Workflows A-G from the PRD as a deterministic state machine.
/// State transitions are enforced here; LLM cannot skip steps.
/// </summary>
public enum ConversationState
{
    // Workflow A: Greeting
    Greeting,

    // Workflow B: Guardian intake
    CollectingGuardianName,
    CollectingGuardianEmail,
    CollectingGuardianPhone,
    CollectingGdprConsent,

    // Workflow C: Player intake
    CollectingPlayerName,
    CollectingPlayerDob,
    CollectingPlayerGender,

    // Workflow D: Program matching & FAQ
    ShowingProgramMatches,
    AnsweringFaq,

    // Workflow E: Assessment booking
    ProposingAssessmentSlots,
    ConfirmingAssessmentBooking,
    AssessmentBooked,

    // Workflow F: Waiver & payment
    SendingWaiver,
    WaiverPending,
    SendingPaymentLink,
    PaymentPending,

    // Workflow G: Completion
    EnrollmentComplete,
    WaitlistConfirmed,

    // Terminal states
    EscalatedToStaff,
    SessionEnded
}

public sealed record StateTransitionResult(
    bool IsValid,
    ConversationState NewState,
    string? ErrorMessage = null);

public static class ConversationStateMachine
{
    private static readonly Dictionary<ConversationState, HashSet<ConversationState>> _allowedTransitions = new()
    {
        [ConversationState.Greeting] = new() { ConversationState.CollectingGuardianName, ConversationState.AnsweringFaq, ConversationState.EscalatedToStaff },
        [ConversationState.CollectingGuardianName] = new() { ConversationState.CollectingGuardianEmail, ConversationState.EscalatedToStaff },
        [ConversationState.CollectingGuardianEmail] = new() { ConversationState.CollectingGuardianPhone, ConversationState.EscalatedToStaff },
        // GDPR consent is mandatory: the ONLY path out of phone collection is the
        // consent step. Player intake must never be reachable without first passing
        // through CollectingGdprConsent, otherwise a future LLM-driven layer could
        // drive a transition that skips consent (fail-open). Keep this fail-closed.
        [ConversationState.CollectingGuardianPhone] = new() { ConversationState.CollectingGdprConsent },
        [ConversationState.CollectingGdprConsent] = new() { ConversationState.CollectingPlayerName, ConversationState.SessionEnded },
        [ConversationState.CollectingPlayerName] = new() { ConversationState.CollectingPlayerDob },
        [ConversationState.CollectingPlayerDob] = new() { ConversationState.CollectingPlayerGender },
        [ConversationState.CollectingPlayerGender] = new() { ConversationState.ShowingProgramMatches },
        [ConversationState.ShowingProgramMatches] = new() { ConversationState.ProposingAssessmentSlots, ConversationState.SendingWaiver, ConversationState.WaitlistConfirmed, ConversationState.AnsweringFaq, ConversationState.EscalatedToStaff },
        [ConversationState.AnsweringFaq] = new() { ConversationState.Greeting, ConversationState.ShowingProgramMatches, ConversationState.CollectingGuardianName, ConversationState.EscalatedToStaff },
        [ConversationState.ProposingAssessmentSlots] = new() { ConversationState.ConfirmingAssessmentBooking, ConversationState.EscalatedToStaff },
        [ConversationState.ConfirmingAssessmentBooking] = new() { ConversationState.AssessmentBooked, ConversationState.ProposingAssessmentSlots },
        [ConversationState.AssessmentBooked] = new() { ConversationState.SendingWaiver, ConversationState.EscalatedToStaff },
        [ConversationState.SendingWaiver] = new() { ConversationState.WaiverPending },
        [ConversationState.WaiverPending] = new() { ConversationState.SendingPaymentLink, ConversationState.EscalatedToStaff },
        [ConversationState.SendingPaymentLink] = new() { ConversationState.PaymentPending },
        [ConversationState.PaymentPending] = new() { ConversationState.EnrollmentComplete, ConversationState.EscalatedToStaff },
        [ConversationState.EnrollmentComplete] = new() { ConversationState.SessionEnded },
        [ConversationState.WaitlistConfirmed] = new() { ConversationState.SessionEnded },
        [ConversationState.EscalatedToStaff] = new() { ConversationState.SessionEnded },
        [ConversationState.SessionEnded] = new()
    };

    public static StateTransitionResult Transition(
        ConversationState current,
        ConversationState requested)
    {
        if (!_allowedTransitions.TryGetValue(current, out var allowed))
            return new StateTransitionResult(false, current, $"Unknown state: {current}");

        if (!allowed.Contains(requested))
            return new StateTransitionResult(
                false, current,
                $"Transition from {current} to {requested} is not permitted.");

        return new StateTransitionResult(true, requested);
    }

    /// <summary>
    /// Fail-loud variant: throws <see cref="InvalidStateTransitionException"/> for
    /// any transition not declared in the table. Escalation to
    /// <see cref="ConversationState.EscalatedToStaff"/> is the one explicit
    /// exception that is always permitted from a non-terminal state — every other
    /// unlisted transition is rejected so a state can never be silently skipped.
    /// </summary>
    public static ConversationState TransitionOrThrow(
        ConversationState current,
        ConversationState requested)
    {
        if (requested == ConversationState.EscalatedToStaff && !IsTerminal(current))
            return ConversationState.EscalatedToStaff;

        var result = Transition(current, requested);
        if (!result.IsValid)
            throw new InvalidStateTransitionException(current, requested, result.ErrorMessage);

        return result.NewState;
    }

    public static bool IsAllowed(ConversationState current, ConversationState requested) =>
        Transition(current, requested).IsValid;

    public static bool IsTerminal(ConversationState state) =>
        state is ConversationState.SessionEnded or ConversationState.EscalatedToStaff;

    public static bool RequiresEscalation(ConversationState state) =>
        state == ConversationState.EscalatedToStaff;
}

/// <summary>
/// Thrown when an unlisted state transition is attempted. Surfacing this loudly
/// (rather than silently proceeding) is a deliberate fail-closed guarantee: the
/// orchestrator catches it and routes to a human via the escalation chokepoint.
/// </summary>
public sealed class InvalidStateTransitionException : Exception
{
    public ConversationState From { get; }
    public ConversationState To { get; }

    public InvalidStateTransitionException(ConversationState from, ConversationState to, string? detail = null)
        : base(detail ?? $"Transition from {from} to {to} is not permitted.")
    {
        From = from;
        To = to;
    }
}

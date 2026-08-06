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

public sealed class ConversationStateMachine
{
    private static readonly Dictionary<ConversationState, HashSet<ConversationState>> _allowedTransitions = new()
    {
        [ConversationState.Greeting] = new() { ConversationState.CollectingGuardianName, ConversationState.AnsweringFaq, ConversationState.EscalatedToStaff },
        [ConversationState.CollectingGuardianName] = new() { ConversationState.CollectingGuardianEmail, ConversationState.EscalatedToStaff },
        [ConversationState.CollectingGuardianEmail] = new() { ConversationState.CollectingGuardianPhone, ConversationState.EscalatedToStaff },
        [ConversationState.CollectingGuardianPhone] = new() { ConversationState.CollectingGdprConsent, ConversationState.CollectingPlayerName },
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

    public static bool IsTerminal(ConversationState state) =>
        state is ConversationState.SessionEnded or ConversationState.EscalatedToStaff;

    public static bool RequiresEscalation(ConversationState state) =>
        state == ConversationState.EscalatedToStaff;
}

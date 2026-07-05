using System.ComponentModel.DataAnnotations;

namespace NeytrixAI.Api.Tools;

/// <summary>
/// All P0 tool contracts. The LLM ONLY calls these typed methods.
/// No tool may read or write the database directly.
/// Each tool goes through a permissioned application service.
/// </summary>

// ── Tool: answer_faq ─────────────────────────────────────────
public sealed record AnswerFaqRequest(
    [Required] string SessionToken,
    [Required][MaxLength(500)] string Question);

public sealed record AnswerFaqResponse(
    string Answer,
    double ConfidenceScore,
    bool RequiresEscalation,
    string[] SourceChunkIds);

// ── Tool: upsert_guardian ────────────────────────────────────
public sealed record UpsertGuardianRequest(
    [Required] string SessionToken,
    [Required][MaxLength(100)] string FirstName,
    [Required][MaxLength(100)] string LastName,
    [Required][EmailAddress] string Email,
    [Phone] string? Phone,
    [Required] bool GdprConsentGiven);

public sealed record UpsertGuardianResponse(
    Guid GuardianId,
    bool IsNew,
    string FullName);

// ── Tool: add_player ─────────────────────────────────────────
public sealed record AddPlayerRequest(
    [Required] string SessionToken,
    [Required] Guid GuardianId,
    [Required][MaxLength(100)] string FirstName,
    [Required][MaxLength(100)] string LastName,
    [Required] DateOnly DateOfBirth,
    string? Gender);

public sealed record AddPlayerResponse(
    Guid PlayerId,
    string FullName,
    int CurrentAge);

// ── Tool: match_programs ─────────────────────────────────────
public sealed record MatchProgramsRequest(
    [Required] string SessionToken,
    [Required] Guid PlayerId);

public sealed record ProgramMatchDto(
    Guid ProgramId,
    string Name,
    string Sport,
    int MinAge,
    int MaxAge,
    string GenderPolicy,
    string SkillLevel,
    int SpotsRemaining,
    bool IsWaitlistOnly,
    long PriceCents,
    long DepositCents,
    string Currency,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Location,
    double RelevanceScore);

public sealed record MatchProgramsResponse(
    ProgramMatchDto[] Matches,
    int TotalEligible);

// ── Tool: get_available_slots ────────────────────────────────
public sealed record GetAvailableSlotsRequest(
    [Required] string SessionToken,
    [Required] Guid ProgramId,
    DateOnly? PreferredWeekOf);

public sealed record SlotDto(
    string SlotId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int DurationMinutes,
    string? Location);

public sealed record GetAvailableSlotsResponse(
    SlotDto[] Slots);

// ── Tool: book_assessment ────────────────────────────────────
public sealed record BookAssessmentRequest(
    [Required] string SessionToken,
    [Required] Guid RegistrationId,
    [Required] string SlotId);

public sealed record BookAssessmentResponse(
    Guid AssessmentId,
    DateTimeOffset ScheduledAt,
    string? Location,
    string GoogleEventId,
    string ConfirmationMessage);

// ── Tool: send_waiver ────────────────────────────────────────
public sealed record SendWaiverRequest(
    [Required] string SessionToken,
    [Required] Guid RegistrationId);

public sealed record SendWaiverResponse(
    bool Sent,
    string WaiverUrl,
    DateTimeOffset ExpiresAt);

// ── Tool: create_payment_link ────────────────────────────────
public sealed record CreatePaymentLinkRequest(
    [Required] string SessionToken,
    [Required] Guid RegistrationId,
    bool DepositOnly = false);

public sealed record CreatePaymentLinkResponse(
    string PaymentUrl,
    long AmountCents,
    string Currency,
    string CheckoutSessionId,
    DateTimeOffset ExpiresAt);

// ── Tool: create_registration ────────────────────────────────
public sealed record CreateRegistrationRequest(
    [Required] string SessionToken,
    [Required] Guid GuardianId,
    [Required] Guid PlayerId,
    [Required] Guid ProgramId);

public sealed record CreateRegistrationResponse(
    Guid RegistrationId,
    string Status,
    bool IsWaitlisted,
    int? WaitlistPosition);

// ── Tool: escalate_to_staff ──────────────────────────────────
public sealed record EscalateToStaffRequest(
    [Required] string SessionToken,
    [Required] string Reason,
    EscalationCategory Category = EscalationCategory.General);

public enum EscalationCategory
{
    General,
    Safeguarding,
    Financial,
    Medical,
    Complaint
}

public sealed record EscalateToStaffResponse(
    string TicketId,
    string Message,
    string EstimatedResponseTime);

// ── Tool: check_registration_status ─────────────────────────
public sealed record CheckRegistrationStatusRequest(
    [Required] string SessionToken,
    [Required] Guid RegistrationId);

public sealed record CheckRegistrationStatusResponse(
    string Status,
    bool WaiverSigned,
    bool PaymentComplete,
    bool IsEnrolled,
    int? WaitlistPosition);

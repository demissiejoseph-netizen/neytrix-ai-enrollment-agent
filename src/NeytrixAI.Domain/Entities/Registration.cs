namespace NeytrixAI.Domain.Entities;

/// <summary>
/// A guardian's registration of a player into a program.
///
/// SAFETY: Registrations are fail-closed. A new registration always starts in
/// the non-committal <see cref="RegistrationStatus.Inquiry"/> state. It can only
/// reach <see cref="RegistrationStatus.Enrolled"/> after a waiver is signed AND
/// payment is complete. Uncertainty (missing capacity, unsigned waiver, unpaid
/// balance) must leave the registration in a pending/waitlisted state — never
/// auto-enrolled.
/// </summary>
public sealed class Registration
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid GuardianId { get; private set; }
    public Guid PlayerId { get; private set; }
    public Guid ProgramId { get; private set; }
    public string Status { get; private set; } = RegistrationStatus.Inquiry;
    public int? WaitlistPosition { get; private set; }
    public string? StripePaymentIntentId { get; private set; }
    public string? StripeCheckoutSessionId { get; private set; }
    public long AmountPaidCents { get; private set; }
    public DateTimeOffset? WaiverSentAt { get; private set; }
    public DateTimeOffset? WaiverSignedAt { get; private set; }
    public DateTimeOffset? EnrolledAt { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Registration() { }

    public static Registration Create(
        Guid tenantId,
        Guid guardianId,
        Guid playerId,
        Guid programId,
        bool isWaitlisted = false,
        int? waitlistPosition = null)
    {
        return new Registration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GuardianId = guardianId,
            PlayerId = playerId,
            ProgramId = programId,
            Status = isWaitlisted ? RegistrationStatus.Waitlisted : RegistrationStatus.Inquiry,
            WaitlistPosition = isWaitlisted ? waitlistPosition : null,
            AmountPaidCents = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public bool WaiverSigned => WaiverSignedAt is not null;
    public bool PaymentComplete => Status is RegistrationStatus.PaymentComplete or RegistrationStatus.Enrolled;
    public bool IsEnrolled => Status == RegistrationStatus.Enrolled;
    public bool IsWaitlisted => Status == RegistrationStatus.Waitlisted;

    public void MarkAssessmentScheduled()
    {
        GuardAgainstTerminal();
        Status = RegistrationStatus.AssessmentScheduled;
        Touch();
    }

    public void MarkWaiverSent()
    {
        GuardAgainstTerminal();
        Status = RegistrationStatus.WaiverSent;
        WaiverSentAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkWaiverSigned()
    {
        GuardAgainstTerminal();
        Status = RegistrationStatus.WaiverSigned;
        WaiverSignedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkPaymentPending(string checkoutSessionId)
    {
        GuardAgainstTerminal();
        Status = RegistrationStatus.PaymentPending;
        StripeCheckoutSessionId = checkoutSessionId;
        Touch();
    }

    public void MarkPaymentComplete(long amountPaidCents, string? paymentIntentId)
    {
        GuardAgainstTerminal();
        Status = RegistrationStatus.PaymentComplete;
        AmountPaidCents = amountPaidCents;
        StripePaymentIntentId = paymentIntentId;
        Touch();
    }

    /// <summary>
    /// Fail-closed enrollment: only completes when a waiver is signed AND payment
    /// is complete. Any other state throws rather than silently enrolling.
    /// </summary>
    public void CompleteEnrollment()
    {
        if (!WaiverSigned)
            throw new InvalidOperationException("Cannot enroll: waiver has not been signed.");
        if (!PaymentComplete)
            throw new InvalidOperationException("Cannot enroll: payment is not complete.");

        Status = RegistrationStatus.Enrolled;
        EnrolledAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Cancel(string? reason = null)
    {
        Status = RegistrationStatus.Cancelled;
        if (reason is not null) Notes = reason;
        Touch();
    }

    private void GuardAgainstTerminal()
    {
        if (Status is RegistrationStatus.Cancelled)
            throw new InvalidOperationException("Registration is cancelled and cannot be modified.");
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>Status values mirror the <c>registration_status</c> enum in the database.</summary>
public static class RegistrationStatus
{
    public const string Inquiry = "inquiry";
    public const string IntakeComplete = "intake_complete";
    public const string AssessmentScheduled = "assessment_scheduled";
    public const string AssessmentComplete = "assessment_complete";
    public const string WaiverSent = "waiver_sent";
    public const string WaiverSigned = "waiver_signed";
    public const string PaymentPending = "payment_pending";
    public const string PaymentComplete = "payment_complete";
    public const string Enrolled = "enrolled";
    public const string Waitlisted = "waitlisted";
    public const string Cancelled = "cancelled";
}

namespace NeytrixAI.Domain.Entities;

/// <summary>
/// A player's enrollment in a program. Mirrors the <c>registrations</c> table,
/// including the <c>registration_status</c> enum values defined in
/// db/migrations/001_initial_schema.sql.
/// </summary>
public sealed class Registration
{
    public const string StatusInquiry = "inquiry";
    public const string StatusPendingWaiver = "waiver_sent";
    // NOTE: these must match the registration_status enum in
    // db/migrations/001_initial_schema.sql exactly. Postgres rejects any other
    // literal at insert time with error 22P02, so a typo here is a runtime 500,
    // not a compile error.
    public const string StatusPendingPayment = "payment_pending";
    public const string StatusPaymentComplete = "payment_complete";
    public const string StatusEnrolled = "enrolled";
    public const string StatusWaitlisted = "waitlisted";
    public const string StatusCancelled = "cancelled";

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid GuardianId { get; private set; }
    public Guid PlayerId { get; private set; }
    public Guid ProgramId { get; private set; }
    public string Status { get; private set; } = StatusInquiry;
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

    public bool IsWaitlisted => Status == StatusWaitlisted;
    public bool WaiverSigned => WaiverSignedAt is not null;
    public bool IsEnrolled => Status == StatusEnrolled;

    private Registration() { }

    public static Registration Create(
        Guid tenantId,
        Guid guardianId,
        Guid playerId,
        Guid programId,
        bool waitlisted = false,
        int? waitlistPosition = null)
    {
        if (waitlisted && waitlistPosition is null or < 1)
            throw new ArgumentException("A waitlisted registration needs a position of 1 or greater.", nameof(waitlistPosition));

        return new Registration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GuardianId = guardianId,
            PlayerId = playerId,
            ProgramId = programId,
            Status = waitlisted ? StatusWaitlisted : StatusInquiry,
            WaitlistPosition = waitlisted ? waitlistPosition : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void MarkWaiverSent()
    {
        WaiverSentAt = DateTimeOffset.UtcNow;
        if (Status == StatusInquiry) Status = StatusPendingWaiver;
        Touch();
    }

    public void MarkWaiverSigned()
    {
        WaiverSignedAt = DateTimeOffset.UtcNow;
        if (Status == StatusPendingWaiver) Status = StatusPendingPayment;
        Touch();
    }

    public void AttachCheckoutSession(string checkoutSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutSessionId);
        StripeCheckoutSessionId = checkoutSessionId;
        if (Status is StatusInquiry or StatusPendingWaiver) Status = StatusPendingPayment;
        Touch();
    }

    /// <summary>Records a settled Stripe payment and enrolls once the balance is covered.</summary>
    public void RecordPayment(string paymentIntentId, long amountCents, long programPriceCents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        if (amountCents < 0) throw new ArgumentException("Paid amount cannot be negative.", nameof(amountCents));

        StripePaymentIntentId = paymentIntentId;
        AmountPaidCents += amountCents;

        if (AmountPaidCents >= programPriceCents && WaiverSignedAt is not null)
        {
            Status = StatusEnrolled;
            EnrolledAt = DateTimeOffset.UtcNow;
        }

        Touch();
    }

    public void PromoteFromWaitlist()
    {
        if (Status != StatusWaitlisted)
            throw new InvalidOperationException("Only a waitlisted registration can be promoted.");

        Status = WaiverSignedAt is null ? StatusPendingWaiver : StatusPendingPayment;
        WaitlistPosition = null;
        Touch();
    }

    public void Cancel(string? reason = null)
    {
        Status = StatusCancelled;
        if (!string.IsNullOrWhiteSpace(reason)) Notes = reason;
        Touch();
    }

    public void SetNotes(string? notes)
    {
        Notes = notes;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

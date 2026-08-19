namespace NeytrixAI.Domain.Entities;

/// <summary>
/// A booked in-person/virtual assessment slot tied to a registration.
/// Mirrors the <c>assessments</c> table in db/migrations/001_initial_schema.sql.
/// </summary>
public sealed class Assessment
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RegistrationId { get; private set; }
    public string? GoogleEventId { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public int DurationMinutes { get; private set; } = 60;
    public string? Location { get; private set; }
    public string? Notes { get; private set; }
    public string? Outcome { get; private set; }
    public DateTimeOffset? AssessedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Assessment() { }

    public static Assessment Create(
        Guid tenantId,
        Guid registrationId,
        DateTimeOffset scheduledAt,
        string? googleEventId = null,
        int durationMinutes = 60,
        string? location = null)
    {
        if (durationMinutes <= 0)
            throw new ArgumentException("Duration must be positive.", nameof(durationMinutes));

        return new Assessment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RegistrationId = registrationId,
            GoogleEventId = googleEventId,
            ScheduledAt = scheduledAt,
            DurationMinutes = durationMinutes,
            Location = location,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void RecordOutcome(string outcome, string? notes = null)
    {
        if (outcome is not ("pass" or "fail" or "pending" or "no_show"))
            throw new ArgumentException("Outcome must be pass, fail, pending, or no_show.", nameof(outcome));
        Outcome = outcome;
        Notes = notes ?? Notes;
        AssessedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

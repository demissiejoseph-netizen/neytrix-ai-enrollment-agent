namespace NeytrixAI.Domain.Entities;

/// <summary>
/// Mirrors the <c>audit_log</c> table in db/migrations/001_initial_schema.sql.
/// Written for safety- and money-relevant tool calls (registration, escalation, payment)
/// so there is a real trail behind the README's advertised audit capability.
/// </summary>
public sealed class AuditLogEntry
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ActorType { get; private set; } = "agent";
    public string? ActorId { get; private set; }
    public string Action { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;
    public Guid? ResourceId { get; private set; }
    public string? PayloadJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private AuditLogEntry() { }

    public static AuditLogEntry Create(
        Guid tenantId,
        string action,
        string resourceType,
        Guid? resourceId,
        string? payloadJson = null,
        string actorType = "agent",
        string? actorId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        if (actorType is not ("agent" or "staff" or "system"))
            throw new ArgumentException("actorType must be agent, staff, or system.", nameof(actorType));

        return new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            PayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IAuditLogRepository
{
    Task<Guid> AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);
}

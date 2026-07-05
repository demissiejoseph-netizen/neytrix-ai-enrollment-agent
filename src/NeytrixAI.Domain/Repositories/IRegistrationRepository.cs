using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IRegistrationRepository
{
    Task<Registration?> GetByIdAsync(Guid tenantId, Guid registrationId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Registration>> GetBySessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Registration>> GetByPlayerAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Registration>> GetByProgramAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Registration registration, CancellationToken cancellationToken = default);
    Task UpdateAsync(Registration registration, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(Guid tenantId, Guid registrationId, string status, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid tenantId, Guid playerId, Guid programId, CancellationToken cancellationToken = default);
}

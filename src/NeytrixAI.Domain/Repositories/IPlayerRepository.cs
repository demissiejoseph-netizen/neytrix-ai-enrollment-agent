using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IPlayerRepository
{
    Task<Player?> GetByIdAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Player>> GetByGuardianAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Player>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Player player, CancellationToken cancellationToken = default);
    Task UpdateAsync(Player player, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
}

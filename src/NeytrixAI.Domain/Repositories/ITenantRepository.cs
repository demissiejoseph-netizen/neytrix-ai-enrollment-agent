using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task SetTenantSessionAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

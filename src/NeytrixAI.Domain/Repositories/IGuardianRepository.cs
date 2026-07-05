using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IGuardianRepository
{
    Task<Guardian?> GetByIdAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default);
    Task<Guardian?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);
    Task<IEnumerable<Guardian>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Guardian guardian, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guardian guardian, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default);
}

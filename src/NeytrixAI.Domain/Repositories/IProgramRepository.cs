using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IProgramRepository
{
    Task<Program?> GetByIdAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Program>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Program>> FindEligibleProgramsAsync(Guid tenantId, int playerAge, string? skillLevel, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Program program, CancellationToken cancellationToken = default);
    Task UpdateAsync(Program program, CancellationToken cancellationToken = default);
    Task<bool> HasCapacityAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default);
}

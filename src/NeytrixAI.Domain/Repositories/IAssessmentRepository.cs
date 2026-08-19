using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

public interface IAssessmentRepository
{
    Task<Assessment?> GetByIdAsync(Guid tenantId, Guid assessmentId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Assessment>> GetByRegistrationAsync(Guid tenantId, Guid registrationId, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(Assessment assessment, CancellationToken cancellationToken = default);
    Task UpdateAsync(Assessment assessment, CancellationToken cancellationToken = default);
}

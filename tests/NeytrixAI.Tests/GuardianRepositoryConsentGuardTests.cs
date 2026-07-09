using System.Data;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Infrastructure.Data;
using NeytrixAI.Infrastructure.Data.Repositories;
using Xunit;

namespace NeytrixAI.Tests;

// Proves the GDPR consent gate lives at the write path itself, not just in the
// conversation layer. If consent has not been recorded, CreateAsync must throw
// BEFORE any database connection is opened — so a guardian's personal data can
// never leave the process without consent, regardless of how the call is reached.
public sealed class GuardianRepositoryConsentGuardTests
{
    // Connection factory that fails the test if it is ever touched: reaching it
    // means the consent guard did not short-circuit.
    private sealed class ThrowingConnectionFactory : IDbConnectionFactory
    {
        public bool WasCalled { get; private set; }

        public Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Connection must not be opened when consent is missing.");
        }
    }

    [Fact]
    public async Task CreateAsync_WithoutRecordedConsent_ThrowsBeforeOpeningConnection()
    {
        var factory = new ThrowingConnectionFactory();
        var repo = new GuardianRepository(factory);

        // Guardian created but consent NOT recorded.
        var guardian = Guardian.Create(Guid.NewGuid(), "Jane", "Doe", "jane@example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CreateAsync(guardian));

        Assert.Contains("consent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(factory.WasCalled, "The connection factory must never be invoked without consent.");
    }
}

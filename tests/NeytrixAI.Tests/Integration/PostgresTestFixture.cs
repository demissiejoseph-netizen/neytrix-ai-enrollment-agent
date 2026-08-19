using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// Wires the end-to-end tests to a real local PostgreSQL instance (the same database and
/// schema used by the running API), instead of any in-memory or mocked store. Connection
/// strings default to the local dev database set up for this sandbox but can be overridden via
/// environment variables so this also runs against a CI-provisioned Postgres.
///
/// Two connection strings are needed because of a deliberate production security boundary
/// (see db/migrations/001_initial_schema.sql, GRANT section): the <c>neytrix_app</c> role that
/// the running API - and every normal repository call in this test - connects as has only
/// SELECT on <c>tenants</c>. Provisioning a tenant is meant to be an out-of-band operator
/// action, so seeding a test tenant must go through a superuser connection directly, bypassing
/// the app's repositories entirely, exactly as a real operator/migration tool would.
/// </summary>
public static class PostgresTestFixture
{
    public static string AppConnectionString =>
        Environment.GetEnvironmentVariable("NEYTRIX_TEST_APP_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=neytrix;Username=neytrix_app;Password=neytrix_dev_local_pw;Pooling=true;MinPoolSize=0;MaxPoolSize=10";

    public static string SuperuserConnectionString =>
        Environment.GetEnvironmentVariable("NEYTRIX_TEST_SUPERUSER_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=neytrix;Username=postgres;Password=postgres_dev_local_pw";

    /// <summary>Soft-skip helper for integration tests: true if the local/CI Postgres from the connection strings above is actually reachable right now.</summary>
    public static async Task<bool> IsPostgresReachableAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(SuperuserConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Builds a real <see cref="IDbConnectionFactory"/> backed by the neytrix_app role, the same one the running API uses (RLS enforced, minimal grants).</summary>
    public static IDbConnectionFactory CreateAppConnectionFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = AppConnectionString
            })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    /// <summary>
    /// Inserts a tenant row via a superuser connection, mirroring TenantRepository.CreateAsync's
    /// SQL exactly. Must be used instead of ITenantRepository.CreateAsync in tests, because the
    /// neytrix_app role the real repository connects as has no INSERT grant on tenants by design.
    /// </summary>
    public static async Task SeedTenantAsync(Tenant tenant, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO tenants (id, slug, name, timezone, stripe_account_id, google_calendar_id, settings, is_active, created_at, updated_at)
            VALUES (@id, @slug, @name, @timezone, @stripe_account_id, @google_calendar_id, @settings::jsonb, @is_active, @created_at, @updated_at)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", tenant.Id);
        command.Parameters.AddWithValue("slug", tenant.Slug);
        command.Parameters.AddWithValue("name", tenant.Name);
        command.Parameters.AddWithValue("timezone", tenant.Timezone);
        command.Parameters.AddWithValue("stripe_account_id", (object?)tenant.StripeAccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("google_calendar_id", (object?)tenant.GoogleCalendarId ?? DBNull.Value);
        command.Parameters.AddWithValue("settings", JsonSerializer.Serialize(tenant.Settings));
        command.Parameters.AddWithValue("is_active", tenant.IsActive);
        command.Parameters.AddWithValue("created_at", tenant.CreatedAt);
        command.Parameters.AddWithValue("updated_at", tenant.UpdatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Superuser cleanup for tenant-scoped rows created by a test, so repeated runs don't accumulate data. Deletes in FK-safe order.</summary>
    public static async Task DeleteTenantCascadeAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync(ct);

        foreach (var table in new[]
        {
            "audit_log", "conversation_messages", "conversation_sessions", "assessments",
            "registrations", "players", "guardians", "programs", "knowledge_chunks"
        })
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id = @tenantId", connection);
            command.Parameters.AddWithValue("tenantId", tenantId);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var deleteTenant = new NpgsqlCommand("DELETE FROM tenants WHERE id = @tenantId", connection);
        deleteTenant.Parameters.AddWithValue("tenantId", tenantId);
        await deleteTenant.ExecuteNonQueryAsync(ct);
    }
}

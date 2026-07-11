using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NeytrixAI.Tests;

// Real-database proof that Row-Level Security isolates tenants. This is the one
// enforcement point that unit tests with in-memory fakes cannot cover, so it runs
// against a genuine Postgres instance (via Testcontainers) with migrations 001 and
// 002 applied, connecting as a NON-superuser, NON-BYPASSRLS role — the only
// configuration under which FORCE ROW LEVEL SECURITY actually bites.
//
// It self-skips (SkippableFact) when Docker is not available (e.g. CI sandboxes
// without a Docker daemon), so it never produces a false failure; it must be run
// in an environment with Docker to exercise the isolation guarantee. LIMITATION:
// in this authoring sandbox Docker is unavailable, so the test skips here and is
// expected to run in CI.
public sealed class TenantIsolationIntegrationTests : IAsyncLifetime
{
    private const string AppRole = "neytrix_app";
    private const string AppPassword = "test_only_password";

    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable = true;

    private static string MigrationsDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations");

    public async Task InitializeAsync()
    {
        try
        {
            // Build() itself validates the Docker endpoint, so it must be inside the
            // guard: on a machine without Docker it throws before StartAsync.
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("neytrix")
                .WithUsername("owner")
                .WithPassword("owner_pw")
                .Build();

            await _pg.StartAsync();
        }
        catch (Exception)
        {
            // No Docker daemon / cannot pull image: mark unavailable so every test skips.
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null && _dockerAvailable)
            await _pg.DisposeAsync();
    }

    private async Task ApplySchemaAndSeedAsync(Guid tenantA, Guid tenantB)
    {
        var ownerCs = _pg!.GetConnectionString();
        await using var conn = new NpgsqlConnection(ownerCs);
        await conn.OpenAsync();

        // pgvector isn't in postgres:16-alpine; migration 001 requires the `vector`
        // extension. Provide a minimal shim so CREATE EXTENSION and vector(1536)
        // succeed without the real extension (isolation behaviour is unaffected).
        await conn.ExecuteAsync(@"
            CREATE OR REPLACE FUNCTION public._noop_ext() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;
        ");

        var schema001 = await File.ReadAllTextAsync(Path.Combine(MigrationsDir, "001_initial_schema.sql"));
        var schema002 = await File.ReadAllTextAsync(Path.Combine(MigrationsDir, "002_rls_hardening.sql"));

        // Neutralise the pgvector-specific statements that the alpine image lacks.
        schema001 = schema001
            .Replace("CREATE EXTENSION IF NOT EXISTS vector;", "-- vector extension shimmed for test")
            .Replace("embedding    vector(1536),", "embedding    TEXT,")
            .Replace(
                "CREATE INDEX idx_knowledge_embedding ON knowledge_chunks\n  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);",
                "-- ivfflat index shimmed for test");

        await conn.ExecuteAsync(schema001);
        await conn.ExecuteAsync(schema002);

        // Dedicated app role: NON-superuser, NON-BYPASSRLS (the whole point).
        await conn.ExecuteAsync($@"
            DROP ROLE IF EXISTS {AppRole};
            CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOBYPASSRLS;
            GRANT USAGE ON SCHEMA public TO {AppRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {AppRole};
        ");

        // Seed two tenants and one guardian each, as the owner (RLS forced, but the
        // owner sets the GUC per insert to satisfy the WITH CHECK on child tables).
        foreach (var (id, slug) in new[] { (tenantA, "acme"), (tenantB, "globex") })
        {
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @id, false)", new { id = id.ToString() });
            await conn.ExecuteAsync(
                "INSERT INTO tenants (id, slug, name) VALUES (@id, @slug, @name)",
                new { id, slug, name = slug });
            await conn.ExecuteAsync(@"
                INSERT INTO guardians (tenant_id, first_name, last_name, email, gdpr_consented_at)
                VALUES (@id, 'Jane', 'Doe', @email, NOW())",
                new { id, email = $"jane@{slug}.test" });
        }
    }

    private NpgsqlConnection AppConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString())
        {
            Username = AppRole,
            Password = AppPassword
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    [SkippableFact]
    public async Task AppRole_OnlySeesItsOwnTenantRows()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available in this environment.");

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await ApplySchemaAndSeedAsync(tenantA, tenantB);

        await using var conn = AppConnection();
        await conn.OpenAsync();

        // Context = tenant A.
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @id, false)", new { id = tenantA.ToString() });

        var visibleGuardians = await conn.QueryAsync<string>("SELECT email FROM guardians");
        var emails = visibleGuardians.ToList();

        Assert.Single(emails);
        Assert.Equal("jane@acme.test", emails[0]);

        // Tenant B's guardian must be completely invisible, even by direct id filter.
        var crossTenantCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM guardians WHERE tenant_id = @b", new { b = tenantB });
        Assert.Equal(0, crossTenantCount);
    }

    [SkippableFact]
    public async Task AppRole_CannotInsertRowForAnotherTenant()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available in this environment.");

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await ApplySchemaAndSeedAsync(tenantA, tenantB);

        await using var conn = AppConnection();
        await conn.OpenAsync();

        // Context = tenant A, but try to write a row tagged for tenant B.
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @id, false)", new { id = tenantA.ToString() });

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await conn.ExecuteAsync(@"
                INSERT INTO guardians (tenant_id, first_name, last_name, email, gdpr_consented_at)
                VALUES (@b, 'Mallory', 'Cross', 'mallory@evil.test', NOW())",
                new { b = tenantB }));
    }

    [SkippableFact]
    public async Task AppRole_IsNotSuperuserAndCannotBypassRls()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available in this environment.");

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await ApplySchemaAndSeedAsync(tenantA, tenantB);

        await using var conn = AppConnection();
        await conn.OpenAsync();

        var (isSuper, bypassRls) = await conn.QuerySingleAsync<(bool, bool)>(
            "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user");

        Assert.False(isSuper, "Application role must not be a superuser.");
        Assert.False(bypassRls, "Application role must not have BYPASSRLS.");
    }
}

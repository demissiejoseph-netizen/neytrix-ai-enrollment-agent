using System.Collections.Concurrent;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Adapters;
// `DomainProgram` disambiguates the Program entity from the API host's top-level
// `Program` class, which the compiler emits into the global namespace.
using DomainProgram = NeytrixAI.Domain.Entities.Program;

namespace NeytrixAI.Tests.Fakes;

// Lightweight in-memory repository/adapter doubles for end-to-end orchestration
// tests. They emulate tenant-scoped reads (every query is filtered by the tenantId
// argument) so tests exercise the same tenant boundary the real RLS enforces.

public sealed class InMemoryGuardianRepository : IGuardianRepository
{
    public readonly ConcurrentDictionary<Guid, Guardian> Store = new();

    public Task<Guardian?> GetByIdAsync(Guid tenantId, Guid guardianId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(guardianId, out var g) && g.TenantId == tenantId ? g : null);

    public Task<Guardian?> GetByEmailAsync(Guid tenantId, string email, CancellationToken ct = default)
        => Task.FromResult(Store.Values.FirstOrDefault(g => g.TenantId == tenantId &&
            string.Equals(g.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<Guardian?> GetByClerkUserIdAsync(Guid tenantId, string clerkUserId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.FirstOrDefault(g => g.TenantId == tenantId &&
            g.ClerkUserId is not null &&
            string.Equals(g.ClerkUserId, clerkUserId.Trim(), StringComparison.Ordinal)));

    public Task<IEnumerable<Guardian>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(g => g.TenantId == tenantId));

    public Task<Guid> CreateAsync(Guardian guardian, CancellationToken ct = default)
    {
        // Honour cancellation like the real (DB-backed) repository so timeout paths
        // are exercised faithfully in tests.
        ct.ThrowIfCancellationRequested();
        // Mirror the fail-closed GDPR gate enforced by the real repository.
        if (guardian.GdprConsentedAt is null)
            throw new InvalidOperationException("Cannot store guardian: GDPR consent has not been recorded.");
        Store[guardian.Id] = guardian;
        return Task.FromResult(guardian.Id);
    }

    public Task UpdateAsync(Guardian guardian, CancellationToken ct = default)
    {
        Store[guardian.Id] = guardian;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(Guid tenantId, Guid guardianId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(guardianId, out var g) && g.TenantId == tenantId);
}

public sealed class InMemoryPlayerRepository : IPlayerRepository
{
    public readonly ConcurrentDictionary<Guid, Player> Store = new();

    public Task<Player?> GetByIdAsync(Guid tenantId, Guid playerId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(playerId, out var p) && p.TenantId == tenantId ? p : null);

    public Task<IEnumerable<Player>> GetByGuardianAsync(Guid tenantId, Guid guardianId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(p => p.TenantId == tenantId && p.GuardianId == guardianId));

    public Task<IEnumerable<Player>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(p => p.TenantId == tenantId));

    public Task<Guid> CreateAsync(Player player, CancellationToken ct = default)
    {
        Store[player.Id] = player;
        return Task.FromResult(player.Id);
    }

    public Task UpdateAsync(Player player, CancellationToken ct = default)
    {
        Store[player.Id] = player;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(Guid tenantId, Guid playerId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(playerId, out var p) && p.TenantId == tenantId);
}

public sealed class InMemoryProgramRepository : IProgramRepository
{
    public readonly ConcurrentDictionary<Guid, DomainProgram> Store = new();

    public Task<DomainProgram?> GetByIdAsync(Guid tenantId, Guid programId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(programId, out var p) && p.TenantId == tenantId ? p : null);

    public Task<IEnumerable<DomainProgram>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(p => p.TenantId == tenantId));

    public Task<IEnumerable<DomainProgram>> FindEligibleProgramsAsync(Guid tenantId, int playerAge, string? skillLevel, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(p => p.TenantId == tenantId));

    public Task<Guid> CreateAsync(DomainProgram program, CancellationToken ct = default)
    {
        Store[program.Id] = program;
        return Task.FromResult(program.Id);
    }

    public Task UpdateAsync(DomainProgram program, CancellationToken ct = default)
    {
        Store[program.Id] = program;
        return Task.CompletedTask;
    }

    public Task<bool> HasCapacityAsync(Guid tenantId, Guid programId, CancellationToken ct = default)
        => Task.FromResult(true);
}

public sealed class InMemoryRegistrationRepository : IRegistrationRepository
{
    public readonly ConcurrentDictionary<Guid, Registration> Store = new();

    public Task<Registration?> GetByIdAsync(Guid tenantId, Guid registrationId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(registrationId, out var r) && r.TenantId == tenantId ? r : null);

    public Task<IEnumerable<Registration>> GetBySessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<Registration>>(Array.Empty<Registration>());

    public Task<IEnumerable<Registration>> GetByPlayerAsync(Guid tenantId, Guid playerId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(r => r.TenantId == tenantId && r.PlayerId == playerId));

    public Task<IEnumerable<Registration>> GetByProgramAsync(Guid tenantId, Guid programId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Where(r => r.TenantId == tenantId && r.ProgramId == programId));

    public Task<Guid> CreateAsync(Registration registration, CancellationToken ct = default)
    {
        Store[registration.Id] = registration;
        return Task.FromResult(registration.Id);
    }

    public Task UpdateAsync(Registration registration, CancellationToken ct = default)
    {
        Store[registration.Id] = registration;
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(Guid tenantId, Guid registrationId, string status, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> ExistsAsync(Guid tenantId, Guid playerId, Guid programId, CancellationToken ct = default)
        => Task.FromResult(Store.Values.Any(r => r.TenantId == tenantId && r.PlayerId == playerId &&
            r.ProgramId == programId && r.Status != RegistrationStatus.Cancelled));
}

public sealed class InMemoryTenantRepository : ITenantRepository
{
    public readonly ConcurrentDictionary<Guid, Tenant> Store = new();

    public Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(Store.TryGetValue(tenantId, out var t) ? t : null);

    public Task<Tenant?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(Store.Values.FirstOrDefault(t => t.Slug == slug.ToLowerInvariant()));

    public Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IEnumerable<Tenant>>(Store.Values.ToList());

    public Task<Guid> CreateAsync(Tenant tenant, CancellationToken ct = default)
    {
        Store[tenant.Id] = tenant;
        return Task.FromResult(tenant.Id);
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken ct = default)
    {
        Store[tenant.Id] = tenant;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(Store.ContainsKey(tenantId));
}

/// <summary>Fake Stripe adapter whose failure behaviour is configurable per test.</summary>
public sealed class FakeStripeAdapter : IStripeAdapter
{
    public bool FailCheckout { get; set; }
    public string LastCheckoutSessionId { get; private set; } = string.Empty;

    public Task<PaymentLinkResult> CreateCheckoutSessionAsync(
        string stripeAccountId, Guid registrationId, long amountCents, string currency,
        string successUrl, string cancelUrl, bool depositOnly, CancellationToken ct)
    {
        if (FailCheckout)
            throw new InvalidOperationException("Simulated Stripe failure.");

        LastCheckoutSessionId = "cs_test_" + registrationId.ToString("N");
        return Task.FromResult(new PaymentLinkResult(
            LastCheckoutSessionId, "https://checkout.stripe.test/pay", amountCents, currency,
            DateTimeOffset.UtcNow.AddHours(24)));
    }

    public Task<WaiverResult> CreateWaiverLinkAsync(Guid registrationId, string guardianEmail, CancellationToken ct)
        => Task.FromResult(new WaiverResult("https://waiver.test/" + registrationId, DateTimeOffset.UtcNow.AddDays(7)));

    public Stripe.Event ParseWebhookEvent(string payload, string signature, string webhookSecret)
        => throw new NotSupportedException();
}

/// <summary>Fake calendar adapter that books a deterministic event.</summary>
public sealed class FakeCalendarAdapter : IGoogleCalendarAdapter
{
    public Task<IReadOnlyList<AvailableSlot>> GetAvailableSlotsAsync(
        string calendarId, DateOnly weekOf, int durationMinutes, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AvailableSlot>>(new[]
        {
            new AvailableSlot("slot_1", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1).AddHours(1), durationMinutes, "Main Hall")
        });

    public Task<BookedEvent> BookSlotAsync(
        string calendarId, string slotId, string guardianName, string guardianEmail,
        string playerName, string programName, CancellationToken ct)
        => Task.FromResult(new BookedEvent("evt_" + slotId, DateTimeOffset.UtcNow.AddDays(1), "https://calendar.test/evt"));

    public Task CancelEventAsync(string calendarId, string eventId, CancellationToken ct)
        => Task.CompletedTask;
}

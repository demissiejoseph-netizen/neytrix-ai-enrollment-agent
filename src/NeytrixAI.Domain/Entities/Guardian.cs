namespace NeytrixAI.Domain.Entities;

public sealed class Guardian
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string? Phone { get; private set; }
    public string PreferredContact { get; private set; } = "email";
    public DateTimeOffset? GdprConsentedAt { get; private set; }

    /// <summary>
    /// Clerk user id (the token's <c>sub</c> claim) when this guardian signed in
    /// through Clerk. Null for guardians created anonymously via the widget intake
    /// flow or entered manually by staff — Clerk auth is optional, not required.
    /// </summary>
    public string? ClerkUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    private readonly List<Player> _players = new();
    public IReadOnlyList<Player> Players => _players.AsReadOnly();

    private Guardian() { }

    public static Guardian Create(
        Guid tenantId,
        string firstName,
        string lastName,
        string email,
        string? phone = null,
        string preferredContact = "email",
        string? clerkUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        return new Guardian
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            Email = email.Trim().ToLowerInvariant(),
            Phone = phone?.Trim(),
            PreferredContact = preferredContact,
            ClerkUserId = string.IsNullOrWhiteSpace(clerkUserId) ? null : clerkUserId.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Attach a Clerk identity to an existing guardian. Idempotent: re-linking the
    /// same id is a no-op. Does NOT alter consent, contact, or any other field —
    /// this is purely additive identity plumbing.
    /// </summary>
    public void LinkClerkUser(string clerkUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clerkUserId);
        var trimmed = clerkUserId.Trim();
        if (ClerkUserId == trimmed) return;
        ClerkUserId = trimmed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordGdprConsent()
    {
        GdprConsentedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateContact(string? phone, string preferredContact)
    {
        Phone = phone?.Trim();
        PreferredContact = preferredContact;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

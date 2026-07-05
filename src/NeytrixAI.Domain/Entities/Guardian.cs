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
        string preferredContact = "email")
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
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

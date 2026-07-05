namespace NeytrixAI.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; private set; }
    public string Slug { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Timezone { get; private set; } = "America/Los_Angeles";
    public string? StripeAccountId { get; private set; }
    public string? GoogleCalendarId { get; private set; }
    public Dictionary<string, object> Settings { get; private set; } = new();
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Tenant() { }

    public static Tenant Create(string slug, string name, string timezone = "America/Los_Angeles")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug.ToLowerInvariant(),
            Name = name,
            Timezone = timezone,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void ConfigureStripe(string stripeAccountId)
    {
        StripeAccountId = stripeAccountId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ConfigureGoogleCalendar(string calendarId)
    {
        GoogleCalendarId = calendarId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate() { IsActive = false; UpdatedAt = DateTimeOffset.UtcNow; }
}

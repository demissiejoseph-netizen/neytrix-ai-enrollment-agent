namespace NeytrixAI.Domain.Entities;

public sealed class Program
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string Sport { get; private set; } = default!;
    public int MinAgeYears { get; private set; }
    public int MaxAgeYears { get; private set; }
    public string GenderPolicy { get; private set; } = "all";
    public string SkillLevel { get; private set; } = "all";
    public int Capacity { get; private set; }
    public long PriceCents { get; private set; }
    public long DepositCents { get; private set; }
    public string Currency { get; private set; } = "usd";
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public DateTimeOffset RegistrationOpenAt { get; private set; }
    public DateTimeOffset? RegistrationCloseAt { get; private set; }
    public string? Location { get; private set; }
    public string? StripePriceId { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Program() { }

    public static Program Create(
        Guid tenantId, string name, string sport,
        int minAge, int maxAge, int capacity,
        long priceCents, DateOnly startDate, DateOnly endDate,
        string genderPolicy = "all", string skillLevel = "all",
        long depositCents = 0, string? location = null)
    {
        if (minAge > maxAge) throw new ArgumentException("minAge cannot exceed maxAge.");
        if (capacity <= 0) throw new ArgumentException("Capacity must be positive.");
        if (priceCents < 0) throw new ArgumentException("Price cannot be negative.");

        return new Program
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Sport = sport,
            MinAgeYears = minAge,
            MaxAgeYears = maxAge,
            Capacity = capacity,
            PriceCents = priceCents,
            DepositCents = depositCents,
            GenderPolicy = genderPolicy,
            SkillLevel = skillLevel,
            StartDate = startDate,
            EndDate = endDate,
            Location = location,
            RegistrationOpenAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public bool IsRegistrationOpen =>
        IsActive &&
        DateTimeOffset.UtcNow >= RegistrationOpenAt &&
        (RegistrationCloseAt == null || DateTimeOffset.UtcNow <= RegistrationCloseAt);

    public void SetStripePriceId(string priceId)
    {
        StripePriceId = priceId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate() { IsActive = false; UpdatedAt = DateTimeOffset.UtcNow; }
}

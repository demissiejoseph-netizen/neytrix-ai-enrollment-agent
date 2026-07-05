namespace NeytrixAI.Domain.Entities;

public sealed class Player
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid GuardianId { get; private set; }
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public DateOnly DateOfBirth { get; private set; }
    public string? Gender { get; private set; }
    public string? MedicalNotes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public int AgeAtDate(DateOnly date)
    {
        var age = date.Year - DateOfBirth.Year;
        if (DateOfBirth.DayOfYear > date.DayOfYear) age--;
        return age;
    }

    public int CurrentAge => AgeAtDate(DateOnly.FromDateTime(DateTime.UtcNow));

    private Player() { }

    public static Player Create(
        Guid tenantId,
        Guid guardianId,
        string firstName,
        string lastName,
        DateOnly dateOfBirth,
        string? gender = null,
        string? medicalNotes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        if (dateOfBirth >= DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Date of birth must be in the past.", nameof(dateOfBirth));

        return new Player
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GuardianId = guardianId,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            DateOfBirth = dateOfBirth,
            Gender = gender,
            MedicalNotes = medicalNotes,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void UpdateMedicalNotes(string? notes)
    {
        MedicalNotes = notes;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

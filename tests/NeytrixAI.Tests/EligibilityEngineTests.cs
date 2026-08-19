using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Services;

namespace NeytrixAI.Tests;

public class EligibilityEngineTests
{
    private static readonly EligibilityEngine Engine = new();
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid GuardianId = Guid.NewGuid();

    private static Player MakePlayer(int ageYears, string? gender = null) =>
        Player.Create(TenantId, GuardianId, "Test", "Player",
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-ageYears)), gender);

    private static Program MakeProgram(
        int minAge, int maxAge, int capacity, string genderPolicy = "all",
        DateOnly? startDate = null) =>
        Program.Create(
            TenantId, "U10 Soccer", "soccer", minAge, maxAge, capacity,
            priceCents: 10000,
            startDate: startDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            endDate: (startDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))).AddMonths(3),
            genderPolicy: genderPolicy);

    [Fact]
    public void Player_WithinAgeRange_AndSpotsAvailable_IsEligible()
    {
        var player = MakePlayer(ageYears: 8);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 20);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 5);

        Assert.Equal(EligibilityStatus.Eligible, result.Status);
        Assert.Equal(15, result.SpotsRemaining);
        Assert.Empty(result.FailureReasons);
    }

    [Fact]
    public void Player_TooYoung_IsIneligible()
    {
        var player = MakePlayer(ageYears: 4);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 20);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
        Assert.Contains(result.FailureReasons, r => r.Contains("minimum age"));
    }

    [Fact]
    public void Player_TooOld_IsIneligible()
    {
        var player = MakePlayer(ageYears: 15);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 20);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
        Assert.Contains(result.FailureReasons, r => r.Contains("maximum age"));
    }

    [Fact]
    public void Player_WrongGenderForRestrictedProgram_IsIneligible()
    {
        var player = MakePlayer(ageYears: 8, gender: "male");
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 20, genderPolicy: "female");

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
        Assert.Contains(result.FailureReasons, r => r.Contains("restricted to female"));
    }

    [Fact]
    public void Player_UnspecifiedGender_PassesRestrictedProgram()
    {
        // Gender policy check is only enforced when the player's gender is known.
        var player = MakePlayer(ageYears: 8, gender: null);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 20, genderPolicy: "female");

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 0);

        Assert.Equal(EligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void ProgramAtCapacity_ReturnsWaitlistOnly()
    {
        var player = MakePlayer(ageYears: 8);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 10);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 10);

        Assert.Equal(EligibilityStatus.WaitlistOnly, result.Status);
        Assert.Equal(0, result.SpotsRemaining);
    }

    [Fact]
    public void ProgramOverCapacity_ReturnsWaitlistOnly()
    {
        var player = MakePlayer(ageYears: 8);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 10);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 12);

        Assert.Equal(EligibilityStatus.WaitlistOnly, result.Status);
    }

    [Fact]
    public void IneligibleAgeTakesPrecedenceOverCapacity_EvenWhenProgramIsFull()
    {
        // A player who fails an eligibility rule must never be quietly offered a waitlist spot.
        var player = MakePlayer(ageYears: 20);
        var program = MakeProgram(minAge: 6, maxAge: 10, capacity: 5);

        var result = Engine.CheckEligibility(player, program, currentEnrollmentCount: 5);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public void MatchPrograms_ExcludesIneligibleAndSortsByRelevance()
    {
        var player = MakePlayer(ageYears: 8);
        var closeMatch = MakeProgram(minAge: 6, maxAge: 10, capacity: 20); // midpoint 8 -> perfect match
        var wideMatch = MakeProgram(minAge: 5, maxAge: 15, capacity: 20); // midpoint 10 -> weaker match
        var tooOld = MakeProgram(minAge: 12, maxAge: 16, capacity: 20);

        var matches = Engine.MatchPrograms(
            player,
            new[] { wideMatch, closeMatch, tooOld },
            new Dictionary<Guid, int>());

        Assert.Equal(2, matches.Count);
        Assert.Equal(closeMatch.Id, matches[0].Program.Id);
        Assert.Equal(wideMatch.Id, matches[1].Program.Id);
        Assert.All(matches, m => Assert.NotEqual(EligibilityStatus.Ineligible, m.EligibilityResult.Status));
    }

    [Fact]
    public void MatchPrograms_IncludesWaitlistCandidates()
    {
        var player = MakePlayer(ageYears: 8);
        var fullProgram = MakeProgram(minAge: 6, maxAge: 10, capacity: 5);

        var matches = Engine.MatchPrograms(
            player,
            new[] { fullProgram },
            new Dictionary<Guid, int> { [fullProgram.Id] = 5 });

        Assert.Single(matches);
        Assert.Equal(EligibilityStatus.WaitlistOnly, matches[0].EligibilityResult.Status);
    }
}

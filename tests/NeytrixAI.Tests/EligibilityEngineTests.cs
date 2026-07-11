using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Services;
using Xunit;
using Prog = NeytrixAI.Domain.Entities.Program;

namespace NeytrixAI.Tests;

public class EligibilityEngineTests
{
    private readonly EligibilityEngine _engine = new();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Guardian = Guid.NewGuid();

    private static Prog OpenProgram(int minAge = 8, int maxAge = 12, int capacity = 10, string genderPolicy = "all")
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
        return Prog.Create(Tenant, "U12 Soccer", "soccer", minAge, maxAge, capacity, 10000,
            start, start.AddMonths(3), genderPolicy);
    }

    private static Player PlayerAgedAtStart(int ageYears, Prog program, string? gender = null)
    {
        // DOB such that player is exactly ageYears at program start.
        var dob = program.StartDate.AddYears(-ageYears);
        return Player.Create(Tenant, Guardian, "Kid", "Test", dob, gender);
    }

    [Fact]
    public void EligibleWhenAgeInRangeAndCapacityAvailable()
    {
        var program = OpenProgram();
        var player = PlayerAgedAtStart(10, program);

        var result = _engine.CheckEligibility(player, program, currentEnrollmentCount: 0);

        Assert.Equal(EligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void IneligibleWhenTooYoung()
    {
        var program = OpenProgram(minAge: 8, maxAge: 12);
        var player = PlayerAgedAtStart(6, program);

        var result = _engine.CheckEligibility(player, program, 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
        Assert.NotEmpty(result.FailureReasons);
    }

    [Fact]
    public void IneligibleWhenTooOld()
    {
        var program = OpenProgram(minAge: 8, maxAge: 12);
        var player = PlayerAgedAtStart(15, program);

        var result = _engine.CheckEligibility(player, program, 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public void WaitlistOnlyWhenAtCapacity()
    {
        var program = OpenProgram(capacity: 5);
        var player = PlayerAgedAtStart(10, program);

        var result = _engine.CheckEligibility(player, program, currentEnrollmentCount: 5);

        Assert.Equal(EligibilityStatus.WaitlistOnly, result.Status);
    }

    [Fact]
    public void IneligibleWhenRegistrationClosed()
    {
        var program = OpenProgram();
        program.Deactivate(); // registration no longer open
        var player = PlayerAgedAtStart(10, program);

        var result = _engine.CheckEligibility(player, program, 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public void IneligibleWhenGenderPolicyDoesNotMatch()
    {
        var program = OpenProgram(genderPolicy: "female");
        var player = PlayerAgedAtStart(10, program, gender: "male");

        var result = _engine.CheckEligibility(player, program, 0);

        Assert.Equal(EligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public void MatchProgramsExcludesIneligible()
    {
        var good = OpenProgram(minAge: 8, maxAge: 12);
        var tooOld = OpenProgram(minAge: 15, maxAge: 18);
        var player = PlayerAgedAtStart(10, good);

        var matches = _engine.MatchPrograms(player, new[] { good, tooOld },
            new Dictionary<Guid, int>());

        Assert.Single(matches);
        Assert.Equal(good.Id, matches[0].Program.Id);
    }
}

using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Services;

/// <summary>
/// Deterministic, rule-based eligibility engine.
/// The LLM NEVER calls this directly - it goes through typed tool contracts.
/// All rules are pure functions with no side effects.
/// </summary>
public sealed class EligibilityEngine
{
    /// <summary>Checks all eligibility rules for a player against a program.</summary>
    public EligibilityResult CheckEligibility(
        Player player,
        Program program,
        int currentEnrollmentCount)
    {
        var failures = new List<string>();

        // Rule 1: Program must be open for registration
        if (!program.IsRegistrationOpen)
            failures.Add("Program registration is currently closed.");

        // Rule 2: Age eligibility - calculated against program start date
        var ageAtStart = player.AgeAtDate(program.StartDate);
        if (ageAtStart < program.MinAgeYears)
            failures.Add($"Player will be {ageAtStart} at program start; minimum age is {program.MinAgeYears}.");
        if (ageAtStart > program.MaxAgeYears)
            failures.Add($"Player will be {ageAtStart} at program start; maximum age is {program.MaxAgeYears}.");

        // Rule 3: Gender policy
        if (program.GenderPolicy != "all" && player.Gender != null)
        {
            if (player.Gender != program.GenderPolicy)
                failures.Add($"Program is restricted to {program.GenderPolicy} players.");
        }

        // Rule 4: Capacity check
        var spotsRemaining = program.Capacity - currentEnrollmentCount;
        var isWaitlistCandidate = spotsRemaining <= 0;

        if (failures.Count > 0)
            return EligibilityResult.Ineligible(failures);

        return isWaitlistCandidate
            ? EligibilityResult.WaitlistOnly(spotsRemaining)
            : EligibilityResult.Eligible(spotsRemaining);
    }

    /// <summary>
    /// Returns all programs a player is eligible for, sorted by relevance.
    /// Relevance = programs where age is closer to midpoint of allowed range.
    /// </summary>
    public IReadOnlyList<ProgramMatch> MatchPrograms(
        Player player,
        IEnumerable<Program> programs,
        IDictionary<Guid, int> enrollmentCounts)
    {
        var matches = new List<ProgramMatch>();

        foreach (var program in programs)
        {
            var count = enrollmentCounts.TryGetValue(program.Id, out var c) ? c : 0;
            var result = CheckEligibility(player, program, count);

            if (result.Status != EligibilityStatus.Ineligible)
            {
                var ageAtStart = player.AgeAtDate(program.StartDate);
                var midpoint = (program.MinAgeYears + program.MaxAgeYears) / 2.0;
                var relevanceScore = 1.0 - Math.Abs(ageAtStart - midpoint) / Math.Max(1, program.MaxAgeYears - program.MinAgeYears);

                matches.Add(new ProgramMatch(program, result, relevanceScore));
            }
        }

        return matches.OrderByDescending(m => m.RelevanceScore).ToList().AsReadOnly();
    }
}

public enum EligibilityStatus { Eligible, WaitlistOnly, Ineligible }

public sealed record EligibilityResult(
    EligibilityStatus Status,
    int SpotsRemaining,
    IReadOnlyList<string> FailureReasons)
{
    public static EligibilityResult Eligible(int spots) =>
        new(EligibilityStatus.Eligible, spots, Array.Empty<string>());

    public static EligibilityResult WaitlistOnly(int spots) =>
        new(EligibilityStatus.WaitlistOnly, spots, Array.Empty<string>());

    public static EligibilityResult Ineligible(IEnumerable<string> reasons) =>
        new(EligibilityStatus.Ineligible, 0, reasons.ToList().AsReadOnly());
}

public sealed record ProgramMatch(
    Program Program,
    EligibilityResult EligibilityResult,
    double RelevanceScore);

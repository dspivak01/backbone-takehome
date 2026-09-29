using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reporting;

/// <summary>
/// The gap log grouped as the gaps command and the review page both show it: by the kind of fact
/// the model suggested it was looking for, largest group first, each with the questions that led
/// to its passages.
/// </summary>
public static class GapLog
{
    public const string NotSuggested = "not suggested";

    public sealed record Group(string Kind, List<GapRow> Passages, List<string> Questions);

    public static List<Group> Groups(IEnumerable<GapRow> gaps) =>
        gaps.GroupBy(g => g.SuggestedKind ?? NotSuggested).OrderByDescending(g => g.Count())
            .Select(g => new Group(g.Key, g.ToList(), g.Select(x => x.Question).Distinct().ToList()))
            .ToList();
}

using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Reconciliation;

/// <summary>One assertion's value for one field of an event.</summary>
public sealed class Statement
{
    public required string Value { get; init; }
    public required Assertion Source { get; init; }
    public int Tier { get; init; }
    public DateTime? Time { get; init; }

    /// <summary>True when the value came from an arrival or departure field on an attendance or desk record.</summary>
    public bool FromRoster { get; init; }

    /// <summary>
    /// True when the statement says the patient attended. Only these are tested by R6. A cancellation
    /// entered before the appointment is a valid statement and must not be set aside.
    /// </summary>
    public bool AssertsAttendance { get; init; }
}

/// <summary>A correction to one field: the value it replaces and the value it puts in its place.</summary>
public sealed class CorrectionStatement
{
    public required Assertion Source { get; init; }
    public string? OldValue { get; init; }
    public required string NewValue { get; init; }
    public int Tier { get; init; }
    public DateTime? Time { get; init; }
}

/// <summary>
/// Decides the value of one field from everything the record says about it.
/// The order is fixed: set aside statements that cannot be evidence (R6, then tier 3),
/// apply corrections (R3), then let the highest tier decide (R4) and report what disagreed (R5).
/// </summary>
public static class FieldResolver
{
    public static FieldDecision Resolve(
        string field,
        IEnumerable<Statement> statements,
        IEnumerable<CorrectionStatement> corrections,
        DateTime? sessionBegins,
        List<Discrepancy> discrepancies)
    {
        var decision = new FieldDecision { Field = field };
        var standing = new List<Statement>();
        var all = statements.ToList();

        foreach (var s in all)
        {
            // R6: a statement made before the session began cannot confirm that the patient attended it.
            if (s.AssertsAttendance && sessionBegins is { } begins && s.Time is { } time && time < begins)
            {
                decision.Overruled.Add(Overrule(s, "R6", $"recorded at {time:yyyy-MM-dd HH:mm}, before the session began at {begins:yyyy-MM-dd HH:mm}"));
                continue;
            }

            // Tier 3 sources are compared and reported, never used.
            if (s.Tier >= 3)
            {
                decision.Overruled.Add(Overrule(s, "R4", "an unsigned draft or billing entry is not evidence of what happened"));
                continue;
            }

            standing.Add(s);
        }

        // R3: corrections, oldest first.
        foreach (var c in corrections.Where(c => c.Tier <= 2).OrderBy(c => c.Time ?? DateTime.MaxValue))
        {
            var hadStatements = standing.Count > 0;
            var replaced = standing.Where(s => Replaces(c, s)).ToList();
            foreach (var s in replaced)
            {
                standing.Remove(s);
                var what = s.Source.IsCopy ? "a copy of the record that was corrected" : "the value the correction replaces";
                decision.Overruled.Add(Overrule(s, "R3", $"{what}; corrected to {Words(field, c.NewValue)} by {c.Source.Citation}"));
            }

            if (!hadStatements)
            {
                discrepancies.Add(new Discrepancy
                {
                    Rule = "R3",
                    Description = $"A correction sets {field} to {Words(field, c.NewValue)}, but the record it corrects was not supplied. The correction stands as a signed assertion.",
                    Citations = [c.Source.Citation],
                });
            }
            else if (c.OldValue is not null && replaced.Count == 0)
            {
                discrepancies.Add(new Discrepancy
                {
                    Rule = "R3",
                    Description = $"A correction says it replaces {field} {Words(field, c.OldValue)}, but no supplied record states that value. Nothing was replaced and the values are compared as they stand.",
                    Citations = [c.Source.Citation],
                });
            }

            standing.Add(new Statement { Value = c.NewValue, Source = c.Source, Tier = c.Tier, Time = c.Time });
        }

        if (standing.Count == 0)
        {
            decision.Status = "missing";
            decision.Rule = "R4";
            decision.Explanation = all.Count == 0
                ? "No record states this."
                : "Only sources that cannot be used as evidence state this.";
            return decision;
        }

        var candidates = standing
            .GroupBy(s => s.Value, StringComparer.Ordinal)
            .Select(g => new
            {
                Candidate = new Candidate
                {
                    Value = g.Key,
                    Tier = g.Min(s => s.Tier),
                    Citations = g.Select(s => s.Source.Citation).Distinct().Order(StringComparer.Ordinal).ToList(),
                    AssertionIds = g.Select(s => s.Source.Id).Distinct().Order().ToList(),
                },
                Statements = g.ToList(),
            })
            .OrderBy(c => c.Candidate.Value, StringComparer.Ordinal)
            .ToList();

        var topTier = candidates.Min(c => c.Candidate.Tier);
        var top = candidates.Where(c => c.Candidate.Tier == topTier).ToList();
        var lower = candidates.Where(c => c.Candidate.Tier > topTier).ToList();
        decision.Tier = topTier;

        if (top.Count == 1)
        {
            decision.Status = "resolved";
            decision.Value = top[0].Candidate.Value;
            decision.Rule = "R4";
            decision.Candidates = [top[0].Candidate];
            decision.Explanation = $"{Capital(Tiers.Describe(topTier))} states {Words(field, decision.Value)}.";
        }
        else
        {
            // D14: records of the same standing disagree. Lower-tier evidence settles it only
            // when it supports exactly one of the values.
            var corroborated = top.Where(c => c.Statements.Any(s => s.Tier > topTier)).ToList();
            if (corroborated.Count == 1)
            {
                decision.Status = "resolved_by_corroboration";
                decision.Value = corroborated[0].Candidate.Value;
                decision.Rule = "R4 with corroboration";
                decision.Candidates = [corroborated[0].Candidate];
                decision.Explanation =
                    $"Records of {Tiers.Describe(topTier)} state {string.Join(" and ", top.Select(c => Words(field, c.Candidate.Value)))}. " +
                    $"An independent lower-tier record supports {Words(field, decision.Value)} and none supports the other.";
                foreach (var loser in top.Where(c => c != corroborated[0]))
                    foreach (var s in loser.Statements)
                        decision.Overruled.Add(Overrule(s, "R4 with corroboration", $"not corroborated; {Words(field, decision.Value)} is supported by an independent record"));
            }
            else
            {
                decision.Status = "unresolved";
                decision.Rule = "R4";
                decision.Candidates = top.Select(c => c.Candidate).ToList();
                decision.Explanation =
                    $"Records of {Tiers.Describe(topTier)} state {string.Join(" and ", top.Select(c => Words(field, c.Candidate.Value)))}, " +
                    "and no correction or independent record settles it.";
                discrepancies.Add(new Discrepancy
                {
                    Rule = "R4",
                    Description = $"Unresolved {field}: {string.Join(" versus ", top.Select(c => $"{Words(field, c.Candidate.Value)} ({string.Join(", ", c.Candidate.Citations)})"))}.",
                    Citations = top.SelectMany(c => c.Candidate.Citations).Distinct().ToList(),
                });
            }
        }

        // R5: lower-tier statements that disagree are reported and change nothing.
        foreach (var c in lower.Where(c => decision.IsSettled ? c.Candidate.Value != decision.Value : true))
        {
            foreach (var s in c.Statements)
                decision.Overruled.Add(Overrule(s, "R5", $"{Tiers.Describe(s.Tier)} disagrees with {Tiers.Describe(topTier)}"));
            discrepancies.Add(new Discrepancy
            {
                Rule = "R5",
                Description = $"{Capital(Tiers.Describe(c.Candidate.Tier))} states {field} {Words(field, c.Candidate.Value)}, which differs from the higher-tier record.",
                Citations = c.Candidate.Citations,
            });
        }

        return decision;
    }

    /// <summary>A value as words, so a sentence never carries a code such as took_place.</summary>
    private static string Words(string field, string? value) => Vocabulary.DescribeValue(field, value);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool Replaces(CorrectionStatement correction, Statement statement)
    {
        if (correction.OldValue is null || statement.Value != correction.OldValue) return false;
        if (statement.Source.DocHash == correction.Source.DocHash) return false;
        if (correction.Time is null) return true;
        if (statement.Time is null) return statement.Source.IsCopy;
        return statement.Time < correction.Time;
    }

    private static Overruled Overrule(Statement s, string rule, string reason) => new()
    {
        Value = s.Value,
        Citation = s.Source.Citation,
        AssertionId = s.Source.Id,
        Rule = rule,
        Reason = reason,
    };
}

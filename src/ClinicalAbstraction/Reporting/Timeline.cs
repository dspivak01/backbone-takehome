using System.Text;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Reporting;

/// <summary>A readable account of everything the abstraction holds for one patient, in date order.</summary>
public static class Timeline
{
    public static string Render(PatientAbstraction p)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {p.Name ?? "Name not recorded"} ({p.PatientKey})\n");
        b.AppendLine(Markdown.Table(["Item", "Value"],
        [
            ["Date of birth", p.DateOfBirth ?? "not recorded"],
            ["Episode", $"{p.Episode.Start:yyyy-MM-dd} to {p.Episode.End:yyyy-MM-dd}" + (p.Episode.Citations.Count > 0 ? $" ({string.Join(", ", p.Episode.Citations)})" : "")],
            ["Documents", p.DocumentCount.ToString()],
            ["Assertions", p.AssertionCount.ToString()],
            ["Encounters", p.Encounters.Count.ToString()],
            ["Abstraction version", p.Version],
        ]));

        b.AppendLine("## Treatment plan versions\n");
        b.AppendLine(Markdown.Table(["In effect", "Minimum therapy days per week", "Minimum minutes per week", "Week", "Counts", "Does not count", "Sources"],
            p.Plans.Select(plan => new[]
            {
                $"{plan.EffectiveFrom:yyyy-MM-dd} to {plan.EffectiveTo:yyyy-MM-dd}", plan.MinTherapyDaysPerWeek?.ToString() ?? "not stated", plan.MinMinutesPerWeek?.ToString() ?? "not stated",
                plan.WeekDefinition == "sunday_saturday" ? "Sunday to Saturday" : "Monday to Sunday",
                string.Join(", ", plan.CountedCategories.Select(c => Vocabulary.DescribeCategory(c))),
                string.Join(", ", plan.ExcludedCategories.Select(c => Vocabulary.DescribeCategory(c))),
                string.Join(", ", plan.Citations),
            })));

        b.AppendLine("## Encounters\n");
        b.AppendLine(Markdown.Table(["Date", "Encounter", "Service", "Conclusion", "Patient-present minutes", "Counted by the plan", "Documents"],
            p.Encounters.Select(e =>
            {
                var line = WeeklyCalculator.Judge(p, e);
                return new[]
                {
                    $"{e.ServiceDate:yyyy-MM-dd ddd}", e.EncounterId ?? e.EventKey, Vocabulary.DescribeCategory(e.Category.Value),
                    e.Occurrence == "not_session" ? e.NotSessionReason ?? "Not a session" : Markdown.Sentence(e.Occurrence),
                    Markdown.Range(e.MinutesMin, e.MinutesMax), Markdown.Sentence(line.Status.Replace('_', ' ')), string.Join(", ", e.SourceDocuments),
                };
            })));

        b.AppendLine("## Symptom assessments\n");
        b.AppendLine(Markdown.Table(["Instrument", "Completed", "Total", "Sources", "Copies and later mentions"],
            p.Measurements.Select(m => new[]
            {
                m.Instrument, m.CompletedAt ?? m.CompletedOn?.ToString("yyyy-MM-dd") ?? "not recorded", m.TotalScore?.ToString() ?? "conflicting",
                string.Join(", ", m.OriginalCitations), m.RepeatCitations.Count == 0 ? "none" : string.Join(", ", m.RepeatCitations),
            })));

        if (p.Episode is { Start: { } start, End: { } end })
        {
            b.AppendLine("## Weeks\n");
            b.AppendLine(Markdown.Table(["Week", "Therapy days", "Minutes", "Arithmetic", "Result"],
                WeeklyCalculator.Weeks(p, start, end).Select(w => new[]
                {
                    $"{w.WeekStart} to {w.WeekEnd}{(w.PartialWeek ? " (partial)" : "")}", Markdown.Range(w.DaysMin, w.DaysMax),
                    Markdown.Range(w.MinutesMin, w.MinutesMax), w.MinutesArithmetic, WeeklyCalculator.Words(w.Result),
                })));
        }

        if (p.Unlinked.Count > 0)
        {
            b.AppendLine("## Assertions that could not be linked to an encounter\n");
            b.AppendLine(Markdown.Table(["Source", "Kind", "Reason", "Quote"],
                p.Unlinked.Select(u => new[] { u.Citation, u.Kind.Replace('_', ' '), u.Reason, $"\"{u.Quote}\"" })));
        }

        if (p.Warnings.Count > 0) b.AppendLine("## Warnings\n\n" + Markdown.List(p.Warnings));

        b.AppendLine("## Each encounter in full\n");
        foreach (var e in p.Encounters)
        {
            b.AppendLine($"### {e.EncounterId ?? e.EventKey} on {e.ServiceDate:yyyy-MM-dd}: {Vocabulary.DescribeCategory(e.Category.Value)}\n");
            b.AppendLine(Markdown.Event(e));
        }
        return b.ToString();
    }
}

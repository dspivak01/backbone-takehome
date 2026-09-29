using System.Text;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Reporting;

/// <summary>
/// Turns calculation results into readable tables. Every number a reviewer sees in these tables
/// comes straight from a calculation result. No model writes or edits them.
/// </summary>
public static class Markdown
{
    public static string Render(object result) => result switch
    {
        SessionCountResult r => Sessions(r),
        WeeklyResult r => Weekly(r),
        ConsecutiveWeeksResult r => Consecutive(r),
        PlanChangeResult r => PlanChange(r),
        DayResult r => Day(r),
        CollectionDayResult r => CollectionDay(r),
        CollectionSummaryResult r => CollectionSummary(r),
        PatientsInPeriodResult r => PatientsInPeriod(r),
        MeasureSeriesResult r => Measures(r),
        ObservationResult r => Observations(r),
        SearchResult r => Search(r),
        PatientSearchResult r => Patients(r),
        _ => "```json\n" + Json.Write(result, indented: true) + "\n```\n",
    };

    public static string Parameters(ResolvedParameters p) =>
        Table(["Parameter", "Value", "Source"],
        [
            ["Patient", $"{p.PatientName ?? "name not recorded"} ({p.PatientKey})", "Matched by medical record number"],
            ["Dates", $"{p.From} to {p.To}", Sentence(p.DateRangeSource)],
            ["Week", p.WeekDefinition.Split(',')[0], Sentence(p.WeekDefinition.Contains(',') ? p.WeekDefinition.Split(',', 2)[1].Trim() : "")],
            ["Abstraction version", p.AbstractionVersion, "Changes only when a conclusion about this patient changes"],
        ]);

    private static string Sessions(SessionCountResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        b.AppendLine("**Sessions attended**\n");
        var rows = r.SessionsByCategory.Select(kv => new[] { kv.Key, kv.Value.ToString() }).ToList();
        rows.Add(["Total", Range(r.TotalSessions, r.TotalSessionsMax)]);
        rows.Add(["Distinct days", Range(r.DistinctDays, r.DistinctDaysMax)]);
        b.AppendLine(Table(["Service type", "Sessions"], rows));

        b.AppendLine("**Counted sessions**\n");
        b.AppendLine(Contacts(r.Counted));
        if (r.Uncertain.Count > 0)
        {
            b.AppendLine("**Contacts that may or may not count**\n");
            b.AppendLine(Contacts(r.Uncertain));
        }
        b.AppendLine("**Appointments and contacts not counted**\n");
        b.AppendLine(Table(["Date", "Encounter", "Service", "Why it is not counted", "Sources"],
            r.NotCounted.Select(c => new[] { c.Date, c.EncounterId ?? c.EventKey, c.CategoryName, c.Reason, string.Join(", ", c.Citations.Count > 0 ? c.Citations : c.SourceDocuments) })));

        var discrepancies = r.Counted.Concat(r.Uncertain).Concat(r.NotCounted).Where(c => c.Discrepancies.Count > 0).ToList();
        if (discrepancies.Count > 0)
        {
            b.AppendLine("**Records that disagree with the conclusion**\n");
            b.AppendLine(List(discrepancies.SelectMany(c => c.Discrepancies.Select(d => $"{c.Date} {c.EncounterId ?? c.EventKey}: {d}"))));
        }
        if (r.DescribedMoreThanOnce.Count > 0)
        {
            b.AppendLine("**Contacts described by more than one document, counted once**\n");
            b.AppendLine(List(r.DescribedMoreThanOnce));
        }
        if (r.RecordsThatAreNotAppointments.Count > 0)
        {
            b.AppendLine("**Records that are not appointments**\n");
            b.AppendLine(List(r.RecordsThatAreNotAppointments));
        }
        if (r.UnlinkedAssertions.Count > 0)
        {
            b.AppendLine("**Assertions that could not be linked to an encounter, left out of the counts**\n");
            b.AppendLine(List(r.UnlinkedAssertions));
        }
        if (r.Warnings.Count > 0) b.AppendLine("**Warnings**\n\n" + List(r.Warnings));
        return b.ToString();
    }

    private static string Contacts(IEnumerable<ContactLine> contacts) =>
        Table(["Date", "Encounter", "Service", "Minutes", "How the minutes were worked out", "Sources"],
            contacts.Select(c => new[]
            {
                $"{c.Date} {(c.Weekday.Length >= 3 ? c.Weekday[..3] : c.Weekday)}", c.EncounterId ?? c.EventKey, c.CategoryName,
                Range(c.Counted ? c.MinutesMin : 0, c.MinutesMax), c.Counted ? c.Arithmetic : c.Reason, string.Join(", ", c.Citations),
            }));

    private static string Weekly(WeeklyResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        b.AppendLine("**By week**\n");
        b.AppendLine(Table(["Week", "Therapy days", "Minutes", "Hours", "Arithmetic", "Goal", "Result"],
            r.Weeks.Select(w => new[]
            {
                $"{w.WeekStart} to {w.WeekEnd}{(w.PartialWeek ? " (partial)" : "")}",
                Range(w.DaysMin, w.DaysMax), Range(w.MinutesMin, w.MinutesMax), Hours(w.HoursMin, w.HoursMax), w.MinutesArithmetic,
                w.RequiredDays is null && w.RequiredMinutes is null ? "None in effect" : $"{w.RequiredDays?.ToString() ?? "no"} days and {w.RequiredMinutes?.ToString() ?? "no"} minutes",
                WeeklyCalculator.Words(w.Result),
            })));
        b.AppendLine($"**Total**: {Range(r.TotalMinutesMin, r.TotalMinutesMax)} minutes, {Hours(r.TotalHoursMin, r.TotalHoursMax)} hours. {r.TotalArithmetic}.\n");

        b.AppendLine("**Why each week has its result**\n");
        b.AppendLine(List(r.Weeks.Select(w =>
            $"Week of {w.WeekStart}: {w.Reason}" +
            (w.PlanCitations.Count > 0 ? $" Goal from {string.Join(", ", w.PlanCitations)}." : "") +
            (w.PlanNote is null ? "" : $" {w.PlanNote}") +
            (w.PartialWeekNote is null ? "" : $" {w.PartialWeekNote}") +
            (w.Needs.Count == 0 ? "" : $" Would be settled by: {string.Join(" ", w.Needs)}"))));

        b.AppendLine("**Contacts behind the totals**\n");
        b.AppendLine(Table(["Date", "Encounter", "Service", "Counted", "Minutes", "Detail", "Sources"],
            r.Weeks.SelectMany(w => w.Contacts).Select(c => new[]
            {
                $"{c.Date} {(c.Weekday.Length >= 3 ? c.Weekday[..3] : c.Weekday)}", c.EncounterId ?? c.EventKey, c.CategoryName,
                c.Status.Replace('_', ' '), c.Counted || c.MayCount ? Range(c.Counted ? c.MinutesMin : 0, c.MinutesMax) : "0",
                c.Counted ? c.Arithmetic : c.Reason, string.Join(", ", c.Citations.Count > 0 ? c.Citations : c.SourceDocuments),
            })));

        if (r.Unsettled.Count > 0) b.AppendLine("**What the record does not settle**\n\n" + List(r.Unsettled));
        if (r.Assumptions.Count > 0) b.AppendLine("**Assumptions**\n\n" + List(r.Assumptions));
        if (r.Warnings.Count > 0) b.AppendLine("**Warnings**\n\n" + List(r.Warnings));
        return b.ToString();
    }

    private static string Consecutive(ConsecutiveWeeksResult r)
    {
        var b = new StringBuilder();
        b.AppendLine($"Patients examined: {r.PatientsExamined}. Period: {r.From} to {r.To}.\n");
        b.AppendLine(Table(["Group", "Patients"],
        [
            ["Two consecutive weeks below the goal", r.Included.Count == 0 ? "None" : string.Join(", ", r.Included.Select(Name))],
            ["Inclusion depends on unresolved documentation", r.DependsOnUnresolvedDocumentation.Count == 0 ? "None" : string.Join(", ", r.DependsOnUnresolvedDocumentation.Select(Name))],
            ["Below only when a partial week is counted", r.OnlyWithPartialWeek.Count == 0 ? "None" : string.Join(", ", r.OnlyWithPartialWeek.Select(Name))],
            ["Not included", r.NotIncluded.Count == 0 ? "None" : string.Join(", ", r.NotIncluded)],
        ]));

        foreach (var p in r.Included.Concat(r.DependsOnUnresolvedDocumentation).Concat(r.OnlyWithPartialWeek))
        {
            b.AppendLine($"**{Name(p)}**: {p.Status.Replace('_', ' ')}\n");
            b.AppendLine(Table(["Pair", "Kind", "First week", "Second week", "Would be settled by"],
                p.DefinitePairs.Select(x => Pair(x, "Definite"))
                    .Concat(p.PossiblePairs.Select(x => Pair(x, "Possible")))
                    .Concat(p.PairsWithPartialWeek.Select(x => Pair(x, "Includes a partial week")))));
        }
        b.AppendLine("**Assumptions**\n\n" + List(r.Assumptions));
        return b.ToString();
    }

    private static string Name(PatientConsecutive p) => $"{p.PatientName ?? "name not recorded"} ({p.PatientKey})";

    private static string[] Pair(WeekPair x, string kind) =>
        [
            $"{x.FirstWeek} and {x.SecondWeek}", kind,
            $"{x.FirstResult}. {x.FirstDetail}{WeekSources(x.FirstGoalSources, x.FirstContactSources)}",
            $"{x.SecondResult}. {x.SecondDetail}{WeekSources(x.SecondGoalSources, x.SecondContactSources)}",
            x.WouldBeSettledBy.Count == 0 ? "Nothing needed" : string.Join(" ", x.WouldBeSettledBy),
        ];

    /// <summary>The sources behind one week of a pair: the plan that set the goal, then the week's contacts.</summary>
    public static string WeekSources(List<string> goal, List<string> contacts) =>
        (goal.Count > 0 ? $" Goal from {string.Join(", ", goal)}." : "") + (contacts.Count > 0 ? $" Contacts: {string.Join(", ", contacts)}." : "");

    private static string PlanChange(PlanChangeResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        b.AppendLine(r.Summary + "\n");
        if (r.Periods.Count > 0)
        {
            b.AppendLine(Table(["Period", "Days", "Goal", "Sessions", "Minutes", "Sessions per week", "Minutes per week", "Plan source"],
                r.Periods.Select(p => new[]
                {
                    $"{p.From} to {p.To}", p.Days.ToString(), $"{p.RequiredDays?.ToString() ?? "no"} days and {p.RequiredMinutes?.ToString() ?? "no"} minutes",
                    p.TotalSessions.ToString(), Range(p.MinutesMin, p.MinutesMax), p.SessionsPerWeek.ToString("0.##"),
                    p.MinutesPerWeekMin == p.MinutesPerWeekMax ? p.MinutesPerWeekMin.ToString("0.#") : $"{p.MinutesPerWeekMin:0.#} to {p.MinutesPerWeekMax:0.#}",
                    string.Join(", ", p.PlanCitations),
                })));
            b.AppendLine("**By service type**\n");
            b.AppendLine(Table(["Period", "Service", "Sessions", "Minutes"],
                r.Periods.SelectMany(p => p.SessionsByCategory.Select(kv => new[] { $"{p.From} to {p.To}", kv.Key, kv.Value.ToString(), p.MinutesByCategory.GetValueOrDefault(kv.Key, "0") }))));
        }
        b.AppendLine("**Assumptions**\n\n" + List(r.Assumptions));
        return b.ToString();
    }

    /// <summary>
    /// One patient's day. The heading level of each encounter can be lowered, so the same tables
    /// sit under a patient's heading when a day covers several patients.
    /// </summary>
    private static string Day(DayResult r, string encounterHeading = "###")
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        b.AppendLine($"**{r.Date}**: {Range(r.TherapyContacts, r.TherapyContactsMax)} therapy contact(s), {Range(r.MinutesMin, r.MinutesMax)} patient therapy minutes. {r.MinutesArithmetic}.\n");
        b.AppendLine(Table(["Encounter", "Service", "Counted", "Minutes", "Detail"],
            r.Contacts.Select(c => new[] { c.EncounterId ?? c.EventKey, c.CategoryName, c.Status.Replace('_', ' '), Range(c.Counted ? c.MinutesMin : 0, c.MinutesMax), c.Counted ? c.Arithmetic : c.Reason })));

        foreach (var e in r.Events)
        {
            b.AppendLine($"{encounterHeading} {e.EncounterId ?? e.EventKey}: {Vocabulary.DescribeCategory(e.Category.Value)}\n");
            b.AppendLine(Event(e));
        }
        if (r.OtherRecords.Count > 0) b.AppendLine("**Other records dated this day**\n\n" + List(r.OtherRecords));
        if (r.Observations.Count > 0)
        {
            b.AppendLine("**Observations recorded for this day**\n");
            b.AppendLine(ObservationTable(r.Observations));
        }
        return b.ToString();
    }

    private static string PatientsInPeriod(PatientsInPeriodResult r)
    {
        var b = new StringBuilder();
        b.AppendLine($"Period: {r.From} to {r.To}. Both dates are included.\n");
        b.AppendLine(string.Join(" ", r.Notes) + "\n");
        b.AppendLine(Table(["Patient", "Appointments and contacts recorded", "Days with an appointment or contact", "First", "Last"],
            r.Patients.Select(p => new[] { Name(p.PatientName, p.PatientKey), p.ContactsRecorded.ToString(), p.DaysWithContacts.ToString(), p.FirstContact, p.LastContact })));
        return b.ToString();
    }

    private static string CollectionDay(CollectionDayResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(string.Join(" ", r.Notes) + "\n");
        b.AppendLine($"**Every patient with an appointment or contact on {r.Date}**\n");
        b.AppendLine(Table(["Patient", "Appointments and contacts recorded", "Therapy contacts", "Patient therapy minutes"],
            r.Summary.Select(p => new[]
            {
                Name(p.PatientName, p.PatientKey), p.ContactsRecorded.ToString(),
                Range(p.TherapyContacts, p.TherapyContactsMax), Range(p.MinutesMin, p.MinutesMax),
            })));

        // In a saved answer each calculation has a heading of its own, so patients sit one level below it.
        foreach (var patient in r.Patients)
        {
            b.AppendLine($"#### {Name(patient.Parameters.PatientName, patient.Parameters.PatientKey)}\n");
            b.AppendLine(Day(patient, "#####"));
        }
        return b.ToString();
    }

    private static string CollectionSummary(CollectionSummaryResult r)
    {
        var b = new StringBuilder();
        b.AppendLine($"Period: {r.From} to {r.To}. {Sentence(r.DateRangeSource)}.\n");
        b.AppendLine(string.Join(" ", r.Notes) + "\n");

        // One column for each service type that any patient attended, so every row has the same columns.
        var services = r.Patients.SelectMany(p => p.SessionsByCategory.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        b.AppendLine("**Sessions attended, by patient**\n");
        b.AppendLine(Table(["Patient", "Period examined", .. services, "Total sessions", "Distinct therapy days", "Minutes"],
            r.Patients.Select(p => (string[])
            [
                Name(p.PatientName, p.PatientKey), $"{p.From} to {p.To}",
                .. services.Select(s => p.SessionsByCategory.GetValueOrDefault(s, 0).ToString()),
                Range(p.TotalSessions, p.TotalSessionsMax), Range(p.DistinctDays, p.DistinctDaysMax), Range(p.MinutesMin, p.MinutesMax),
            ])));

        b.AppendLine("**Weeks against the treatment plan goal, by patient**\n");
        b.AppendLine(Table(["Patient", "Weeks examined", "Goal met", "Goal not met", "Cannot be determined", "No goal in effect", "Weeks only partly inside the period"],
            r.Patients.Select(p => new[]
            {
                Name(p.PatientName, p.PatientKey), p.WeeksExamined.ToString(), p.WeeksMet.ToString(), p.WeeksNotMet.ToString(),
                p.WeeksUndetermined.ToString(), p.WeeksWithNoGoal.ToString(), p.PartialWeeks.ToString(),
            })));

        if (r.Assumptions.Count > 0) b.AppendLine("**Assumptions**\n\n" + List(r.Assumptions));
        var warnings = r.Patients.SelectMany(p => p.Warnings.Select(w => $"{Name(p.PatientName, p.PatientKey)}: {w}")).ToList();
        if (warnings.Count > 0) b.AppendLine("**Warnings**\n\n" + List(warnings));
        return b.ToString();
    }

    private static string Name(string? name, string patientKey) => $"{name ?? "name not recorded"} ({patientKey})";

    /// <summary>One event with every decision, every source, and every assertion that was set aside.</summary>
    public static string Event(EncounterEvent e)
    {
        var b = new StringBuilder();
        b.AppendLine(Table(["Field", "Decision", "Value", "Rule", "Explanation", "Sources"],
            new[] { e.Category, e.Disposition, e.PatientPresent, e.Start, e.End }.Select(d => new[]
            {
                Sentence(d.Field), d.Status.Replace('_', ' '),
                d.IsSettled ? d.Value!.Replace('_', ' ') : d.Candidates.Count > 0 ? string.Join(" or ", d.Candidates.Select(c => c.Value)) : "not stated",
                d.Rule, d.Explanation, string.Join(", ", d.Candidates.SelectMany(c => c.Citations).Distinct()),
            })));

        var facts = new List<string>
        {
            $"Conclusion: {e.Occurrence.Replace('_', ' ')}. {Domain_Range(e)}. {e.Arithmetic}",
        };
        if (e.SessionInterval is { } s) facts.Add($"Session interval: {s} ({s.Reason}; {string.Join(", ", s.Citations)}).");
        facts.AddRange(e.Excluded.Select(x => $"Removed: {x.Reason} {x} ({string.Join(", ", x.Citations)})."));
        facts.AddRange(e.Flags.Select(f => $"Note: {f}"));
        facts.AddRange(e.Needs.Select(n => $"Would be settled by: {n}"));
        b.AppendLine(List(facts));

        var overruled = new[] { e.Category, e.Disposition, e.PatientPresent, e.Start, e.End }.SelectMany(d => d.Overruled.Select(o => (d.Field, o))).ToList();
        if (overruled.Count > 0)
        {
            b.AppendLine("Assertions set aside:\n");
            b.AppendLine(Table(["Field", "Value", "Source", "Rule", "Reason"],
                overruled.Select(x => new[] { Sentence(x.Field), x.o.Value.Replace('_', ' '), x.o.Citation, x.o.Rule, Sentence(x.o.Reason) })));
        }
        if (e.Discrepancies.Count > 0)
        {
            b.AppendLine("Disagreements in the record:\n");
            b.AppendLine(List(e.Discrepancies.Select(d => $"{d.Rule}: {d.Description} ({string.Join(", ", d.Citations)})")));
        }

        b.AppendLine("Every assertion linked to this encounter:\n");
        b.AppendLine(Table(["Source", "Kind", "Record", "Signed", "Tier", "Stated at", "Quote"],
            e.Assertions.Select(a => new[]
            {
                a.Citation, a.Kind.Replace('_', ' '), a.RecordType.Replace('_', ' ') + (a.IsCopy ? " (copy)" : ""), a.Signed.Replace('_', ' '),
                a.Tier.ToString(), a.StatementTime ?? "not stated", $"\"{a.Quote}\"",
            })));
        return b.ToString();
    }

    private static string Domain_Range(EncounterEvent e) => e.MinutesMin == e.MinutesMax ? $"{e.MinutesMin} patient-present minutes" : $"{e.MinutesMin} to {e.MinutesMax} patient-present minutes";

    private static string Measures(MeasureSeriesResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        b.AppendLine($"**Distinct assessments**: {r.DistinctAssessments}\n");
        b.AppendLine(Table(["Instrument", "Completed", "Total", "Change from previous", "Change from first", "Item scores", "Sources", "Copies and later mentions"],
            r.Assessments.Select(a => new[]
            {
                a.Instrument, a.CompletedOn, a.TotalScore?.ToString() ?? "conflicting", Signed(a.ChangeFromPrevious), Signed(a.ChangeFromFirst),
                a.ItemScores.Count == 0 ? "not recorded" : string.Join("; ", a.ItemScores), string.Join(", ", a.Citations),
                a.CopiesAndMentions.Count == 0 ? "none" : string.Join(", ", a.CopiesAndMentions),
            })));
        var discrepancies = r.Assessments.SelectMany(a => a.Discrepancies.Select(d => $"{a.Instrument} {a.CompletedOn}: {d}")).ToList();
        if (discrepancies.Count > 0) b.AppendLine("**Disagreements**\n\n" + List(discrepancies));
        b.AppendLine("**Limits on what these results support**\n\n" + List(r.Limits));
        return b.ToString();
    }

    public static string Signed(int? change) => change is null ? "" : change > 0 ? $"+{change}" : change.ToString()!;

    private static string Observations(ObservationResult r)
    {
        var b = new StringBuilder();
        b.AppendLine(Parameters(r.Parameters));
        if (r.CategoryFilter is not null) b.AppendLine($"Category: {r.CategoryFilter.Replace('_', ' ')}\n");
        b.AppendLine(ObservationTable(r.Observations));
        return b.ToString();
    }

    private static string ObservationTable(IEnumerable<ObservationLine> observations) =>
        Table(["Date", "Category", "What the record says", "Source", "Record"],
            observations.Select(o => new[] { o.Date, o.Category.Replace('_', ' '), $"\"{o.Quote}\"", o.Citation, o.Source + (o.IsCopy ? " (copy)" : "") }));

    private static string Search(SearchResult r)
    {
        var b = new StringBuilder();
        b.AppendLine($"{r.Note}\n\nSearch terms: {string.Join(", ", r.Terms)}. {r.Hits.Count} passage(s) found, {r.GapsRecorded} written to the gap log.\n");
        b.AppendLine(Table(["Source", "Passage", "Already covered by"],
            r.Hits.Select(h => new[] { h.Citation, h.Passage, h.CoveredByAssertion ? string.Join(", ", h.CoveringKinds.Select(k => k.Replace('_', ' '))) : "nothing" })));
        return b.ToString();
    }

    private static string Patients(PatientSearchResult r) =>
        $"{r.Note}\n\n" + Table(["Patient", "MRN", "Date of birth", "Episode", "Plan versions", "Encounters"],
            r.Matches.Select(m => new[] { m.Name ?? "", m.Mrn ?? "", m.DateOfBirth ?? "", $"{m.EpisodeStart} to {m.EpisodeEnd}", m.PlanVersions.ToString(), m.Encounters.ToString() }));

    // ---------- helpers ----------

    public static string Range(int min, int max) => min == max ? $"{min}" : $"{min} to {max}";

    public static string Hours(double min, double max) => min == max ? $"{min:0.##}" : $"{min:0.##} to {max:0.##}";

    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string List(IEnumerable<string> items)
    {
        var b = new StringBuilder();
        foreach (var item in items) b.AppendLine($"- {item.Replace("\n", " ")}");
        return b.AppendLine().ToString();
    }

    public static string Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var head = headers.ToList();
        var b = new StringBuilder();
        b.AppendLine("| " + string.Join(" | ", head) + " |");
        b.AppendLine("|" + string.Join("|", head.Select(_ => "---")) + "|");
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            b.AppendLine("| " + string.Join(" | ", row.Select(Cell)) + " |");
        }
        if (!any) b.AppendLine("| " + string.Join(" | ", head.Select((_, i) => i == 0 ? "None" : "")) + " |");
        return b.AppendLine().ToString();
    }

    private static string Cell(string value) => (value ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
}

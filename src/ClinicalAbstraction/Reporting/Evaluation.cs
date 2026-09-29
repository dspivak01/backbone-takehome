using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reporting;

/// <summary>
/// Scores a saved abstraction against a reference file of expected results. This is a measuring
/// tool for development. The pipeline never reads a reference file.
/// </summary>
public static class Evaluation
{
    public sealed class Reference
    {
        public string PatientKey { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public List<ReferenceEncounter> Encounters { get; set; } = [];
        public List<ReferenceWeek> Weeks { get; set; } = [];
        public Dictionary<string, int> Sessions { get; set; } = [];
        /// <summary>A number, or a range such as "8 to 9" when a contact may or may not count.</summary>
        public string TotalSessions { get; set; } = "";
        public string DistinctDays { get; set; } = "";
        public List<ReferenceMeasure> Measures { get; set; } = [];
        public ReferencePlan? Plan { get; set; }
    }

    public sealed class ReferenceEncounter
    {
        public string Id { get; set; } = "";
        public string Date { get; set; } = "";
        public string Category { get; set; } = "";
        public string Status { get; set; } = "";
        public int MinutesMin { get; set; }
        public int MinutesMax { get; set; }
    }

    public sealed class ReferenceWeek
    {
        public string WeekStart { get; set; } = "";
        public int DaysMin { get; set; }
        public int DaysMax { get; set; }
        public int MinutesMin { get; set; }
        public int MinutesMax { get; set; }
        public string Result { get; set; } = "";
    }

    public sealed class ReferenceMeasure
    {
        public string Instrument { get; set; } = "";
        public string Date { get; set; } = "";
        public int Total { get; set; }
    }

    public sealed class ReferencePlan
    {
        public int MinDays { get; set; }
        public int MinMinutes { get; set; }
        public int Versions { get; set; } = 1;
    }

    public sealed record Check(string Area, string Item, string Expected, string Actual)
    {
        public bool Passed => Expected == Actual;
    }

    public static List<Check> Score(Database database, Reference reference)
    {
        var checks = new List<Check>();
        var calculations = new Calculations(database);
        PatientAbstraction patient;
        try
        {
            patient = calculations.Load(reference.PatientKey);
        }
        catch (InvalidOperationException)
        {
            return [new Check("Patient", reference.PatientKey, "found", "not found")];
        }

        var contacts = WeeklyCalculator.ContactsInRange(patient, DateOnly.Parse(reference.From), DateOnly.Parse(reference.To));
        foreach (var expected in reference.Encounters)
        {
            var actual = contacts.FirstOrDefault(c => string.Equals(c.EncounterId, expected.Id, StringComparison.OrdinalIgnoreCase) || c.EventKey == expected.Id);
            checks.Add(new Check("Encounter", expected.Id,
                $"{expected.Date} {expected.Category} {expected.Status} {Markdown.Range(expected.MinutesMin, expected.MinutesMax)}",
                actual is null ? "missing" : $"{actual.Date} {actual.Category} {actual.Status} {Markdown.Range(actual.Status == "counted" ? actual.MinutesMin : 0, actual.Status is "counted" or "uncertain" or "not_addressed_by_plan" ? actual.MinutesMax : 0)}"));
        }
        // An event the reference does not list is an error only if it could change a count. An
        // administrative contact recorded as "not a session" is a correct description of the record.
        foreach (var extra in contacts.Where(c => reference.Encounters.All(e => !string.Equals(e.Id, c.EncounterId, StringComparison.OrdinalIgnoreCase) && e.Id != c.EventKey)))
            if (extra.Counted || extra.MayCount)
                checks.Add(new Check("Encounter", extra.EncounterId ?? extra.EventKey, "absent", $"{extra.Date} {extra.Category} {extra.Status}"));

        var weekly = calculations.Weekly(reference.PatientKey, reference.From, reference.To);
        foreach (var expected in reference.Weeks)
        {
            var actual = weekly.Weeks.FirstOrDefault(w => w.WeekStart == expected.WeekStart);
            checks.Add(new Check("Week", expected.WeekStart,
                $"days {Markdown.Range(expected.DaysMin, expected.DaysMax)}, minutes {Markdown.Range(expected.MinutesMin, expected.MinutesMax)}, {expected.Result}",
                actual is null ? "missing" : $"days {Markdown.Range(actual.DaysMin, actual.DaysMax)}, minutes {Markdown.Range(actual.MinutesMin, actual.MinutesMax)}, {actual.Result}"));
        }

        var sessions = calculations.SessionCounts(reference.PatientKey, reference.From, reference.To);
        foreach (var (category, count) in reference.Sessions)
            checks.Add(new Check("Sessions", category, count.ToString(), sessions.SessionsByCategory.GetValueOrDefault(category).ToString()));
        checks.Add(new Check("Sessions", "Total", reference.TotalSessions, Markdown.Range(sessions.TotalSessions, sessions.TotalSessionsMax)));
        checks.Add(new Check("Sessions", "Distinct days", reference.DistinctDays, Markdown.Range(sessions.DistinctDays, sessions.DistinctDaysMax)));

        checks.Add(new Check("Measures", "Distinct assessments", reference.Measures.Count.ToString(), patient.Measurements.Count.ToString()));
        foreach (var expected in reference.Measures)
        {
            var actual = patient.Measurements.FirstOrDefault(m =>
                m.Instrument.Equals(expected.Instrument, StringComparison.OrdinalIgnoreCase) && m.CompletedOn?.ToString("yyyy-MM-dd") == expected.Date);
            checks.Add(new Check("Measures", $"{expected.Instrument} {expected.Date}", expected.Total.ToString(), actual?.TotalScore?.ToString() ?? "missing"));
        }

        if (reference.Plan is { } plan)
        {
            var actual = patient.Plans.FirstOrDefault();
            checks.Add(new Check("Plan", "Goal", $"{plan.MinDays} days, {plan.MinMinutes} minutes",
                actual is null ? "missing" : $"{actual.MinTherapyDaysPerWeek} days, {actual.MinMinutesPerWeek} minutes"));
            checks.Add(new Check("Plan", "Versions", plan.Versions.ToString(), patient.Plans.Count.ToString()));
        }

        checks.Add(new Check("Linking", "Assertions left unlinked", "0", patient.Unlinked.Count.ToString()));
        return checks;
    }

    /// <summary>
    /// The events the abstraction holds for the patient that the reference does not list and that
    /// cannot change a count, each named with its date and why. An unlisted event that could change
    /// a count already fails a check in <see cref="Score"/>. These are reported beside the score
    /// and do not change it.
    /// </summary>
    public static List<string> NotListed(Database database, Reference reference)
    {
        PatientAbstraction patient;
        try
        {
            patient = new Calculations(database).Load(reference.PatientKey);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var (from, to) = (DateOnly.Parse(reference.From), DateOnly.Parse(reference.To));
        bool Listed(EncounterEvent e) => reference.Encounters.Any(x => string.Equals(x.Id, e.EncounterId, StringComparison.OrdinalIgnoreCase) || x.Id == e.EventKey);
        var named = new List<string>();
        foreach (var e in patient.Encounters.Where(e => !Listed(e)))
        {
            var line = WeeklyCalculator.Judge(patient, e);
            var inPeriod = e.ServiceDate is { } d && d >= from && d <= to;
            if (inPeriod && (line.Counted || line.MayCount)) continue;
            var name = e.EncounterId ?? $"the {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()} contact with no identifier";
            var why = e.ServiceDate is null ? "it has no date"
                : !inPeriod ? "it falls outside the period scored"
                : line.Status == "excluded_by_plan" ? "the treatment plan excludes this service"
                : "it is not a session";
            named.Add($"{name} on {e.ServiceDate?.ToString("yyyy-MM-dd") ?? "no date"}, because {why}");
        }
        return named;
    }

    public static string Render(List<Check> checks, string title, List<string>? notListed = null)
    {
        var passed = checks.Count(c => c.Passed);
        var text = $"## {title}\n\n{passed} of {checks.Count} checks passed.\n\n";
        if (notListed is not null)
            text += notListed.Count == 0
                ? "Events the reference does not list: none.\n\n"
                : $"Events the reference does not list, none of which can change a count, so they are not scored: {notListed.Count} ({string.Join("; ", notListed)}).\n\n";
        text += Markdown.Table(["Area", "Checks", "Passed"],
            checks.GroupBy(c => c.Area).Select(g => new[] { g.Key, g.Count().ToString(), g.Count(c => c.Passed).ToString() }));
        var failed = checks.Where(c => !c.Passed).ToList();
        if (failed.Count > 0)
            text += "Checks that failed:\n\n" + Markdown.Table(["Area", "Item", "Expected", "Actual"], failed.Select(c => new[] { c.Area, c.Item, c.Expected, c.Actual }));
        return text;
    }
}

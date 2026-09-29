using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Calculation;

/// <summary>
/// Judges contacts against the treatment plan and totals them by week. Everything here is plain
/// arithmetic over saved events. No model is involved and no document is read.
/// </summary>
public static class WeeklyCalculator
{
    public const string Met = "met";
    public const string NotMet = "not_met";
    public const string Undetermined = "cannot_be_determined";
    public const string NoGoal = "no_goal";

    /// <summary>The plan version in effect on a date, or null if none was.</summary>
    public static PlanVersion? PlanOn(PatientAbstraction patient, DateOnly date) =>
        patient.Plans.LastOrDefault(p => (p.EffectiveFrom ?? DateOnly.MinValue) <= date && (p.EffectiveTo is null || date <= p.EffectiveTo));

    public static DateOnly WeekStart(DateOnly date, string weekDefinition) =>
        weekDefinition == "sunday_saturday"
            ? date.AddDays(-(int)date.DayOfWeek)
            : date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>Decides whether one contact counts, using the plan in effect on the day it happened.</summary>
    public static ContactLine Judge(PatientAbstraction patient, EncounterEvent e)
    {
        var line = new ContactLine
        {
            EventKey = e.EventKey,
            EncounterId = e.EncounterId,
            Date = e.ServiceDate?.ToString("yyyy-MM-dd") ?? "",
            Weekday = e.ServiceDate?.DayOfWeek.ToString() ?? "",
            Category = e.Category.Value ?? "unresolved",
            CategoryName = Vocabulary.DescribeCategory(e.Category.Value),
            MinutesMin = e.MinutesMin,
            MinutesMax = e.MinutesMax,
            Arithmetic = e.Arithmetic,
            SourceDocuments = e.SourceDocuments,
            Citations = CitationsOf(e),
            Flags = e.Flags,
            Discrepancies = e.Discrepancies.Select(d => $"{d.Rule}: {d.Description} ({string.Join(", ", d.Citations)})").ToList(),
            Needs = e.Needs,
        };

        if (e.Occurrence == "not_session")
        {
            line.Status = "not_a_session";
            line.Reason = e.NotSessionReason ?? "The appointment did not take place.";
            line.MinutesMin = line.MinutesMax = 0;
            return line;
        }

        // Scheduling calls, outreach and notices are never a clinical service, whatever a plan says.
        if (e.Category.Value == "administrative")
        {
            line.Status = "not_a_session";
            line.Reason = "An administrative contact such as a scheduling or outreach call. It is not a clinical service.";
            line.MinutesMin = line.MinutesMax = 0;
            return line;
        }

        if (!e.Category.IsSettled)
        {
            line.Status = "uncertain";
            line.Reason = "The records disagree on which service this was, so it cannot be judged against the plan.";
            line.MinutesMin = 0;
            return line;
        }

        var plan = e.ServiceDate is { } date ? PlanOn(patient, date) : null;
        if (plan is null)
        {
            line.Status = "not_addressed_by_plan";
            line.Reason = "No treatment plan was in effect on this date.";
            line.MinutesMin = 0;
            return line;
        }

        if (plan.ExcludedCategories.Contains(e.Category.Value!))
        {
            line.Status = "excluded_by_plan";
            line.Reason = $"The treatment plan says {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()} does not count toward the goal ({string.Join(", ", plan.Citations)}).";
            line.MinutesMin = line.MinutesMax = 0;
            return line;
        }

        if (!plan.CountedCategories.Contains(e.Category.Value!))
        {
            line.Status = "not_addressed_by_plan";
            line.Reason = $"The treatment plan neither counts nor excludes {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()}. Totals are shown without it and with it.";
            line.MinutesMin = 0;
            line.Needs = [.. line.Needs, $"A treatment plan statement on whether {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()} counts toward the goal."];
            return line;
        }

        if (e.Occurrence == "uncertain")
        {
            line.Status = "uncertain";
            line.Reason = "The record does not establish that the patient attended. " + e.Arithmetic;
            line.MinutesMin = 0;
            return line;
        }

        line.Status = "counted";
        line.Reason = $"{line.CategoryName} with the patient present, counted by the treatment plan ({string.Join(", ", plan.Citations)}).";
        return line;
    }

    private static List<string> CitationsOf(EncounterEvent e) =>
        new[] { e.Category, e.Disposition, e.PatientPresent, e.Start, e.End }
            .SelectMany(d => d.Candidates.SelectMany(c => c.Citations))
            .Concat(e.Excluded.SelectMany(x => x.Citations))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    public static List<ContactLine> ContactsInRange(PatientAbstraction patient, DateOnly from, DateOnly to) =>
        patient.Encounters
            .Where(e => e.ServiceDate is { } d && d >= from && d <= to)
            .Select(e => Judge(patient, e))
            .ToList();

    public static List<WeekResult> Weeks(PatientAbstraction patient, DateOnly from, DateOnly to)
    {
        var definition = patient.Plans.FirstOrDefault()?.WeekDefinition ?? "monday_sunday";
        var contacts = ContactsInRange(patient, from, to);
        var weeks = new List<WeekResult>();

        for (var start = WeekStart(from, definition); start <= to; start = start.AddDays(7))
        {
            var end = start.AddDays(6);
            var firstDay = start < from ? from : start;
            var lastDay = end > to ? to : end;
            var week = new WeekResult
            {
                WeekStart = start.ToString("yyyy-MM-dd"),
                WeekEnd = end.ToString("yyyy-MM-dd"),
                DaysInRange = lastDay.DayNumber - firstDay.DayNumber + 1,
            };
            week.PartialWeek = week.DaysInRange < 7;
            if (week.PartialWeek)
                week.PartialWeekNote = $"Only {week.DaysInRange} of 7 days ({firstDay:yyyy-MM-dd} to {lastDay:yyyy-MM-dd}) fall inside the period examined. The goal is applied in full and is not prorated.";

            ChoosePlan(patient, start, firstDay, end, week);

            week.Contacts = contacts.Where(c => DateOnly.Parse(c.Date) >= start && DateOnly.Parse(c.Date) <= end).ToList();
            var counted = week.Contacts.Where(c => c.Counted).ToList();
            var possible = week.Contacts.Where(c => c.MayCount && c.MinutesMax > 0).ToList();

            week.MinutesMin = counted.Sum(c => c.MinutesMin);
            week.MinutesMax = counted.Sum(c => c.MinutesMax) + possible.Sum(c => c.MinutesMax);
            week.HoursMin = Hours(week.MinutesMin);
            week.HoursMax = Hours(week.MinutesMax);
            week.MinutesArithmetic = Arithmetic(counted.Concat(possible).ToList(), week.MinutesMin, week.MinutesMax);

            var definiteDays = counted.Where(c => c.MinutesMin > 0).Select(c => c.Date).Distinct().Order().ToList();
            var possibleDays = counted.Concat(possible).Where(c => c.MinutesMax > 0).Select(c => c.Date).Distinct().Order().ToList();
            week.TherapyDates = definiteDays;
            week.DaysMin = definiteDays.Count;
            week.DaysMax = possibleDays.Count;

            week.DaysResult = Compare(week.DaysMin, week.DaysMax, week.RequiredDays);
            week.MinutesResult = Compare(week.MinutesMin, week.MinutesMax, week.RequiredMinutes);
            week.Result = Combine(week.DaysResult, week.MinutesResult);
            week.Reason = Explain(week);

            if (week.Result == Undetermined)
                week.Needs = week.Contacts
                    .Where(c => (c.Counted && c.MinutesMin != c.MinutesMax) || c.MayCount)
                    .SelectMany(c => c.Needs)
                    .Distinct()
                    .ToList();

            weeks.Add(week);
        }

        return weeks;
    }

    /// <summary>
    /// Thresholds follow the plan in effect on the first day of the week. When that day is before
    /// any plan, the plan in effect on the first day examined is used.
    /// </summary>
    private static void ChoosePlan(PatientAbstraction patient, DateOnly weekStart, DateOnly firstDay, DateOnly weekEnd, WeekResult week)
    {
        var plan = PlanOn(patient, weekStart) ?? PlanOn(patient, firstDay)
            ?? patient.Plans.FirstOrDefault(p => p.EffectiveFrom is { } f && f >= weekStart && f <= weekEnd);
        if (plan is null)
        {
            week.PlanNote = "No treatment plan was in effect during this week.";
            return;
        }

        week.RequiredDays = plan.MinTherapyDaysPerWeek;
        week.RequiredMinutes = plan.MinMinutesPerWeek;
        week.PlanCitations = plan.Citations;

        var changes = patient.Plans.Where(p => p != plan && p.EffectiveFrom is { } f && f > weekStart && f <= weekEnd).ToList();
        if (changes.Count > 0)
        {
            var other = changes[0];
            week.PlanNote =
                $"A different plan takes effect on {other.EffectiveFrom:yyyy-MM-dd}, inside this week. The goal applied is the one in effect on {weekStart:yyyy-MM-dd} " +
                $"({Goal(plan)}). The later plan requires {Goal(other)} ({string.Join(", ", other.Citations)}). Each contact is judged by the plan in effect on its own date.";
        }
    }

    private static string Goal(PlanVersion plan) =>
        $"{plan.MinTherapyDaysPerWeek?.ToString() ?? "no stated number of"} therapy days and {plan.MinMinutesPerWeek?.ToString() ?? "no stated number of"} minutes";

    public static double Hours(int minutes) => Math.Round(minutes / 60.0, 2);

    private static string Arithmetic(List<ContactLine> lines, int min, int max)
    {
        if (lines.Count == 0) return "No counted contacts = 0";
        var parts = lines.Select(c =>
        {
            var low = c.Counted ? c.MinutesMin : 0;
            return low == c.MinutesMax ? $"{c.MinutesMax}" : $"({low} to {c.MinutesMax})";
        });
        var total = min == max ? $"{min}" : $"{min} to {max}";
        return $"{string.Join(" + ", parts)} = {total}";
    }

    /// <summary>
    /// Met when even the lowest possible value reaches the goal. Not met when even the highest
    /// possible value falls short. Otherwise the record does not settle it.
    /// </summary>
    public static string Compare(int min, int max, int? required)
    {
        if (required is not { } goal) return NoGoal;
        if (min >= goal) return Met;
        if (max < goal) return NotMet;
        return Undetermined;
    }

    public static string Combine(string days, string minutes)
    {
        if (days == NoGoal && minutes == NoGoal) return NoGoal;
        if (days == NotMet || minutes == NotMet) return NotMet;
        if (days == Undetermined || minutes == Undetermined) return Undetermined;
        return Met;
    }

    private static string Explain(WeekResult w)
    {
        if (w.Result == NoGoal) return w.PlanNote ?? "No goal applies to this week.";
        var parts = new List<string>();
        if (w.RequiredDays is { } days)
            parts.Add($"{Span(w.DaysMin, w.DaysMax)} therapy day(s) against a goal of {days}: {Words(w.DaysResult)}.");
        if (w.RequiredMinutes is { } minutes)
            parts.Add($"{Span(w.MinutesMin, w.MinutesMax)} minutes against a goal of {minutes}: {Words(w.MinutesResult)}.");
        return string.Join(" ", parts);
    }

    public static string Span(int min, int max) => min == max ? $"{min}" : $"{min} to {max}";

    public static string Words(string result) => result switch
    {
        Met => "met",
        NotMet => "not met",
        Undetermined => "cannot be determined",
        _ => "no goal",
    };
}

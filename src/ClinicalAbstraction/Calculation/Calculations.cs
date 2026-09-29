using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Calculation;

/// <summary>
/// Every calculation the system can run. Each reads the saved abstraction, runs without a model,
/// and returns its result together with the parameters it used and the sources behind it.
/// </summary>
public sealed class Calculations(Database database)
{
    public const string PartialWeekAssumption =
        "A week that is only partly inside the period examined is judged against the full goal. The goal is not prorated.";

    public const string RosterAssumption =
        "Arrival and departure times from attendance and desk records are taken as the patient's time in the session. They record check-in and check-out, so time in the room may be slightly shorter.";

    /// <summary>
    /// The most patients a question that names none is answered for in full detail. Above this,
    /// every patient who fits still has a row in a summary table, no patient is shown in detail,
    /// and the result says how to narrow the question. The limit keeps an answer readable.
    /// </summary>
    public const int FullDetailLimit = 10;

    // ---------- patients ----------

    public PatientSearchResult FindPatient(string query)
    {
        var wanted = query.Trim();
        var all = database.LoadAllAbstractions();
        if (wanted.Length == 0)
        {
            return new PatientSearchResult
            {
                Query = query,
                Matches = all.Select(Describe).ToList(),
                Note = all.Count == 1
                    ? "The collection holds exactly one patient."
                    : $"The collection holds {all.Count} patients. A question that names no patient applies to every one of them who fits what the question specifies. Do not choose one of them.",
            };
        }
        var matches = all.Where(p =>
                string.Equals(p.Mrn, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.PatientKey, wanted, StringComparison.OrdinalIgnoreCase)
                || (p.Name is not null && p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                || (p.Name is not null && wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(part => p.Name.Contains(part, StringComparison.OrdinalIgnoreCase))))
            .Select(Describe)
            .ToList();

        return new PatientSearchResult
        {
            Query = query,
            Matches = matches,
            Note = matches.Count switch
            {
                0 => $"No patient matches \"{query}\". The collection holds {all.Count} patient(s). Do not guess; say that the patient was not found.",
                1 => "One patient matches.",
                _ => $"{matches.Count} patients match \"{query}\". Do not pick one; report the ambiguity or use the MRN.",
            },
        };
    }

    public List<PatientMatch> ListPatients() => database.LoadAllAbstractions().Select(Describe).ToList();

    /// <summary>
    /// The key of the only patient in the collection, or null when it holds none or several.
    /// It reads the list of patients and loads no abstraction.
    /// </summary>
    public string? OnlyPatient() => database.ListPatients() is [var only] ? only.PatientKey : null;

    private static PatientMatch Describe(PatientAbstraction p) => new()
    {
        PatientKey = p.PatientKey, Mrn = p.Mrn, Name = p.Name, DateOfBirth = p.DateOfBirth,
        EpisodeStart = p.Episode.Start?.ToString("yyyy-MM-dd"), EpisodeEnd = p.Episode.End?.ToString("yyyy-MM-dd"),
        PlanVersions = p.Plans.Count, Encounters = p.Encounters.Count, AbstractionVersion = p.Version,
    };

    /// <summary>A patient by key: exactly as given first, since a key can hold any character, then trimmed and in capitals.</summary>
    public PatientAbstraction Load(string patientKey) =>
        database.LoadAbstraction(patientKey)
        ?? database.LoadAbstraction(patientKey.Trim().ToUpperInvariant())
        ?? throw new InvalidOperationException($"No saved abstraction for patient '{patientKey}'. Run find_patient to see who is in the collection.");

    /// <summary>
    /// Settles the date range. Dates given by the caller are used as given, with the end date
    /// included. Missing dates default to the episode.
    /// </summary>
    private static (DateOnly From, DateOnly To, ResolvedParameters Parameters) Resolve(PatientAbstraction patient, string? from, string? to)
    {
        var givenFrom = Parse.Date(from);
        var givenTo = Parse.Date(to);
        var start = givenFrom ?? patient.Episode.Start ?? patient.Encounters.Where(e => e.ServiceDate is not null).Select(e => e.ServiceDate!.Value).DefaultIfEmpty(DateOnly.MinValue).Min();
        var end = givenTo ?? patient.Episode.End ?? patient.Encounters.Where(e => e.ServiceDate is not null).Select(e => e.ServiceDate!.Value).DefaultIfEmpty(DateOnly.MaxValue).Max();

        var episode = patient.Episode.Citations.Count > 0
            ? $"the episode dates stated in the record ({string.Join(", ", patient.Episode.Citations)})"
            : "the range of recorded encounters, because no record states the episode dates";
        var source = (givenFrom, givenTo) switch
        {
            (not null, not null) => "given in the request; both dates are included",
            (null, null) => $"not given, so {episode} are used; both dates are included",
            _ => $"partly given; the missing date comes from {episode}; both dates are included",
        };

        return (start, end, new ResolvedParameters
        {
            PatientKey = patient.PatientKey,
            PatientName = patient.Name,
            From = start.ToString("yyyy-MM-dd"),
            To = end.ToString("yyyy-MM-dd"),
            DateRangeSource = source,
            WeekDefinition = (patient.Plans.FirstOrDefault()?.WeekDefinition ?? "monday_sunday") == "sunday_saturday"
                ? "Sunday to Saturday, from the treatment plan"
                : patient.Plans.Count > 0 ? "Monday to Sunday, from the treatment plan" : "Monday to Sunday, by default because no plan defines a week",
            AbstractionVersion = patient.Version,
        });
    }

    // ---------- sessions ----------

    public SessionCountResult SessionCounts(string patientKey, string? from, string? to)
    {
        var patient = Load(patientKey);
        var (start, end, parameters) = Resolve(patient, from, to);
        var contacts = WeeklyCalculator.ContactsInRange(patient, start, end);

        var counted = contacts.Where(c => c.Counted).ToList();
        var uncertain = contacts.Where(c => c.MayCount).ToList();
        var result = new SessionCountResult
        {
            Parameters = parameters,
            Counted = counted,
            Uncertain = uncertain,
            NotCounted = contacts.Where(c => !c.Counted && !c.MayCount).ToList(),
            SessionsByCategory = counted.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            TotalSessions = counted.Count,
            TotalSessionsMax = counted.Count + uncertain.Count,
            TherapyDates = counted.Select(c => c.Date).Distinct().Order().ToList(),
            Warnings = patient.Warnings,
        };
        result.DistinctDays = result.TherapyDates.Count;
        result.DistinctDaysMax = counted.Concat(uncertain).Select(c => c.Date).Distinct().Count();

        result.DescribedMoreThanOnce = patient.Encounters
            .Where(e => e.ServiceDate >= start && e.ServiceDate <= end && e.SourceDocuments.Count > 1)
            .Select(e => $"{(e.EncounterId is { } id ? $"{id} on {e.ServiceDate:yyyy-MM-dd}" : $"The {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()} contact on {e.ServiceDate:yyyy-MM-dd}, which has no identifier,")} is described by {e.SourceDocuments.Count} documents ({string.Join(", ", e.SourceDocuments)}) and is one contact.")
            .ToList();

        result.RecordsThatAreNotAppointments = patient.NonEncounterRecords
            .Select(r => $"{r.Citation}: {r.Description}")
            .Concat(patient.Measurements.SelectMany(m => m.RepeatCitations.Select(c => $"{c}: a copy or later mention of the {m.Instrument} result completed on {m.CompletedOn:yyyy-MM-dd}, not a new assessment or visit.")))
            .Distinct()
            .ToList();

        result.UnlinkedAssertions = patient.Unlinked.Select(u => $"{u.Citation} ({Vocabulary.DescribeKind(u.Kind)}): {u.Reason}").ToList();
        return result;
    }

    // ---------- minutes and goals by week ----------

    public WeeklyResult Weekly(string patientKey, string? from, string? to)
    {
        var patient = Load(patientKey);
        var (start, end, parameters) = Resolve(patient, from, to);
        return Weekly(patient, start, end, parameters);
    }

    public static WeeklyResult Weekly(PatientAbstraction patient, DateOnly start, DateOnly end, ResolvedParameters parameters)
    {
        var weeks = WeeklyCalculator.Weeks(patient, start, end);
        var result = new WeeklyResult
        {
            Parameters = parameters,
            Weeks = weeks,
            TotalMinutesMin = weeks.Sum(w => w.MinutesMin),
            TotalMinutesMax = weeks.Sum(w => w.MinutesMax),
            Warnings = patient.Warnings,
        };
        result.TotalHoursMin = WeeklyCalculator.Hours(result.TotalMinutesMin);
        result.TotalHoursMax = WeeklyCalculator.Hours(result.TotalMinutesMax);
        result.TotalArithmetic =
            $"{string.Join(" + ", weeks.Select(w => w.MinutesMin == w.MinutesMax ? $"{w.MinutesMin}" : $"({w.MinutesMin} to {w.MinutesMax})"))} = " +
            $"{WeeklyCalculator.Span(result.TotalMinutesMin, result.TotalMinutesMax)} minutes; divided by 60 = {Hours(result.TotalHoursMin, result.TotalHoursMax)} hours";

        var contacts = weeks.SelectMany(w => w.Contacts).ToList();
        result.Unsettled = contacts
            .Where(c => (c.Counted && c.MinutesMin != c.MinutesMax) || c.MayCount)
            .Select(c => $"{c.Date} {c.CategoryName} ({c.EncounterId ?? "no identifier"}): {(c.Counted ? c.MinutesMin : 0)} to {c.MinutesMax} minutes. {Stop(Capital(c.Counted ? c.Arithmetic : c.Reason))} Would be settled by: {string.Join(" ", c.Needs)}")
            .ToList();

        if (weeks.Any(w => w.PartialWeek)) result.Assumptions.Add(PartialWeekAssumption);
        if (patient.Encounters.Any(e => e.PresenceFromRoster && e.ServiceDate >= start && e.ServiceDate <= end)) result.Assumptions.Add(RosterAssumption);
        foreach (var flag in contacts.Where(c => c.Counted).SelectMany(c => c.Flags.Select(f => $"{c.Date} {c.CategoryName}: {f}")).Distinct())
            result.Assumptions.Add(flag);
        return result;
    }

    private static string Hours(double min, double max) => min == max ? $"{min:0.##}" : $"{min:0.##} to {max:0.##}";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Stop(string text) => text.Length == 0 || text[^1] is '.' or '?' ? text : text + ".";

    // ---------- consecutive weeks below goal ----------

    public ConsecutiveWeeksResult ConsecutiveWeeks(string? patientKey, string? from, string? to)
    {
        // For every patient over their whole episode, the weekly results saved at reconciliation are
        // read as they are. Nothing is recomputed and no patient's events are loaded.
        if (patientKey is null && from is null && to is null) return ConsecutiveWeeksFromStoredResults();

        var patients = patientKey is null ? database.LoadAllAbstractions() : [Load(patientKey)];
        var result = new ConsecutiveWeeksResult { PatientsExamined = patients.Count, From = from ?? "each patient's episode start", To = to ?? "each patient's episode end" };

        foreach (var patient in patients)
        {
            var (start, end, _) = Resolve(patient, from, to);
            var summary = Consecutive(patient, WeeklyCalculator.Weeks(patient, start, end));
            switch (summary.Status)
            {
                case "included": result.Included.Add(summary); break;
                case "depends_on_unresolved_documentation": result.DependsOnUnresolvedDocumentation.Add(summary); break;
                case "only_with_a_partial_week": result.OnlyWithPartialWeek.Add(summary); break;
                default: result.NotIncluded.Add($"{patient.Name ?? patient.PatientKey} ({patient.PatientKey})"); break;
            }
        }

        result.Assumptions.Add("Two weeks are consecutive when one begins seven days after the other.");
        result.Assumptions.Add("A pair that includes a week only partly inside the period examined is reported separately, because the goal is not prorated.");
        return result;
    }

    private ConsecutiveWeeksResult ConsecutiveWeeksFromStoredResults()
    {
        var names = database.ListPatients().ToDictionary(p => p.PatientKey, p => p.Name);
        var stored = database.LoadWeeklyResults().GroupBy(r => r.PatientKey).ToDictionary(g => g.Key, g => g.Select(r => Json.Read<WeekResult>(r.Json)).ToList());
        var result = new ConsecutiveWeeksResult { PatientsExamined = names.Count, From = "each patient's episode start", To = "each patient's episode end" };

        foreach (var (key, name) in names.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            var summary = Consecutive(new PatientAbstraction { PatientKey = key, Name = name }, stored.GetValueOrDefault(key, []));
            switch (summary.Status)
            {
                case "included": result.Included.Add(summary); break;
                case "depends_on_unresolved_documentation": result.DependsOnUnresolvedDocumentation.Add(summary); break;
                case "only_with_a_partial_week": result.OnlyWithPartialWeek.Add(summary); break;
                default: result.NotIncluded.Add($"{name ?? key} ({key})"); break;
            }
        }

        result.Assumptions.Add("Two weeks are consecutive when one begins seven days after the other.");
        result.Assumptions.Add("A pair that includes a week only partly inside the period examined is reported separately, because the goal is not prorated.");
        return result;
    }

    /// <summary>
    /// A pair of adjacent weeks is definitely below when both are not met. It is possibly below
    /// when both are either not met or undetermined and at least one is undetermined.
    /// </summary>
    public static PatientConsecutive Consecutive(PatientAbstraction patient, List<WeekResult> weeks)
    {
        var summary = new PatientConsecutive { PatientKey = patient.PatientKey, PatientName = patient.Name };

        for (var i = 0; i + 1 < weeks.Count; i++)
        {
            var (a, b) = (weeks[i], weeks[i + 1]);
            bool Below(WeekResult w) => w.Result is WeeklyCalculator.NotMet or WeeklyCalculator.Undetermined;
            if (!Below(a) || !Below(b)) continue;

            var pair = new WeekPair
            {
                FirstWeek = a.WeekStart, SecondWeek = b.WeekStart,
                FirstResult = WeeklyCalculator.Words(a.Result), SecondResult = WeeklyCalculator.Words(b.Result),
                FirstDetail = a.Reason, SecondDetail = b.Reason,
                IncludesPartialWeek = a.PartialWeek || b.PartialWeek,
                WouldBeSettledBy = a.Needs.Concat(b.Needs).Distinct().ToList(),
                // The stored weekly results already hold the plan's sources and each contact's, so
                // both ways of working this out carry the same sources.
                FirstGoalSources = a.PlanCitations, SecondGoalSources = b.PlanCitations,
                FirstContactSources = ContactSources(a), SecondContactSources = ContactSources(b),
            };

            var definite = a.Result == WeeklyCalculator.NotMet && b.Result == WeeklyCalculator.NotMet;
            if (pair.IncludesPartialWeek) summary.PairsWithPartialWeek.Add(pair);
            else if (definite) summary.DefinitePairs.Add(pair);
            else summary.PossiblePairs.Add(pair);
        }

        summary.Status =
            summary.DefinitePairs.Count > 0 ? "included"
            : summary.PossiblePairs.Count > 0 ? "depends_on_unresolved_documentation"
            : summary.PairsWithPartialWeek.Count > 0 ? "only_with_a_partial_week"
            : "not_included";
        return summary;
    }

    /// <summary>Every source behind the contacts of a week. A contact's sources include each candidate value of a field left unresolved.</summary>
    private static List<string> ContactSources(WeekResult week) =>
        week.Contacts.SelectMany(c => c.Citations).Distinct().Order(StringComparer.Ordinal).ToList();

    // ---------- before and after a plan change ----------

    public PlanChangeResult PlanChange(string patientKey)
    {
        var patient = Load(patientKey);
        var (start, end, parameters) = Resolve(patient, null, null);
        var result = new PlanChangeResult { Parameters = parameters };

        if (patient.Plans.Count < 2)
        {
            result.PlanChangeFound = false;
            result.Summary = patient.Plans.Count == 0
                ? "The record contains no treatment plan with a participation goal, so there is no plan change to compare."
                : $"The record contains one treatment plan ({string.Join(", ", patient.Plans[0].Citations)}) and no later plan or revision. There is no plan change to compare.";
        }
        else
        {
            result.PlanChangeFound = true;
            result.Summary = $"The record contains {patient.Plans.Count} plan versions. Each period below runs from the day a version takes effect to the day before the next one.";
        }

        foreach (var plan in patient.Plans)
        {
            var from = plan.EffectiveFrom is { } f && f > start ? f : start;
            var to = plan.EffectiveTo is { } t && t < end ? t : end;
            if (to < from) continue;

            var counted = WeeklyCalculator.ContactsInRange(patient, from, to).Where(c => c.Counted).ToList();
            var days = to.DayNumber - from.DayNumber + 1;
            var weeksInPeriod = days / 7.0;
            result.Periods.Add(new PlanPeriod
            {
                From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd"), Days = days,
                RequiredDays = plan.MinTherapyDaysPerWeek, RequiredMinutes = plan.MinMinutesPerWeek,
                CountedCategories = plan.CountedCategories.Select(c => Vocabulary.DescribeCategory(c)).ToList(),
                PlanCitations = plan.Citations,
                SessionsByCategory = counted.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                MinutesByCategory = counted.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => WeeklyCalculator.Span(g.Sum(c => c.MinutesMin), g.Sum(c => c.MinutesMax))),
                TotalSessions = counted.Count,
                MinutesMin = counted.Sum(c => c.MinutesMin),
                MinutesMax = counted.Sum(c => c.MinutesMax),
                SessionsPerWeek = Math.Round(counted.Count / weeksInPeriod, 2),
                MinutesPerWeekMin = Math.Round(counted.Sum(c => c.MinutesMin) / weeksInPeriod, 1),
                MinutesPerWeekMax = Math.Round(counted.Sum(c => c.MinutesMax) / weeksInPeriod, 1),
            });
        }

        result.Assumptions.Add("Rates per week divide each period's totals by its length in days over seven, so periods of different lengths can be compared.");
        result.Assumptions.Add("Each contact is judged by the plan in effect on the day it happened.");
        return result;
    }

    // ---------- one day ----------

    public DayResult Day(string patientKey, string date)
    {
        var patient = Load(patientKey);
        var day = Parse.Date(date) ?? throw new InvalidOperationException($"'{date}' is not a date. Use yyyy-MM-dd.");
        return Day(patient, day, withObservations: true);
    }

    /// <summary>
    /// One patient's day. The day across patients calls this once for each patient who fits, so a
    /// patient's contacts and minutes are the same whether or not the question named the patient.
    /// </summary>
    private DayResult Day(PatientAbstraction patient, DateOnly day, bool withObservations)
    {
        var date = day.ToString("yyyy-MM-dd");
        var (_, _, parameters) = Resolve(patient, date, date);

        var events = patient.Encounters.Where(e => e.ServiceDate == day).ToList();
        var contacts = events.Select(e => WeeklyCalculator.Judge(patient, e)).ToList();
        var counted = contacts.Where(c => c.Counted).ToList();
        var possible = contacts.Where(c => c.MayCount && c.MinutesMax > 0).ToList();

        return new DayResult
        {
            Parameters = parameters,
            Date = day.ToString("yyyy-MM-dd"),
            Events = events,
            Contacts = contacts,
            TherapyContacts = counted.Count,
            TherapyContactsMax = counted.Count + possible.Count,
            MinutesMin = counted.Sum(c => c.MinutesMin),
            MinutesMax = counted.Sum(c => c.MinutesMax) + possible.Sum(c => c.MinutesMax),
            MinutesArithmetic = counted.Count + possible.Count == 0
                ? "No counted contacts = 0 minutes"
                : $"{string.Join(" + ", counted.Concat(possible).Select(c => WeeklyCalculator.Span(c.Counted ? c.MinutesMin : 0, c.MinutesMax)))} = " +
                  $"{WeeklyCalculator.Span(counted.Sum(c => c.MinutesMin), counted.Sum(c => c.MinutesMax) + possible.Sum(c => c.MinutesMax))} minutes",
            Observations = withObservations ? ObservationLines(patient.PatientKey, day, day, null) : [],
            OtherRecords = patient.NonEncounterRecords.Where(r => r.Date == day).Select(r => $"{r.Citation}: {r.Description}").ToList(),
        };
    }

    // ---------- questions that name no patient ----------

    /// <summary>
    /// Who has an appointment or contact recorded in a period. This is how a question that names
    /// no patient finds the patients it applies to. It reads the saved events, not the model's
    /// judgement, so the same question always covers the same patients.
    /// </summary>
    public PatientsInPeriodResult PatientsInPeriod(string? from, string? to)
    {
        var (start, end) = Period(from, to);
        var fitting = database.ListPatientsWithEncounters(start?.ToString("yyyy-MM-dd"), end?.ToString("yyyy-MM-dd"));
        var inCollection = database.ListPatients().Count;

        var result = new PatientsInPeriodResult
        {
            From = start?.ToString("yyyy-MM-dd") ?? "the earliest recorded contact",
            To = end?.ToString("yyyy-MM-dd") ?? "the latest recorded contact",
            PatientsInCollection = inCollection,
            PatientsWhoFit = fitting.Count,
            FullDetailLimit = FullDetailLimit,
            FullDetailAllowed = fitting.Count <= FullDetailLimit,
            Patients = fitting.Select(p => new PatientInPeriod
            {
                PatientKey = p.PatientKey, PatientName = p.Name, ContactsRecorded = p.Encounters,
                DaysWithContacts = p.Days, FirstContact = p.FirstDate, LastContact = p.LastDate,
            }).ToList(),
        };

        result.Notes.Add(Coverage(inCollection, fitting.Select(p => Label(p.Name, p.PatientKey)).ToList(), PeriodInWords(start, end)));
        result.Notes.Add("The count of appointments and contacts includes ones that did not take place and ones that do not count toward a goal. It shows who the question applies to. It is not a count of therapy sessions.");
        if (!result.FullDetailAllowed) result.Notes.Add(OverTheLimit(fitting.Count, "name one patient, or give a shorter period"));
        return result;
    }

    /// <summary>
    /// One date for every patient who has a contact on it, grouped by patient. Only the patients
    /// who fit are loaded, one at a time. Above the limit each of them has a summary row and none
    /// is shown in detail.
    /// </summary>
    public CollectionDayResult DayAcrossPatients(string date)
    {
        var day = Parse.Date(date) ?? throw new InvalidOperationException($"'{date}' is not a date. Use yyyy-MM-dd.");
        var text = day.ToString("yyyy-MM-dd");
        var fitting = database.ListPatientsWithEncounters(text, text);
        var inCollection = database.ListPatients().Count;

        var result = new CollectionDayResult
        {
            Date = text,
            PatientsInCollection = inCollection,
            PatientsWhoFit = fitting.Count,
            FullDetailLimit = FullDetailLimit,
            FullDetailGiven = fitting.Count <= FullDetailLimit,
        };

        foreach (var row in fitting)
        {
            var one = Day(Load(row.PatientKey), day, withObservations: result.FullDetailGiven);
            result.Summary.Add(new PatientDayLine
            {
                PatientKey = row.PatientKey, PatientName = row.Name, ContactsRecorded = one.Contacts.Count,
                TherapyContacts = one.TherapyContacts, TherapyContactsMax = one.TherapyContactsMax,
                MinutesMin = one.MinutesMin, MinutesMax = one.MinutesMax,
            });
            if (result.FullDetailGiven) result.Patients.Add(one);
        }

        result.Notes.Add(Coverage(inCollection, fitting.Select(p => Label(p.Name, p.PatientKey)).ToList(), PeriodInWords(day, day)));
        if (!result.FullDetailGiven) result.Notes.Add(OverTheLimit(fitting.Count, "name one patient, or ask about a date on which fewer patients have a contact"));
        return result;
    }

    /// <summary>
    /// One row for each patient who fits: sessions by service type, therapy days, minutes, and how
    /// many weeks met the goal. With dates, a patient fits by having a contact in the period. With
    /// no dates every patient fits, and each is examined over their own episode.
    /// </summary>
    public CollectionSummaryResult CollectionSummary(string? from, string? to)
    {
        var (start, end) = Period(from, to);
        var everyone = database.ListPatients();
        var noDates = start is null && end is null;
        var fitting = noDates
            ? everyone.Select(p => (p.PatientKey, p.Name)).ToList()
            : database.ListPatientsWithEncounters(start?.ToString("yyyy-MM-dd"), end?.ToString("yyyy-MM-dd")).Select(p => (p.PatientKey, p.Name)).ToList();

        var result = new CollectionSummaryResult
        {
            From = start?.ToString("yyyy-MM-dd") ?? "each patient's episode start",
            To = end?.ToString("yyyy-MM-dd") ?? "each patient's episode end",
            DateRangeSource = (start, end) switch
            {
                (not null, not null) => "given in the request; both dates are included",
                (null, null) => "not given, so each patient's own episode is used, and the period can differ from one patient to the next",
                _ => "partly given; the missing date comes from each patient's own episode; both dates are included",
            },
            PatientsInCollection = everyone.Count,
            PatientsWhoFit = fitting.Count,
            FullDetailLimit = FullDetailLimit,
            FullDetailAllowed = fitting.Count <= FullDetailLimit,
        };

        var anyPartialWeek = false;
        var anyRoster = false;
        foreach (var (key, name) in fitting)
        {
            var line = new PatientSummaryLine { PatientKey = key, PatientName = name };
            result.Patients.Add(line);

            if (database.LoadAbstraction(key) is not { } patient)
            {
                line.Warnings.Add("No saved abstraction exists for this patient, so nothing could be counted.");
                continue;
            }
            line.Warnings = patient.Warnings;

            var (first, last, parameters) = Resolve(patient, from, to);
            if (first == DateOnly.MinValue || last == DateOnly.MaxValue)
            {
                line.From = line.To = "not recorded";
                line.Warnings = [.. line.Warnings, "The record states no episode dates and holds no dated contact, so there is no period to examine."];
                continue;
            }

            var contacts = WeeklyCalculator.ContactsInRange(patient, first, last);
            var counted = contacts.Where(c => c.Counted).ToList();
            var uncertain = contacts.Where(c => c.MayCount).ToList();
            var weeks = WeeklyCalculator.Weeks(patient, first, last);

            line.From = parameters.From;
            line.To = parameters.To;
            line.SessionsByCategory = counted.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            line.TotalSessions = counted.Count;
            line.TotalSessionsMax = counted.Count + uncertain.Count;
            line.DistinctDays = counted.Select(c => c.Date).Distinct().Count();
            line.DistinctDaysMax = counted.Concat(uncertain).Select(c => c.Date).Distinct().Count();
            line.MinutesMin = weeks.Sum(w => w.MinutesMin);
            line.MinutesMax = weeks.Sum(w => w.MinutesMax);
            line.WeeksExamined = weeks.Count;
            line.WeeksMet = weeks.Count(w => w.Result == WeeklyCalculator.Met);
            line.WeeksNotMet = weeks.Count(w => w.Result == WeeklyCalculator.NotMet);
            line.WeeksUndetermined = weeks.Count(w => w.Result == WeeklyCalculator.Undetermined);
            line.WeeksWithNoGoal = weeks.Count(w => w.Result == WeeklyCalculator.NoGoal);
            line.PartialWeeks = weeks.Count(w => w.PartialWeek);

            anyPartialWeek |= line.PartialWeeks > 0;
            anyRoster |= patient.Encounters.Any(e => e.PresenceFromRoster && e.ServiceDate >= first && e.ServiceDate <= last);
        }

        var labels = fitting.Select(p => Label(p.Name, p.PatientKey)).ToList();
        result.Notes.Add(noDates
            ? $"No dates were given, so every patient in the collection is covered, each over their own episode. The collection holds {Patients(everyone.Count)}."
            : Coverage(everyone.Count, labels, PeriodInWords(start, end)));
        if (!result.FullDetailAllowed)
            result.Notes.Add(OverTheLimit(fitting.Count, noDates ? "name one patient, or give dates in which fewer patients have a contact" : "name one patient, or give a shorter period"));

        result.Assumptions.Add("Each contact is judged by the treatment plan in effect for that patient on the day it happened.");
        result.Assumptions.Add("A session is counted when the patient was present and the treatment plan counts that service. Where the record leaves this unsettled, the lowest and highest possible values are both shown.");
        if (anyPartialWeek) result.Assumptions.Add(PartialWeekAssumption);
        if (anyRoster) result.Assumptions.Add(RosterAssumption);
        return result;
    }

    /// <summary>Reads the dates of a period. A date that cannot be read is an error, never a silent default.</summary>
    private static (DateOnly? From, DateOnly? To) Period(string? from, string? to)
    {
        static DateOnly? Read(string? text) =>
            text is null ? null : Parse.Date(text) ?? throw new InvalidOperationException($"'{text}' is not a date. Use yyyy-MM-dd.");

        var (start, end) = (Read(from), Read(to));
        if (start > end) throw new InvalidOperationException($"The period ends on {end:yyyy-MM-dd}, which is before it begins on {start:yyyy-MM-dd}.");
        return (start, end);
    }

    private static string PeriodInWords(DateOnly? from, DateOnly? to) => (from, to) switch
    {
        ({ } f, { } t) when f == t => $"on {f:yyyy-MM-dd}",
        ({ } f, { } t) => $"between {f:yyyy-MM-dd} and {t:yyyy-MM-dd}",
        ({ } f, null) => $"on or after {f:yyyy-MM-dd}",
        (null, { } t) => $"on or before {t:yyyy-MM-dd}",
        _ => "at any time",
    };

    private static string Label(string? name, string patientKey) => $"{name ?? "name not recorded"} ({patientKey})";

    private static string Patients(int count) => count == 1 ? "1 patient" : $"{count} patients";

    /// <summary>
    /// Says in a sentence who is covered and who is left out, so an answer can state that no other
    /// patient fits without the model working it out.
    /// </summary>
    private static string Coverage(int inCollection, List<string> fitting, string period)
    {
        var held = $"an appointment or contact recorded {period}";
        var others = inCollection - fitting.Count;
        var leftOut = others == 1
            ? "The other patient in the collection has none and is left out."
            : $"The other {others} patients in the collection have none and are left out.";

        if (fitting.Count == 0) return $"No patient in the collection has {held}. The collection holds {Patients(inCollection)}.";
        if (others <= 0)
            return fitting.Count == 1
                ? $"{fitting[0]} is the only patient in the collection and has {held}."
                : $"All {fitting.Count} patients in the collection have {held}.";
        if (fitting.Count == 1) return $"{fitting[0]} is the only patient who has {held}. {leftOut}";
        return fitting.Count <= FullDetailLimit
            ? $"{fitting.Count} of the {inCollection} patients in the collection have {held}: {string.Join(", ", fitting)}. {leftOut}"
            : $"{fitting.Count} of the {inCollection} patients in the collection have {held}. {leftOut}";
    }

    private static string OverTheLimit(int fitting, string howToNarrow) =>
        $"{fitting} patients fit, which is more than the limit of {FullDetailLimit} for full detail. Each of them has a row in the summary table, and none is shown in full detail. To see full detail, {howToNarrow}.";

    // ---------- measures and observations ----------

    public MeasureSeriesResult Measures(string patientKey, string? instrument)
    {
        var patient = Load(patientKey);
        var (_, _, parameters) = Resolve(patient, null, null);
        var result = new MeasureSeriesResult { Parameters = parameters };

        var chosen = patient.Measurements
            .Where(m => instrument is null || m.Instrument.Replace("-", "").Equals(instrument.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var series in chosen.GroupBy(m => m.Instrument.ToUpperInvariant()))
        {
            MeasurementEvent? first = null, previous = null;
            foreach (var m in series.OrderBy(m => m.CompletedOn ?? DateOnly.MaxValue))
            {
                result.Assessments.Add(new MeasureLine
                {
                    Instrument = m.Instrument,
                    CompletedOn = m.CompletedAt ?? m.CompletedOn?.ToString("yyyy-MM-dd") ?? "not recorded",
                    TotalScore = m.TotalScore,
                    ChangeFromPrevious = previous?.TotalScore is { } p && m.TotalScore is { } t ? t - p : null,
                    ChangeFromFirst = first?.TotalScore is { } f && m.TotalScore is { } t2 ? t2 - f : null,
                    ItemScores = m.ItemScores.Select(i => $"{i.Item}: {i.Score}").ToList(),
                    FormId = m.FormId,
                    Citations = m.OriginalCitations,
                    CopiesAndMentions = m.RepeatCitations,
                    Discrepancies = m.Discrepancies.Select(d => $"{d.Rule}: {d.Description}").ToList(),
                    DiscrepancyDetails = m.Discrepancies,
                });
                first ??= m;
                previous = m;
            }

            var list = series.ToList();
            var withItems = list.Count(m => m.ItemScores.Count > 0);
            if (withItems < list.Count)
                result.Limits.Add($"{series.First().Instrument}: item-level scores are recorded for {withItems} of {list.Count} assessments, so changes in individual items cannot be compared across the episode.");
            if (list.Count < 3)
                result.Limits.Add($"{series.First().Instrument}: only {list.Count} assessment(s) are recorded, which is too few to describe a trend.");
        }

        result.DistinctAssessments = result.Assessments.Count;
        var instruments = chosen.Select(m => m.Instrument).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.Limits.Add(instruments.Count == 0
            ? "No symptom instrument results are recorded."
            : $"The only symptom instrument recorded is {string.Join(", ", instruments)}. Symptoms it does not measure have no scored assessment in the record.");
        result.Limits.Add("A change in score shows that the score changed. The record does not establish what caused the change.");
        return result;
    }

    public ObservationResult Observations(string patientKey, string? from, string? to, string? category)
    {
        var patient = Load(patientKey);
        var (start, end, parameters) = Resolve(patient, from, to);
        return new ObservationResult
        {
            Parameters = parameters,
            CategoryFilter = category,
            Observations = ObservationLines(patient.PatientKey, start, end, category),
        };
    }

    private List<ObservationLine> ObservationLines(string patientKey, DateOnly from, DateOnly to, string? category)
    {
        return database.GetAssertions(patientKey)
            .Where(a => a.Kind == "observation" && (category is null || a.Fields.ObservationCategory == category))
            .Select(a => (Assertion: a, Date: a.ServiceDate ?? (a.StatementTime is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null)))
            .Where(x => x.Date is { } d && d >= from && d <= to)
            .OrderBy(x => x.Date).ThenBy(x => x.Assertion.PrintedDocId, StringComparer.Ordinal).ThenBy(x => x.Assertion.LineStart)
            .Select(x => new ObservationLine
            {
                Date = x.Date!.Value.ToString("yyyy-MM-dd"),
                Category = x.Assertion.Fields.ObservationCategory ?? "not categorised",
                Summary = x.Assertion.Fields.Summary ?? "",
                Quote = x.Assertion.Quote,
                Citation = x.Assertion.Citation,
                EncounterId = x.Assertion.EncounterId,
                Source = $"{x.Assertion.RecordType.Replace('_', ' ')}, {(x.Assertion.Signed == "no" ? "unsigned" : "signed")}{(x.Assertion.Signer is null ? "" : $" by {x.Assertion.Signer}")}",
                IsCopy = x.Assertion.IsCopy,
            })
            .ToList();
    }

    // ---------- passage search and the gap log ----------

    /// <summary>
    /// Keyword search over one patient's documents, for questions the abstraction does not cover.
    /// Every hit is written to the gap log, which shows what the extractor might capture next.
    /// </summary>
    public SearchResult Search(string patientKey, IEnumerable<string> terms, string question, string? suggestedKind)
    {
        var key = patientKey.Trim().ToUpperInvariant();
        var wanted = terms.Select(t => t.Trim()).Where(t => t.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new SearchResult { Terms = wanted };
        if (wanted.Count == 0) return result;

        var assertions = database.GetAssertions(key);
        var byDocument = assertions.GroupBy(a => a.DocHash).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var document in database.ListDocuments().Where(d => byDocument.ContainsKey(d.DocHash)))
        {
            var lines = document.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!wanted.Any(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                var number = i + 1;
                var covering = byDocument[document.DocHash]
                    .Where(a => a.LineStart <= number && number <= a.LineEnd)
                    .Select(a => a.Kind).Distinct().Order(StringComparer.Ordinal).ToList();
                result.Hits.Add(new PassageHit
                {
                    Citation = $"{document.PrintedId ?? document.DocHash[..12]} L{number}",
                    Passage = lines[i].Length > 600 ? lines[i][..600] + " [cut]" : lines[i],
                    CoveredByAssertion = covering.Count > 0,
                    CoveringKinds = covering,
                });
                database.RecordGap(question, key, document.DocHash, document.PrintedId, number, lines[i], string.Join(", ", wanted), suggestedKind);
                result.GapsRecorded++;
            }
        }

        return result;
    }
}

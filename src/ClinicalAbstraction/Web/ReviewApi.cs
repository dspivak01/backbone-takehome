using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Web;

/// <summary>
/// What each request the review page makes returns. Every method takes plain parameters, runs the
/// same calculations the command line runs, and returns an object ready to show: figures are
/// already strings made by the command line's own formatting, and internal values are already
/// words. Nothing here depends on the web framework, so it is tested against a temporary
/// database with no server running. Nothing here writes to the database.
/// </summary>
public sealed class ReviewApi(Database database, string outputFolder)
{
    /// <summary>The patient list shows at most this many rows. The rest are found by searching.</summary>
    public const int ListLimit = 50;

    /// <summary>Above this many patients the list offers a search box.</summary>
    public const int SearchThreshold = 10;

    /// <summary>
    /// The longest period the weekly results are worked out for. A period of centuries, typed into
    /// the address by mistake, would otherwise build hundreds of thousands of empty weeks.
    /// </summary>
    public const int LongestPeriodInDays = 3660;

    private readonly Calculations _calculations = new(database);

    // ---------- status ----------

    public StatusView Status()
    {
        var counts = database.CountRows();
        var settings = ModelSettings.FromEnvironment();
        return new StatusView
        {
            Database = Path.GetFullPath(database.FilePath),
            OutputFolder = Path.GetFullPath(outputFolder),
            Patients = database.ListPatients().Count,
            Counts = CountLabels.Where(l => counts.ContainsKey(l.Table)).Select(l => new CountLine { Label = l.Label, Count = counts[l.Table] }).ToList(),
            DocumentIds = database.ListDocuments()
                .SelectMany(d => new[] { d.PrintedId, d.DocHash[..Math.Min(8, d.DocHash.Length)] })
                .OfType<string>().Where(id => id.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList(),
            Model = new ModelView
            {
                Provider = settings.Provider == "anthropic" ? "Anthropic API" : "Amazon Bedrock",
                ExtractionModel = settings.ExtractionModel,
                AnswerModel = settings.AnswerModel,
                Region = settings.Region,
                Note = "The Patients, Trace and Gap log screens never call a model. Ask and Documents use these settings, as the ask and ingest commands do.",
            },
        };
    }

    private static readonly (string Table, string Label)[] CountLabels =
    [
        ("documents", "Documents"), ("document_copies", "Copies of documents seen"), ("extractions", "Extractions"),
        ("assertions", "Assertions kept"), ("rejected_assertions", "Assertions rejected"), ("patients", "Patients"),
        ("events", "Events"), ("weekly_results", "Weekly results"), ("answers", "Saved answers"),
        ("model_calls", "Model calls recorded"), ("gaps", "Passages in the gap log"),
    ];

    // ---------- patients ----------

    /// <summary>
    /// The patient list, as the patients command lists it. With more than <see cref="ListLimit"/>
    /// matching, the first ones are shown and the note says how many were left out.
    /// </summary>
    public PatientListView Patients(string? search)
    {
        var all = _calculations.ListPatients();
        // Reconciliation adds a warning of its own when assertions could not be linked, so this count covers them.
        var warnings = database.LoadAllAbstractions().ToDictionary(p => p.PatientKey, p => p.Warnings.Count);
        var wanted = search?.Trim() ?? "";
        var matching = wanted.Length == 0 ? all : all.Where(p => Matches(p, wanted)).ToList();

        var view = new PatientListView
        {
            PatientsInCollection = all.Count,
            Matching = matching.Count,
            Search = wanted.Length == 0 ? null : wanted,
            SearchOffered = all.Count > SearchThreshold,
            ListLimit = ListLimit,
            Patients = matching.Take(ListLimit).Select(p => new PatientRow
            {
                Key = p.PatientKey, Name = NameOf(p.Name), Mrn = p.Mrn, DateOfBirth = p.DateOfBirth ?? "Not recorded",
                Episode = p.EpisodeStart is null && p.EpisodeEnd is null ? "Not recorded" : $"{p.EpisodeStart} to {p.EpisodeEnd}",
                PlanVersions = p.PlanVersions, Encounters = p.Encounters, Warnings = warnings.GetValueOrDefault(p.PatientKey),
            }).ToList(),
        };

        if (all.Count == 0) view.EmptyCollection = EmptyCollection();
        else if (matching.Count == 0) view.Note = $"No patient matches \"{wanted}\". The collection holds {Patients(all.Count)}.";
        else if (matching.Count > ListLimit)
            view.Note = $"Showing the first {ListLimit} of {matching.Count} {(wanted.Length == 0 ? "patients" : "matching patients")}. Search by name, key or medical record number to find the others.";
        return view;
    }

    private static bool Matches(PatientMatch p, string wanted) =>
        p.PatientKey.Contains(wanted, StringComparison.OrdinalIgnoreCase)
        || (p.Mrn?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false)
        || (p.Name is not null && wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(part => p.Name.Contains(part, StringComparison.OrdinalIgnoreCase)));

    private string EmptyCollection()
    {
        var documents = database.CountRows()["documents"];
        return documents == 0
            ? "No documents have been processed into this database, so there are no patients to show. Add documents on the Documents screen, or from the command line with the ingest command, naming this database, for example: " +
              $"dotnet run --project src/ClinicalAbstraction -- ingest documents --db \"{Path.GetFullPath(database.FilePath)}\""
            : $"This database holds {documents} document(s) but no patient's abstraction, so there are no patients to show. The reconcile command builds the abstraction from the saved assertions.";
    }

    /// <summary>One patient's saved abstraction: the record's own facts, warnings first, then plans and encounters.</summary>
    public PatientView Patient(string key)
    {
        var p = Load(key);
        var definition = WeekDefinitionOf(p);
        return new PatientView
        {
            Key = p.PatientKey, Name = NameOf(p.Name), Mrn = p.Mrn, DateOfBirth = p.DateOfBirth ?? "Not recorded",
            Episode = p.Episode.Start is null && p.Episode.End is null ? "Not recorded" : $"{p.Episode.Start:yyyy-MM-dd} to {p.Episode.End:yyyy-MM-dd}",
            EpisodeSources = p.Episode.Citations,
            EpisodeNote = p.Episode.Citations.Count > 0 ? null
                : p.Episode.Start is null ? "No record states the episode dates, and no encounter is dated."
                : "No record states the episode dates. The range of recorded encounters is used.",
            Documents = p.DocumentCount, Statements = p.AssertionCount, EncounterCount = p.Encounters.Count, Version = p.Version,
            WeekDefinition = Wording.WeekDefinition(definition),
            Warnings = p.Warnings.Select(Wording.FullSentence).ToList(),
            Unlinked = p.Unlinked.Select(u => new UnlinkedRow { Source = u.Citation, Kind = Wording.Kind(u.Kind), Reason = Wording.FullSentence(u.Reason), Quote = u.Quote }).ToList(),
            Plans = p.Plans.Select(plan => new PlanRow
            {
                InEffect = $"{plan.EffectiveFrom?.ToString("yyyy-MM-dd") ?? "no stated start"} to {plan.EffectiveTo?.ToString("yyyy-MM-dd") ?? "no stated end"}",
                MinimumDays = plan.MinTherapyDaysPerWeek?.ToString() ?? "Not stated",
                MinimumMinutes = plan.MinMinutesPerWeek?.ToString() ?? "Not stated",
                Week = Wording.WeekDefinition(plan.WeekDefinition),
                Counts = plan.CountedCategories.Select(c => Vocabulary.DescribeCategory(c)).ToList(),
                DoesNotCount = plan.ExcludedCategories.Select(c => Vocabulary.DescribeCategory(c)).ToList(),
                Sources = plan.Citations,
            }).ToList(),
            Encounters = p.Encounters.Select(e =>
            {
                var line = WeeklyCalculator.Judge(p, e);
                return new EncounterRow
                {
                    Ref = e.EventKey, Encounter = Wording.EncounterLabel(e.EventKey, e.EncounterId),
                    Date = e.ServiceDate?.ToString("yyyy-MM-dd") ?? "Not recorded", Weekday = Short(e.ServiceDate?.DayOfWeek.ToString()),
                    WeekStart = e.ServiceDate is { } d ? WeeklyCalculator.WeekStart(d, definition).ToString("yyyy-MM-dd") : null,
                    Service = Vocabulary.DescribeCategory(e.Category.Value), Conclusion = Wording.Occurrence(e),
                    Minutes = Markdown.Range(e.MinutesMin, e.MinutesMax), CountedByPlan = Wording.ContactStatus(line.Status), Tone = Wording.ContactTone(line),
                    Documents = e.SourceDocuments,
                };
            }).ToList(),
            OtherRecords = p.NonEncounterRecords.Select(r => new OtherRecordRow { Source = r.Citation, Date = r.Date?.ToString("yyyy-MM-dd") ?? "Not dated", Description = r.Description }).ToList(),
        };
    }

    // ---------- the calculations for one patient ----------

    /// <summary>Days, minutes and the goal result for each week, as 'calc weekly' works them out.</summary>
    public WeeklyView Weekly(string key, string? from, string? to)
    {
        var p = Load(key);
        CheckWeeks(p, from, to);
        var r = _calculations.Weekly(p.PatientKey, Blank(from), Blank(to));
        return new WeeklyView
        {
            Parameters = Parameters(r.Parameters),
            Weeks = r.Weeks.Select(w => new WeekView
            {
                WeekStart = w.WeekStart, WeekEnd = w.WeekEnd, Label = $"{w.WeekStart} to {w.WeekEnd}",
                Partial = w.PartialWeek, PartialNote = w.PartialWeekNote,
                Days = Markdown.Range(w.DaysMin, w.DaysMax), Minutes = Markdown.Range(w.MinutesMin, w.MinutesMax), Hours = Markdown.Hours(w.HoursMin, w.HoursMax),
                Arithmetic = w.MinutesArithmetic,
                Goal = w.RequiredDays is null && w.RequiredMinutes is null ? "None in effect" : $"{w.RequiredDays?.ToString() ?? "no"} days and {w.RequiredMinutes?.ToString() ?? "no"} minutes",
                GoalSources = w.PlanCitations, PlanNote = w.PlanNote is null ? null : Wording.Text(w.PlanNote),
                Result = Wording.Result(w.Result), Tone = Wording.Tone(w.Result),
                DaysResult = Wording.Result(w.DaysResult), MinutesResult = Wording.Result(w.MinutesResult),
                Reason = Wording.Text(w.Reason), TherapyDates = w.TherapyDates, WouldSettle = w.Needs.Select(Wording.FullSentence).ToList(),
                ContactsNote = w.Contacts.Count == 0 ? "No appointment or contact is recorded in this week." : null,
                Counted = w.Contacts.Where(c => c.Counted).Select(c => Contact(p, c)).ToList(),
                MayCount = w.Contacts.Where(c => c.MayCount).Select(c => Contact(p, c)).ToList(),
                NotCounted = w.Contacts.Where(c => !c.Counted && !c.MayCount).Select(c => Contact(p, c)).ToList(),
            }).ToList(),
            TotalMinutes = Markdown.Range(r.TotalMinutesMin, r.TotalMinutesMax),
            TotalHours = Markdown.Hours(r.TotalHoursMin, r.TotalHoursMax),
            TotalArithmetic = r.TotalArithmetic,
            Unsettled = r.Unsettled.Select(Wording.Text).ToList(),
            Assumptions = r.Assumptions.Select(Wording.Text).ToList(),
            Warnings = r.Warnings.Select(Wording.FullSentence).ToList(),
        };
    }

    /// <summary>Sessions by service type, with every contact that was not counted and why, as 'calc sessions' works them out.</summary>
    public SessionsView Sessions(string key, string? from, string? to)
    {
        var p = Load(key);
        CheckDates(from, to);
        var r = _calculations.SessionCounts(p.PatientKey, Blank(from), Blank(to));
        return new SessionsView
        {
            Parameters = Parameters(r.Parameters),
            ByService = r.SessionsByCategory.Select(kv => new ServiceCount { Service = kv.Key, Sessions = kv.Value }).ToList(),
            Total = Markdown.Range(r.TotalSessions, r.TotalSessionsMax),
            DistinctDays = Markdown.Range(r.DistinctDays, r.DistinctDaysMax),
            TherapyDates = r.TherapyDates,
            Counted = r.Counted.Select(c => Contact(p, c)).ToList(),
            MayCount = r.Uncertain.Select(c => Contact(p, c)).ToList(),
            NotCounted = r.NotCounted.Select(c => Contact(p, c)).ToList(),
            Disagreements = r.Counted.Concat(r.Uncertain).Concat(r.NotCounted)
                .SelectMany(c => Disagreements(p, c, $"{c.Date} {Wording.EncounterLabel(c.EventKey, c.EncounterId)}"))
                .ToList(),
            DescribedMoreThanOnce = r.DescribedMoreThanOnce.Select(Wording.Text).ToList(),
            NotAppointments = r.RecordsThatAreNotAppointments.Select(Wording.Text).ToList(),
            Unlinked = r.UnlinkedAssertions.Select(Wording.Text).ToList(),
            Warnings = r.Warnings.Select(Wording.FullSentence).ToList(),
        };
    }

    /// <summary>
    /// One encounter with all its evidence, as 'show --encounter' prints it: how each field was
    /// decided, every value still standing, what would settle an unresolved one, the statements
    /// set aside and why, the disagreements in the record, and every statement linked to it.
    /// </summary>
    public EncounterView Encounter(string key, string id)
    {
        var p = Load(key);
        var wanted = (id ?? "").Trim();
        // The page names an encounter by its key exactly. A person may type its identifier instead.
        var e = p.Encounters.FirstOrDefault(x => x.EventKey == id)
            ?? p.Encounters.FirstOrDefault(x => x.EventKey == wanted || string.Equals(x.EncounterId?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException($"{Label(p.Name, p.PatientKey)} has no encounter \"{wanted}\".");
        var fields = new[] { e.Category, e.Disposition, e.PatientPresent, e.Start, e.End };
        var identifiers = Identifiers(e);
        // Items the reconciler recorded against a field are shown with that field. The rest, and
        // every item of an encounter saved before fields recorded their own, are shown for the
        // encounter as a whole.
        var byField = fields.SelectMany(f => f.Needs ?? []).ToHashSet();

        return new EncounterView
        {
            Ref = e.EventKey, Encounter = Wording.EncounterLabel(e.EventKey, e.EncounterId), PatientKey = p.PatientKey,
            Date = e.ServiceDate?.ToString("yyyy-MM-dd") ?? "Not recorded", Weekday = e.ServiceDate?.DayOfWeek.ToString() ?? "",
            WeekStart = e.ServiceDate is { } d ? WeeklyCalculator.WeekStart(d, WeekDefinitionOf(p)).ToString("yyyy-MM-dd") : null,
            Service = Vocabulary.DescribeCategory(e.Category.Value), ServiceLabels = e.ServiceLabels, Modality = Wording.Modality(e.Modality),
            Conclusion = Wording.Occurrence(e), Minutes = Markdown.Range(e.MinutesMin, e.MinutesMax), Arithmetic = Wording.Saved(e.Arithmetic, identifiers),
            Judgement = Contact(p, WeeklyCalculator.Judge(p, e)),
            SessionInterval = e.SessionInterval is { } s ? Interval(s) : null,
            Removed = e.Excluded.Select(Interval).ToList(),
            Fields = fields.Select(f => Field(f, identifiers)).ToList(),
            SetAside = fields.SelectMany(f => f.Overruled.Select(o => new SetAsideView
            {
                Field = Wording.Sentence(f.Field), Value = Wording.Value(f.Field, o.Value), Source = o.Citation,
                Rule = Wording.SetAsideRule(o.Rule), Reason = Stop(Wording.Saved(o.Reason, identifiers)),
            })).ToList(),
            Disagreements = e.Discrepancies.Select(x => Disagreement(x, identifiers)).ToList(),
            Notes = e.Flags.Select(Wording.FullSentence).ToList(),
            WouldSettle = e.Needs.Where(n => !byField.Contains(n)).Select(Wording.FullSentence).ToList(),
            Statements = e.Assertions.Select(a => new StatementView
            {
                Source = a.Citation, Kind = Wording.Kind(a.Kind), Record = Wording.Record(a.RecordType, a.IsCopy), Signed = Wording.Signed(a.Signed),
                Tier = Wording.Tier(a.Tier), StatedAt = a.StatementTime ?? "Not stated", LinkedBy = Wording.LinkMethod(a.LinkMethod), Quote = a.Quote,
            }).ToList(),
            Documents = e.SourceDocuments,
        };
    }

    private static FieldView Field(FieldDecision d, List<string> identifiers) => new()
    {
        Field = Wording.Sentence(d.Field),
        Decision = Wording.Decision(d.Status),
        Settled = d.IsSettled,
        Unresolved = d.Status == "unresolved",
        Value = d.IsSettled ? Wording.Value(d.Field, d.Value)
            : d.Candidates.Count > 0 ? string.Join(" or ", d.Candidates.Select(c => Wording.Value(d.Field, c.Value)))
            : "Not stated",
        Rule = Wording.DecisionRule(d),
        Explanation = Stop(Wording.Saved(d.Explanation, identifiers)),
        Sources = d.Candidates.SelectMany(c => c.Citations).Distinct().ToList(),
        Candidates = d.Candidates.Select(c => new CandidateView { Value = Wording.Value(d.Field, c.Value), Tier = Wording.Tier(c.Tier), Sources = c.Citations }).ToList(),
        WouldSettle = d.Status == "unresolved" ? (d.Needs ?? []).Select(Wording.FullSentence).ToList() : [],
    };

    /// <summary>Every source and document identifier an encounter's own sentences can name. They are never reworded.</summary>
    private static List<string> Identifiers(EncounterEvent e) =>
        e.Assertions.Select(a => a.Citation).Concat(e.SourceDocuments).Distinct().ToList();

    private static DisagreementView Disagreement(Discrepancy d, List<string> identifiers, string? encounter = null) => new()
    {
        Encounter = encounter,
        Rule = Wording.DisagreementRule(d.Rule),
        Description = Stop(Wording.Saved(d.Description, identifiers)),
        Sources = d.Citations,
    };

    private static string Stop(string text) => text.Length == 0 || text[^1] is '.' or '?' ? text : text + ".";

    private static IntervalView Interval(TimeSpanMinutes s) => new() { Times = s.ToString(), Reason = Wording.Text(s.Reason), Sources = s.Citations };

    /// <summary>Everything recorded for one day, as 'calc day' works it out for one patient.</summary>
    public DayView Day(string key, string? date)
    {
        var p = Load(key);
        if (string.IsNullOrWhiteSpace(date)) throw new RequestException("Give a date as yyyy-MM-dd.");
        ReadDate(date);
        var r = _calculations.Day(p.PatientKey, date);
        return new DayView
        {
            Parameters = Parameters(r.Parameters),
            Date = r.Date,
            TherapyContacts = Markdown.Range(r.TherapyContacts, r.TherapyContactsMax),
            Minutes = Markdown.Range(r.MinutesMin, r.MinutesMax),
            Arithmetic = r.MinutesArithmetic,
            Contacts = r.Contacts.Select(c => Contact(p, c)).ToList(),
            OtherRecords = r.OtherRecords.Select(Wording.Text).ToList(),
            Observations = r.Observations.Select(Observation).ToList(),
        };
    }

    /// <summary>Symptom assessments in date order, as 'calc measures' works them out.</summary>
    public MeasuresView Measures(string key, string? instrument)
    {
        var p = Load(key);
        var r = _calculations.Measures(p.PatientKey, string.IsNullOrWhiteSpace(instrument) ? null : instrument.Trim());
        return new MeasuresView
        {
            Parameters = Parameters(r.Parameters),
            DistinctAssessments = r.DistinctAssessments,
            Assessments = r.Assessments.Select(a => new MeasureRow
            {
                Instrument = a.Instrument, Completed = Wording.Timestamp(Wording.Sentence(a.CompletedOn)), Total = a.TotalScore?.ToString() ?? "Conflicting",
                ChangeFromPrevious = Markdown.Signed(a.ChangeFromPrevious), ChangeFromFirst = Markdown.Signed(a.ChangeFromFirst),
                ItemScores = a.ItemScores.Count == 0 ? "Not recorded" : string.Join("; ", a.ItemScores),
                FormId = a.FormId, Sources = a.Citations, CopiesAndMentions = a.CopiesAndMentions,
                Disagreements = a.DiscrepancyDetails.Select(d => Disagreement(d, [.. a.Citations, .. a.CopiesAndMentions])).ToList(),
            }).ToList(),
            Limits = r.Limits.Select(Wording.FullSentence).ToList(),
        };
    }

    /// <summary>Quoted observations, as 'calc observations' lists them.</summary>
    public ObservationsView Observations(string key, string? from, string? to, string? category)
    {
        var p = Load(key);
        CheckDates(from, to);
        var chosen = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var r = _calculations.Observations(p.PatientKey, Blank(from), Blank(to), chosen);
        return new ObservationsView
        {
            Parameters = Parameters(r.Parameters),
            Category = chosen is null ? null : Wording.ObservationCategory(chosen),
            Observations = r.Observations.Select(Observation).ToList(),
        };
    }

    private static ObservationView Observation(ObservationLine o) => new()
    {
        Date = o.Date, Category = Wording.ObservationCategory(o.Category), Summary = o.Summary, Quote = o.Quote,
        Source = o.Citation, Encounter = o.EncounterId, Record = Wording.Text(o.Source) + (o.IsCopy ? " (copy)" : ""),
    };

    // ---------- the collection ----------

    /// <summary>Patients with two consecutive weeks below the goal, as 'calc consecutive' works it out.</summary>
    public ConsecutiveView Consecutive(string? from, string? to)
    {
        CheckDates(from, to);
        var r = _calculations.ConsecutiveWeeks(null, Blank(from), Blank(to));

        static ConsecutiveGroup Group(string label, List<PatientConsecutive> patients) =>
            new() { Label = label, Patients = patients.Select(p => new PatientLink { Key = p.PatientKey, Label = Label(p.PatientName, p.PatientKey) }).ToList() };

        static PairView Pair(WeekPair x, string kind) => new()
        {
            Weeks = $"{x.FirstWeek} and {x.SecondWeek}", Kind = kind,
            FirstWeek = x.FirstWeek, FirstResult = Wording.Sentence(x.FirstResult), FirstTone = Wording.ToneOfWords(x.FirstResult), FirstDetail = Wording.Text(x.FirstDetail),
            SecondWeek = x.SecondWeek, SecondResult = Wording.Sentence(x.SecondResult), SecondTone = Wording.ToneOfWords(x.SecondResult), SecondDetail = Wording.Text(x.SecondDetail),
            FirstGoalSources = x.FirstGoalSources, FirstContactSources = x.FirstContactSources,
            SecondGoalSources = x.SecondGoalSources, SecondContactSources = x.SecondContactSources,
            WouldSettle = x.WouldBeSettledBy.Select(Wording.FullSentence).ToList(),
        };

        var listed = r.Included.Concat(r.DependsOnUnresolvedDocumentation).Concat(r.OnlyWithPartialWeek).ToList();
        return new ConsecutiveView
        {
            PatientsExamined = r.PatientsExamined,
            // Every patient is named in the groups above, but the pairs of weeks are shown for at
            // most as many patients as the patient list shows, so a large collection stays readable.
            Note = listed.Count > ListLimit
                ? $"The pairs of weeks are shown for the first {ListLimit} of the {listed.Count} patients named above. Each patient's own page shows their weekly results."
                : null,
            Period = $"{Wording.Sentence(r.From)} to {r.To}",
            Groups =
            [
                Group(Wording.ConsecutiveStatus("included"), r.Included),
                Group(Wording.ConsecutiveStatus("depends_on_unresolved_documentation"), r.DependsOnUnresolvedDocumentation),
                Group(Wording.ConsecutiveStatus("only_with_a_partial_week"), r.OnlyWithPartialWeek),
                new ConsecutiveGroup { Label = Wording.ConsecutiveStatus("not_included"), Named = r.NotIncluded },
            ],
            Details = listed.Take(ListLimit).Select(p => new ConsecutivePatient
            {
                Key = p.PatientKey, Label = Label(p.PatientName, p.PatientKey), Status = Wording.ConsecutiveStatus(p.Status),
                Pairs = p.DefinitePairs.Select(x => Pair(x, "Definite"))
                    .Concat(p.PossiblePairs.Select(x => Pair(x, "Possible")))
                    .Concat(p.PairsWithPartialWeek.Select(x => Pair(x, "Includes a partial week")))
                    .ToList(),
            }).ToList(),
            Assumptions = r.Assumptions.Select(Wording.Text).ToList(),
        };
    }

    /// <summary>One row per patient, as 'calc summary' works it out.</summary>
    public SummaryView CollectionSummary(string? from, string? to)
    {
        CheckDates(from, to);
        var r = _calculations.CollectionSummary(Blank(from), Blank(to));
        var services = r.Patients.SelectMany(p => p.SessionsByCategory.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        return new SummaryView
        {
            Period = $"{Wording.Sentence(r.From)} to {r.To}",
            DateRangeSource = Wording.FullSentence(r.DateRangeSource),
            Notes = r.Notes.Select(Wording.Text).ToList(),
            Services = services,
            Note = r.Patients.Count > ListLimit
                ? $"Showing the first {ListLimit} of {r.Patients.Count} patients, as the patient list does. Each patient's own page shows their figures."
                : null,
            Rows = r.Patients.Take(ListLimit).Select(p => new SummaryRow
            {
                Key = p.PatientKey, Label = Label(p.PatientName, p.PatientKey),
                Period = p.From == "not recorded" ? "Not recorded" : $"{p.From} to {p.To}",
                Sessions = services.Select(s => p.SessionsByCategory.GetValueOrDefault(s, 0)).ToList(),
                Total = Markdown.Range(p.TotalSessions, p.TotalSessionsMax),
                DistinctDays = Markdown.Range(p.DistinctDays, p.DistinctDaysMax),
                Minutes = Markdown.Range(p.MinutesMin, p.MinutesMax),
                WeeksExamined = p.WeeksExamined, WeeksMet = p.WeeksMet, WeeksNotMet = p.WeeksNotMet,
                WeeksUndetermined = p.WeeksUndetermined, WeeksWithNoGoal = p.WeeksWithNoGoal, PartialWeeks = p.PartialWeeks,
                Warnings = p.Warnings.Select(Wording.FullSentence).ToList(),
            }).ToList(),
            Assumptions = r.Assumptions.Select(Wording.Text).ToList(),
            Warnings = r.Patients.SelectMany(p => p.Warnings.Select(w => $"{Label(p.PatientName, p.PatientKey)}: {Wording.FullSentence(w)}")).ToList(),
        };
    }

    // ---------- source lines ----------

    /// <summary>
    /// The lines a source points to, as the source command prints them: the cited lines with
    /// context either side, the file they came from, and every assertion extracted from them. The
    /// text is the copy saved in the database, so the original file is not needed. When two
    /// documents carry the identifier and the source cannot be tied to one of them, each is given
    /// in <see cref="SourceView.Candidates"/> with its file name, and nothing is shown as if it were the one.
    /// </summary>
    public SourceView Source(string? document, string? from, string? to, string? context, string? patient = null)
    {
        if (string.IsNullOrWhiteSpace(document)) throw new RequestException("Name a document, for example BH-D005.");
        var first = ReadLine(from, "first line");
        var last = string.IsNullOrWhiteSpace(to) ? first : ReadLine(to, "last line");
        var around = string.IsNullOrWhiteSpace(context) ? 1
            : int.TryParse(context, out var c) && c is >= 0 and <= 50 ? c
            : throw new RequestException("The context is the number of lines to show either side, from 0 to 50.");

        var id = document.Trim();
        if (last < first) throw new RequestException($"The last line asked for, {last}, comes before the first, {first}.");
        var found = SourceLookup.Find(database, id, first, last, string.IsNullOrWhiteSpace(patient) ? null : patient)
            ?? throw new NotFoundException(id.Length < SourceLookup.ShortestHashPrefix && id.All(Uri.IsHexDigit)
                ? $"No document \"{id}\" is in this database. A document can be named by its printed identifier, or by the first {SourceLookup.ShortestHashPrefix} or more characters of its hash."
                : $"No document \"{id}\" is in this database.");

        if (!found.Shared)
        {
            var only = found.Documents[0];
            if (first < 1 || last > only.Lines.Length)
                throw new RequestException(first == last
                    ? $"{only.Name} has {only.Lines.Length} lines. Line {first} is outside it."
                    : $"{only.Name} has {only.Lines.Length} lines. Lines {first} to {last} are outside it.");
            var view = Lines(only, first, last, around);
            view.Note = found.Note;
            return view;
        }

        return new SourceView
        {
            Document = found.Identifier,
            From = first,
            To = last,
            Heading = $"{found.Identifier}, {LineWords(first, last)}",
            Note = found.Note,
            Candidates = found.Documents.Select(d => Lines(d, first, last, around)).ToList(),
        };
    }

    private static SourceView Lines(SourceLookup.Found found, int first, int last, int around)
    {
        var lines = found.Lines;
        var inside = first >= 1 && last <= lines.Length;
        return new SourceView
        {
            Document = found.Name,
            File = found.Document.Path,
            LineCount = lines.Length,
            From = first,
            To = last,
            Heading = $"{found.Name}, {LineWords(first, last)}",
            Lines = !inside ? [] : Enumerable.Range(Math.Max(1, first - around), Math.Min(lines.Length, last + around) - Math.Max(1, first - around) + 1)
                .Select(n => new SourceLine { Number = n, Text = lines[n - 1], Cited = n >= first && n <= last })
                .ToList(),
            Note = inside ? null : $"This document has {lines.Length} lines, so {LineWords(first, last)} is outside it.",
            StatementsHeading = $"Assertions extracted from {(first == last ? "this line" : "these lines")}: {found.Citing.Count}",
            Statements = found.Citing.Select(Statement).ToList(),
        };
    }

    /// <summary>One assertion as step 4 of the trace and the document page show it.</summary>
    internal static SourceStatement Statement(Assertion a) => new()
    {
        Source = a.Citation, Kind = Wording.Kind(a.Kind), Encounter = a.EncounterId, Record = Wording.Record(a.RecordType, a.IsCopy),
        Signed = Wording.Signed(a.Signed), Recorded = Recorded(a.Fields), Quote = a.Quote,
    };

    private static string LineWords(int first, int last) => first == last ? $"line {first}" : $"lines {first} to {last}";

    /// <summary>What one statement recorded, with a plain label for each field the extractor filled in.</summary>
    private static List<RecordedValue> Recorded(AssertionFields f)
    {
        var list = new List<RecordedValue>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(new RecordedValue { Label = label, Value = value });
        }
        static string? Join(IEnumerable<string>? values, string separator = ", ") => values is null ? null : string.Join(separator, values);

        Add("Service type", f.ServiceCategory is null ? null : Vocabulary.DescribeCategory(f.ServiceCategory));
        Add("Service as written", f.ServiceLabel);
        Add("Modality", f.Modality is null ? null : Wording.Modality(f.Modality));
        Add("Clinicians", Join(f.Clinicians));
        Add("Scheduled start", f.ScheduledStart);
        Add("Scheduled end", f.ScheduledEnd);
        Add("Session start", f.SessionStart);
        Add("Session end", f.SessionEnd);
        Add("Disposition", f.Disposition is null ? null : Wording.Sentence(Vocabulary.DescribeDisposition(f.Disposition)));
        Add("Patient present", f.PatientPresent switch { null => null, "yes" => "Yes", "no" => "No", "part" => "For part of the time", var x => Wording.Words(x) });
        Add("Arrival", f.Arrival);
        Add("Departure", f.Departure);
        Add("Intervals", Join(f.Intervals?.Select(i => $"{i.Start ?? "not stated"} to {i.End ?? "not stated"}")));
        Add("Reason for the interval", f.IntervalReason is null ? null : Wording.Words(f.IntervalReason));
        Add("Minutes", f.Minutes?.ToString());
        Add("What the minutes measure", f.DurationMeasures switch
        {
            null => null, "patient_present" => "Time the patient was present", "session" => "The session", "visit" => "The visit", var x => Wording.Words(x),
        });
        Add("Field corrected", f.CorrectedField is null ? null : Wording.Words(f.CorrectedField));
        Add("Old value", f.OldValue);
        Add("New value", f.NewValue);
        Add("What did not occur", f.WhatDidNotOccur);
        Add("Minimum therapy days per week", f.MinTherapyDaysPerWeek?.ToString());
        Add("Minimum minutes per week", f.MinMinutesPerWeek?.ToString());
        Add("Week", f.WeekDefinition is null or "not_stated" ? null : Wording.WeekDefinition(f.WeekDefinition));
        Add("Services that count", Join(f.CountedCategories?.Select(c => Vocabulary.DescribeCategory(c))));
        Add("Services that do not count", Join(f.ExcludedCategories?.Select(c => Vocabulary.DescribeCategory(c))));
        Add("Effective from", f.EffectiveFrom);
        Add("Effective to", f.EffectiveTo);
        Add("Episode start", f.EpisodeStart);
        Add("Episode end", f.EpisodeEnd);
        Add("Instrument", f.Instrument);
        Add("Total score", f.TotalScore?.ToString());
        Add("Completed", f.CompletedAt);
        Add("Form", f.FormId);
        Add("Item scores", Join(f.ItemScores?.Select(i => $"{i.Item}: {i.Score}"), "; "));
        Add("A restatement of an earlier result", f.IsRestatement == true ? "Yes" : null);
        Add("Observation category", f.ObservationCategory is null ? null : Wording.ObservationCategory(f.ObservationCategory));
        Add("Summary", f.Summary);
        Add("Authorization number", f.AuthorizationNumber);
        Add("Quantity", f.Quantity?.ToString());
        Add("Unit", f.Unit);
        Add("Period start", f.PeriodStart);
        Add("Period end", f.PeriodEnd);
        Add("Charge", f.ChargeId);
        Add("Description", f.Description);
        Add("Posted", f.PostedAt);
        Add("Kind of assertion", f.OtherCategory is null ? null : Wording.Words(f.OtherCategory));
        return list;
    }

    // ---------- helpers ----------

    /// <summary>
    /// A patient by key. The key is tried exactly as given first, because a key can hold any
    /// character a document holds, then trimmed and in capitals, as the command line takes it.
    /// </summary>
    private PatientAbstraction Load(string key)
    {
        var given = key ?? "";
        var wanted = given.Trim();
        if (wanted.Length == 0) throw new RequestException("Name a patient by their key.");
        return database.LoadAbstraction(given)
            ?? database.LoadAbstraction(wanted.ToUpperInvariant())
            ?? throw new NotFoundException($"No patient with the key \"{wanted}\" is in this database.");
    }

    private static ContactView Contact(PatientAbstraction p, ContactLine c) => new()
    {
        Ref = c.EventKey,
        Encounter = Wording.EncounterLabel(c.EventKey, c.EncounterId),
        Date = c.Date,
        Weekday = Short(c.Weekday),
        WeekStart = DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", out var d) ? WeeklyCalculator.WeekStart(d, WeekDefinitionOf(p)).ToString("yyyy-MM-dd") : null,
        Service = c.CategoryName,
        Status = Wording.ContactStatus(c.Status),
        Tone = Wording.ContactTone(c),
        Minutes = c.Counted || c.MayCount ? Markdown.Range(c.Counted ? c.MinutesMin : 0, c.MinutesMax) : "0",
        Detail = Wording.Text(c.Counted ? c.Arithmetic : c.Reason),
        Reason = Wording.Text(c.Reason),
        Sources = c.Citations.Count > 0 ? c.Citations : c.SourceDocuments,
        Notes = c.Flags.Select(Wording.FullSentence).ToList(),
        Disagreements = Disagreements(p, c),
        WouldSettle = c.Needs.Select(Wording.FullSentence).ToList(),
    };

    /// <summary>A contact's disagreements, from its saved event, so each keeps its rule and its sources apart from its sentence.</summary>
    private static List<DisagreementView> Disagreements(PatientAbstraction p, ContactLine c, string? encounter = null) =>
        p.Encounters.FirstOrDefault(e => e.EventKey == c.EventKey) is { } e
            ? e.Discrepancies.Select(d => Disagreement(d, Identifiers(e), encounter)).ToList()
            : [];

    private static ParametersView Parameters(ResolvedParameters r) => new()
    {
        Patient = Label(r.PatientName, r.PatientKey), PatientKey = r.PatientKey, From = r.From, To = r.To, Dates = $"{r.From} to {r.To}",
        DateRangeSource = Wording.FullSentence(r.DateRangeSource), Week = r.WeekDefinition, Version = r.AbstractionVersion,
    };

    /// <summary>The week definition the weekly calculation uses: the first plan's, or Monday to Sunday.</summary>
    private static string WeekDefinitionOf(PatientAbstraction p) => p.Plans.FirstOrDefault()?.WeekDefinition ?? "monday_sunday";

    /// <summary>
    /// Refuses a period the weekly calculation cannot work out sensibly: one with no start or end,
    /// because the record states no episode and dates no contact, or one of more than ten years.
    /// The ends are settled the way the calculation settles them.
    /// </summary>
    private static void CheckWeeks(PatientAbstraction p, string? from, string? to)
    {
        CheckDates(from, to);
        var dated = p.Encounters.Where(e => e.ServiceDate is not null).Select(e => e.ServiceDate!.Value).ToList();
        var start = (string.IsNullOrWhiteSpace(from) ? null : Parse.Date(from)) ?? p.Episode.Start ?? (dated.Count > 0 ? dated.Min() : null);
        var end = (string.IsNullOrWhiteSpace(to) ? null : Parse.Date(to)) ?? p.Episode.End ?? (dated.Count > 0 ? dated.Max() : null);
        if (start is null || end is null)
            throw new RequestException($"The record for {Label(p.Name, p.PatientKey)} states no episode dates and holds no dated contact, so there are no weeks to show.");
        if (end.Value.DayNumber - start.Value.DayNumber > LongestPeriodInDays)
            throw new RequestException("The period is longer than ten years. Give a shorter period.");
    }

    /// <summary>
    /// Reads the dates of a request. The calculations treat a date they cannot read as not given,
    /// which on a page would silently show a different period, so an unreadable date is refused.
    /// </summary>
    private static void CheckDates(string? from, string? to)
    {
        var start = string.IsNullOrWhiteSpace(from) ? (DateOnly?)null : ReadDate(from);
        var end = string.IsNullOrWhiteSpace(to) ? (DateOnly?)null : ReadDate(to);
        if (start > end) throw new RequestException($"The period ends on {end:yyyy-MM-dd}, which is before it begins on {start:yyyy-MM-dd}.");
    }

    private static DateOnly ReadDate(string text)
    {
        var date = Parse.Date(text) ?? throw new RequestException($"\"{text.Trim()}\" is not a date. Use yyyy-MM-dd.");
        if (date.Year is < 1900 or > 2199) throw new RequestException($"{date:yyyy-MM-dd} is outside the years this page works with, 1900 to 2199.");
        return date;
    }

    private static int ReadLine(string? text, string which) =>
        int.TryParse(text?.Trim(), out var n) ? n : throw new RequestException($"Give the {which} as a number.");

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Short(string? weekday) => weekday is null ? "" : weekday.Length >= 3 ? weekday[..3] : weekday;

    private static string NameOf(string? name) => name ?? "Name not recorded";

    private static string Label(string? name, string key) => $"{NameOf(name)} ({key})";

    private static string Patients(int count) => count == 1 ? "1 patient" : $"{count} patients";
}

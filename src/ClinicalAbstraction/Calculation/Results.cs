using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Calculation;

/// <summary>The parameters a calculation actually ran with, and where each came from.</summary>
public sealed class ResolvedParameters
{
    public string PatientKey { get; set; } = "";
    public string? PatientName { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string DateRangeSource { get; set; } = "";
    public string WeekDefinition { get; set; } = "";
    public string AbstractionVersion { get; set; } = "";
}

/// <summary>One service contact as a calculation sees it, judged against the plan in effect on its date.</summary>
public sealed class ContactLine
{
    public string EventKey { get; set; } = "";
    public string? EncounterId { get; set; }
    public string Date { get; set; } = "";
    public string Weekday { get; set; } = "";
    public string Category { get; set; } = "";
    public string CategoryName { get; set; } = "";

    /// <summary>counted, excluded_by_plan, not_a_session, not_addressed_by_plan, or uncertain.</summary>
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public string Arithmetic { get; set; } = "";
    public List<string> SourceDocuments { get; set; } = [];
    public List<string> Citations { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public List<string> Discrepancies { get; set; } = [];
    public List<string> Needs { get; set; } = [];

    public bool Counted => Status == "counted";
    public bool MayCount => Status is "uncertain" or "not_addressed_by_plan";
}

public sealed class SessionCountResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public Dictionary<string, int> SessionsByCategory { get; set; } = [];
    public int TotalSessions { get; set; }
    public int TotalSessionsMax { get; set; }
    public int DistinctDays { get; set; }
    public int DistinctDaysMax { get; set; }
    public List<string> TherapyDates { get; set; } = [];
    public List<ContactLine> Counted { get; set; } = [];
    public List<ContactLine> Uncertain { get; set; } = [];
    public List<ContactLine> NotCounted { get; set; } = [];

    /// <summary>Contacts described by more than one document, counted once.</summary>
    public List<string> DescribedMoreThanOnce { get; set; } = [];
    public List<string> RecordsThatAreNotAppointments { get; set; } = [];
    public List<string> UnlinkedAssertions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class WeekResult
{
    public string WeekStart { get; set; } = "";
    public string WeekEnd { get; set; } = "";
    public int DaysInRange { get; set; }
    public bool PartialWeek { get; set; }
    public string? PartialWeekNote { get; set; }

    public int? RequiredDays { get; set; }
    public int? RequiredMinutes { get; set; }
    public List<string> PlanCitations { get; set; } = [];
    public string? PlanNote { get; set; }

    public int DaysMin { get; set; }
    public int DaysMax { get; set; }
    public List<string> TherapyDates { get; set; } = [];
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public double HoursMin { get; set; }
    public double HoursMax { get; set; }
    public string MinutesArithmetic { get; set; } = "";

    /// <summary>met, not_met, cannot_be_determined, or no_goal.</summary>
    public string DaysResult { get; set; } = "";
    public string MinutesResult { get; set; } = "";
    public string Result { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Needs { get; set; } = [];
    public List<ContactLine> Contacts { get; set; } = [];
}

public sealed class WeeklyResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public List<WeekResult> Weeks { get; set; } = [];
    public int TotalMinutesMin { get; set; }
    public int TotalMinutesMax { get; set; }
    public double TotalHoursMin { get; set; }
    public double TotalHoursMax { get; set; }
    public string TotalArithmetic { get; set; } = "";
    public List<string> Unsettled { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class WeekPair
{
    public string FirstWeek { get; set; } = "";
    public string SecondWeek { get; set; } = "";
    public string FirstResult { get; set; } = "";
    public string SecondResult { get; set; } = "";
    public string FirstDetail { get; set; } = "";
    public string SecondDetail { get; set; } = "";
    public bool IncludesPartialWeek { get; set; }
    public List<string> WouldBeSettledBy { get; set; } = [];

    /// <summary>The sources of the plan that set each week's goal.</summary>
    public List<string> FirstGoalSources { get; set; } = [];
    public List<string> SecondGoalSources { get; set; } = [];

    /// <summary>
    /// The sources of every contact in each week, counted or not, including every value still
    /// standing for a field the record leaves unresolved.
    /// </summary>
    public List<string> FirstContactSources { get; set; } = [];
    public List<string> SecondContactSources { get; set; } = [];
}

public sealed class PatientConsecutive
{
    public string PatientKey { get; set; } = "";
    public string? PatientName { get; set; }

    /// <summary>included, depends_on_unresolved_documentation, only_with_a_partial_week, or not_included.</summary>
    public string Status { get; set; } = "";
    public List<WeekPair> DefinitePairs { get; set; } = [];
    public List<WeekPair> PossiblePairs { get; set; } = [];
    public List<WeekPair> PairsWithPartialWeek { get; set; } = [];
}

public sealed class ConsecutiveWeeksResult
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int PatientsExamined { get; set; }
    public List<PatientConsecutive> Included { get; set; } = [];
    public List<PatientConsecutive> DependsOnUnresolvedDocumentation { get; set; } = [];
    public List<PatientConsecutive> OnlyWithPartialWeek { get; set; } = [];
    public List<string> NotIncluded { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

public sealed class PlanPeriod
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Days { get; set; }
    public int? RequiredDays { get; set; }
    public int? RequiredMinutes { get; set; }
    public List<string> CountedCategories { get; set; } = [];
    public List<string> PlanCitations { get; set; } = [];
    public Dictionary<string, int> SessionsByCategory { get; set; } = [];
    public Dictionary<string, string> MinutesByCategory { get; set; } = [];
    public int TotalSessions { get; set; }
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public double SessionsPerWeek { get; set; }
    public double MinutesPerWeekMin { get; set; }
    public double MinutesPerWeekMax { get; set; }
}

public sealed class PlanChangeResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public bool PlanChangeFound { get; set; }
    public string Summary { get; set; } = "";
    public List<PlanPeriod> Periods { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

public sealed class ObservationLine
{
    public string Date { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Citation { get; set; } = "";
    public string? EncounterId { get; set; }
    public string Source { get; set; } = "";
    public bool IsCopy { get; set; }
}

public sealed class ObservationResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public string? CategoryFilter { get; set; }
    public List<ObservationLine> Observations { get; set; } = [];
}

public sealed class DayResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public string Date { get; set; } = "";
    public int TherapyContacts { get; set; }
    public int TherapyContactsMax { get; set; }
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public string MinutesArithmetic { get; set; } = "";
    public List<ContactLine> Contacts { get; set; } = [];
    public List<EncounterEvent> Events { get; set; } = [];
    public List<ObservationLine> Observations { get; set; } = [];
    public List<string> OtherRecords { get; set; } = [];
}

public sealed class MeasureLine
{
    public string Instrument { get; set; } = "";
    public string CompletedOn { get; set; } = "";
    public int? TotalScore { get; set; }
    public int? ChangeFromPrevious { get; set; }
    public int? ChangeFromFirst { get; set; }
    public List<string> ItemScores { get; set; } = [];
    public string? FormId { get; set; }
    public List<string> Citations { get; set; } = [];
    public List<string> CopiesAndMentions { get; set; } = [];
    public List<string> Discrepancies { get; set; } = [];

    /// <summary>The same disagreements with their rule kept apart, for the review page. Not part of the result a model reads.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<Discrepancy> DiscrepancyDetails { get; set; } = [];
}

public sealed class MeasureSeriesResult
{
    public ResolvedParameters Parameters { get; set; } = new();
    public int DistinctAssessments { get; set; }
    public List<MeasureLine> Assessments { get; set; } = [];

    /// <summary>Limits on what the measurements can support, worked out from what is missing.</summary>
    public List<string> Limits { get; set; } = [];
}

public sealed class PassageHit
{
    public string Citation { get; set; } = "";
    public string Passage { get; set; } = "";
    public bool CoveredByAssertion { get; set; }
    public List<string> CoveringKinds { get; set; } = [];
}

public sealed class SearchResult
{
    public string Note { get; set; } = "These passages were found by text search, not taken from the abstraction. Counts must not be based on them.";
    public List<string> Terms { get; set; } = [];
    public List<PassageHit> Hits { get; set; } = [];
    public int GapsRecorded { get; set; }
}

public sealed class PatientMatch
{
    public string PatientKey { get; set; } = "";
    public string? Mrn { get; set; }
    public string? Name { get; set; }
    public string? DateOfBirth { get; set; }
    public string? EpisodeStart { get; set; }
    public string? EpisodeEnd { get; set; }
    public int PlanVersions { get; set; }
    public int Encounters { get; set; }
    public string AbstractionVersion { get; set; } = "";
}

public sealed class PatientSearchResult
{
    public string Query { get; set; } = "";
    public List<PatientMatch> Matches { get; set; } = [];
    public string Note { get; set; } = "";
}

/// <summary>One patient a question covers, with how much the record holds for the period.</summary>
public sealed class PatientInPeriod
{
    public string PatientKey { get; set; } = "";
    public string? PatientName { get; set; }

    /// <summary>Every appointment or contact the record describes, including ones that did not take place.</summary>
    public int ContactsRecorded { get; set; }
    public int DaysWithContacts { get; set; }
    public string FirstContact { get; set; } = "";
    public string LastContact { get; set; } = "";
}

/// <summary>
/// Who a question covers when it names no patient. Code works this out from the saved events, so
/// the same question always covers the same patients.
/// </summary>
public sealed class PatientsInPeriodResult
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int PatientsInCollection { get; set; }
    public int PatientsWhoFit { get; set; }
    public int FullDetailLimit { get; set; }

    /// <summary>False when more patients fit than the limit. The answer then gives a summary and no detail.</summary>
    public bool FullDetailAllowed { get; set; }
    public List<PatientInPeriod> Patients { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>One patient's row in the table of everyone who has a contact on a date.</summary>
public sealed class PatientDayLine
{
    public string PatientKey { get; set; } = "";
    public string? PatientName { get; set; }
    public int ContactsRecorded { get; set; }
    public int TherapyContacts { get; set; }
    public int TherapyContactsMax { get; set; }
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
}

/// <summary>
/// One date across the collection: every patient who has a contact that day. The summary has a
/// row for each of them. Full detail is given for all of them or, above the limit, for none.
/// </summary>
public sealed class CollectionDayResult
{
    public string Date { get; set; } = "";
    public int PatientsInCollection { get; set; }
    public int PatientsWhoFit { get; set; }
    public int FullDetailLimit { get; set; }
    public bool FullDetailGiven { get; set; }
    public List<PatientDayLine> Summary { get; set; } = [];

    /// <summary>The same result a one-patient day reconstruction gives, once for each patient. Empty above the limit.</summary>
    public List<DayResult> Patients { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>One patient's row in the collection summary.</summary>
public sealed class PatientSummaryLine
{
    public string PatientKey { get; set; } = "";
    public string? PatientName { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public Dictionary<string, int> SessionsByCategory { get; set; } = [];
    public int TotalSessions { get; set; }
    public int TotalSessionsMax { get; set; }
    public int DistinctDays { get; set; }
    public int DistinctDaysMax { get; set; }
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public int WeeksExamined { get; set; }
    public int WeeksMet { get; set; }
    public int WeeksNotMet { get; set; }
    public int WeeksUndetermined { get; set; }
    public int WeeksWithNoGoal { get; set; }
    public int PartialWeeks { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Sessions, therapy days, minutes and weekly goal results for every patient who fits, one row
/// each. Each row is worked out the same way as that patient's own session counts and weekly results.
/// </summary>
public sealed class CollectionSummaryResult
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string DateRangeSource { get; set; } = "";
    public int PatientsInCollection { get; set; }
    public int PatientsWhoFit { get; set; }
    public int FullDetailLimit { get; set; }

    /// <summary>False when more patients fit than the limit. The answer then gives this table and no detail.</summary>
    public bool FullDetailAllowed { get; set; }
    public List<PatientSummaryLine> Patients { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

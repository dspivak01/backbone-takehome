namespace ClinicalAbstraction.Web;

// The objects the page receives. Every number in them is copied from a calculation result, and
// every figure the page shows is already a string made by the same code the command line uses
// (Markdown.Range, Markdown.Hours). The page only places these strings on screen.

/// <summary>A plain message for a request that cannot be answered, such as an unknown patient.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>
/// A plain message, written by this program, for a request it cannot answer as asked, such as a
/// date it cannot read. Only this and <see cref="NotFoundException"/> carry their message to the
/// page. A message from anywhere else, .NET included, never reaches it.
/// </summary>
public sealed class RequestException(string message) : Exception(message);

/// <summary>A plain message for a request refused because other work is running, such as a second ingest.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>A plain message for a request that needs something not configured, such as a model.</summary>
public sealed class UnavailableException(string message) : Exception(message);

public sealed class ErrorView
{
    public string Error { get; set; } = "";
}

// ---------- status ----------

public sealed class StatusView
{
    public string Database { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public int Patients { get; set; }
    public List<CountLine> Counts { get; set; } = [];

    /// <summary>Every document identifier a source can name, so the page can recognise sources inside sentences.</summary>
    public List<string> DocumentIds { get; set; } = [];
    public ModelView Model { get; set; } = new();
}

public sealed class CountLine
{
    public string Label { get; set; } = "";
    public long Count { get; set; }
}

public sealed class ModelView
{
    public string Provider { get; set; } = "";
    public string ExtractionModel { get; set; } = "";
    public string AnswerModel { get; set; } = "";
    public string Region { get; set; } = "";
    public string Note { get; set; } = "";
}

// ---------- patients ----------

public sealed class PatientListView
{
    public int PatientsInCollection { get; set; }
    public int Matching { get; set; }
    public string? Search { get; set; }

    /// <summary>True when the collection holds more patients than fit comfortably in one list.</summary>
    public bool SearchOffered { get; set; }
    public int ListLimit { get; set; }
    public List<PatientRow> Patients { get; set; } = [];

    /// <summary>Says how many are shown when the list is cut, or why it is empty.</summary>
    public string? Note { get; set; }
    public string? EmptyCollection { get; set; }
}

public sealed class PatientRow
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Mrn { get; set; }
    public string DateOfBirth { get; set; } = "";
    public string Episode { get; set; } = "";
    public int PlanVersions { get; set; }
    public int Encounters { get; set; }
    public int Warnings { get; set; }
}

public sealed class PatientView
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Mrn { get; set; }
    public string DateOfBirth { get; set; } = "";
    public string Episode { get; set; } = "";
    public List<string> EpisodeSources { get; set; } = [];
    public string? EpisodeNote { get; set; }
    public int Documents { get; set; }
    public int Statements { get; set; }
    public int EncounterCount { get; set; }
    public string Version { get; set; } = "";
    public string WeekDefinition { get; set; } = "";

    /// <summary>Failed or incomplete extractions and statements that could not be linked. The page shows them first.</summary>
    public List<string> Warnings { get; set; } = [];
    public List<UnlinkedRow> Unlinked { get; set; } = [];
    public List<PlanRow> Plans { get; set; } = [];
    public List<EncounterRow> Encounters { get; set; } = [];
    public List<OtherRecordRow> OtherRecords { get; set; } = [];
}

public sealed class UnlinkedRow
{
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Quote { get; set; } = "";
}

public sealed class OtherRecordRow
{
    public string Source { get; set; } = "";
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class PlanRow
{
    public string InEffect { get; set; } = "";
    public string MinimumDays { get; set; } = "";
    public string MinimumMinutes { get; set; } = "";
    public string Week { get; set; } = "";
    public List<string> Counts { get; set; } = [];
    public List<string> DoesNotCount { get; set; } = [];
    public List<string> Sources { get; set; } = [];
}

public sealed class EncounterRow
{
    /// <summary>The key the page puts in the address to select this encounter. Never shown.</summary>
    public string Ref { get; set; } = "";
    public string Encounter { get; set; } = "";
    public string Date { get; set; } = "";
    public string Weekday { get; set; } = "";

    /// <summary>The first day of the week the encounter falls in, so a link can open that week too.</summary>
    public string? WeekStart { get; set; }
    public string Service { get; set; } = "";
    public string Conclusion { get; set; } = "";
    public string Minutes { get; set; } = "";
    public string CountedByPlan { get; set; } = "";
    public string Tone { get; set; } = "";
    public List<string> Documents { get; set; } = [];
}

// ---------- calculations ----------

public sealed class ParametersView
{
    public string Patient { get; set; } = "";
    public string PatientKey { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Dates { get; set; } = "";
    public string DateRangeSource { get; set; } = "";
    public string Week { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>One contact as a calculation judged it against the plan in effect on its date.</summary>
public sealed class ContactView
{
    public string Ref { get; set; } = "";
    public string Encounter { get; set; } = "";
    public string Date { get; set; } = "";
    public string Weekday { get; set; } = "";
    public string? WeekStart { get; set; }
    public string Service { get; set; } = "";
    public string Status { get; set; } = "";
    public string Tone { get; set; } = "";

    /// <summary>The minutes this contact adds, as the command line shows them: "45", "40 to 50", or "0".</summary>
    public string Minutes { get; set; } = "";

    /// <summary>The arithmetic for a counted contact, or the reason it is not counted.</summary>
    public string Detail { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<DisagreementView> Disagreements { get; set; } = [];
    public List<string> WouldSettle { get; set; } = [];
}

public sealed class WeeklyView
{
    public ParametersView Parameters { get; set; } = new();
    public List<WeekView> Weeks { get; set; } = [];
    public string TotalMinutes { get; set; } = "";
    public string TotalHours { get; set; } = "";
    public string TotalArithmetic { get; set; } = "";
    public List<string> Unsettled { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class WeekView
{
    public string WeekStart { get; set; } = "";
    public string WeekEnd { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Partial { get; set; }
    public string? PartialNote { get; set; }
    public string Days { get; set; } = "";
    public string Minutes { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Arithmetic { get; set; } = "";
    public string Goal { get; set; } = "";
    public List<string> GoalSources { get; set; } = [];
    public string? PlanNote { get; set; }
    public string Result { get; set; } = "";
    public string Tone { get; set; } = "";
    public string DaysResult { get; set; } = "";
    public string MinutesResult { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> TherapyDates { get; set; } = [];
    public List<string> WouldSettle { get; set; } = [];

    /// <summary>Said instead of the three lists when the week holds no appointment or contact at all.</summary>
    public string? ContactsNote { get; set; }
    public List<ContactView> Counted { get; set; } = [];
    public List<ContactView> MayCount { get; set; } = [];
    public List<ContactView> NotCounted { get; set; } = [];
}

public sealed class SessionsView
{
    public ParametersView Parameters { get; set; } = new();
    public List<ServiceCount> ByService { get; set; } = [];
    public string Total { get; set; } = "";
    public string DistinctDays { get; set; } = "";
    public List<string> TherapyDates { get; set; } = [];
    public List<ContactView> Counted { get; set; } = [];
    public List<ContactView> MayCount { get; set; } = [];
    public List<ContactView> NotCounted { get; set; } = [];
    public List<DisagreementView> Disagreements { get; set; } = [];
    public List<string> DescribedMoreThanOnce { get; set; } = [];
    public List<string> NotAppointments { get; set; } = [];
    public List<string> Unlinked { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ServiceCount
{
    public string Service { get; set; } = "";
    public int Sessions { get; set; }
}

// ---------- one encounter ----------

public sealed class EncounterView
{
    public string Ref { get; set; } = "";
    public string Encounter { get; set; } = "";
    public string PatientKey { get; set; } = "";
    public string Date { get; set; } = "";
    public string Weekday { get; set; } = "";
    public string? WeekStart { get; set; }
    public string Service { get; set; } = "";
    public List<string> ServiceLabels { get; set; } = [];
    public string Modality { get; set; } = "";
    public string Conclusion { get; set; } = "";
    public string Minutes { get; set; } = "";
    public string Arithmetic { get; set; } = "";

    /// <summary>Whether the treatment plan counts this contact, and why.</summary>
    public ContactView Judgement { get; set; } = new();
    public IntervalView? SessionInterval { get; set; }
    public List<IntervalView> Removed { get; set; } = [];
    public List<FieldView> Fields { get; set; } = [];
    public List<SetAsideView> SetAside { get; set; } = [];
    public List<DisagreementView> Disagreements { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public List<string> WouldSettle { get; set; } = [];
    public List<StatementView> Statements { get; set; } = [];
    public List<string> Documents { get; set; } = [];
}

public sealed class IntervalView
{
    public string Times { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Sources { get; set; } = [];
}

public sealed class FieldView
{
    public string Field { get; set; } = "";
    public string Decision { get; set; } = "";
    public bool Settled { get; set; }
    public bool Unresolved { get; set; }
    public string Value { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Explanation { get; set; } = "";
    public List<string> Sources { get; set; } = [];

    /// <summary>Every value still standing: one when settled, several when not.</summary>
    public List<CandidateView> Candidates { get; set; } = [];

    /// <summary>What documentation would settle the field, when it is not settled.</summary>
    public List<string> WouldSettle { get; set; } = [];
}

public sealed class CandidateView
{
    public string Value { get; set; } = "";
    public string Tier { get; set; } = "";
    public List<string> Sources { get; set; } = [];
}

public sealed class SetAsideView
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class DisagreementView
{
    /// <summary>The date and encounter, where a list covers several encounters.</summary>
    public string? Encounter { get; set; }

    /// <summary>The rule, in words with its code beside it.</summary>
    public string Rule { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Sources { get; set; } = [];
}

public sealed class StatementView
{
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Record { get; set; } = "";
    public string Signed { get; set; } = "";
    public string Tier { get; set; } = "";
    public string StatedAt { get; set; } = "";
    public string LinkedBy { get; set; } = "";
    public string Quote { get; set; } = "";
}

// ---------- one day, measures, observations ----------

public sealed class DayView
{
    public ParametersView Parameters { get; set; } = new();
    public string Date { get; set; } = "";
    public string TherapyContacts { get; set; } = "";
    public string Minutes { get; set; } = "";
    public string Arithmetic { get; set; } = "";
    public List<ContactView> Contacts { get; set; } = [];
    public List<string> OtherRecords { get; set; } = [];
    public List<ObservationView> Observations { get; set; } = [];
}

public sealed class MeasuresView
{
    public ParametersView Parameters { get; set; } = new();
    public int DistinctAssessments { get; set; }
    public List<MeasureRow> Assessments { get; set; } = [];
    public List<string> Limits { get; set; } = [];
}

public sealed class MeasureRow
{
    public string Instrument { get; set; } = "";
    public string Completed { get; set; } = "";
    public string Total { get; set; } = "";
    public string ChangeFromPrevious { get; set; } = "";
    public string ChangeFromFirst { get; set; } = "";
    public string ItemScores { get; set; } = "";
    public string? FormId { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<string> CopiesAndMentions { get; set; } = [];
    public List<DisagreementView> Disagreements { get; set; } = [];
}

public sealed class ObservationsView
{
    public ParametersView Parameters { get; set; } = new();
    public string? Category { get; set; }
    public List<ObservationView> Observations { get; set; } = [];
}

public sealed class ObservationView
{
    public string Date { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Encounter { get; set; }
    public string Record { get; set; } = "";
}

// ---------- the collection ----------

public sealed class ConsecutiveView
{
    public int PatientsExamined { get; set; }

    /// <summary>Says how many patients' pairs of weeks are left out when there are more than the list shows.</summary>
    public string? Note { get; set; }
    public string Period { get; set; } = "";
    public List<ConsecutiveGroup> Groups { get; set; } = [];
    public List<ConsecutivePatient> Details { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

public sealed class ConsecutiveGroup
{
    public string Label { get; set; } = "";
    public List<PatientLink> Patients { get; set; } = [];

    /// <summary>Patients the result names only as text, such as the ones not included.</summary>
    public List<string> Named { get; set; } = [];
}

public sealed class PatientLink
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class ConsecutivePatient
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Status { get; set; } = "";
    public List<PairView> Pairs { get; set; } = [];
}

public sealed class PairView
{
    public string Weeks { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FirstWeek { get; set; } = "";
    public string FirstResult { get; set; } = "";
    public string FirstTone { get; set; } = "";
    public string FirstDetail { get; set; } = "";
    public string SecondWeek { get; set; } = "";
    public string SecondResult { get; set; } = "";
    public string SecondTone { get; set; } = "";
    public string SecondDetail { get; set; } = "";

    /// <summary>The sources of the plan that set each week's goal, and of each week's contacts, as the command line lists them.</summary>
    public List<string> FirstGoalSources { get; set; } = [];
    public List<string> FirstContactSources { get; set; } = [];
    public List<string> SecondGoalSources { get; set; } = [];
    public List<string> SecondContactSources { get; set; } = [];
    public List<string> WouldSettle { get; set; } = [];
}

public sealed class SummaryView
{
    public string Period { get; set; } = "";
    public string DateRangeSource { get; set; } = "";
    public List<string> Notes { get; set; } = [];

    /// <summary>One column for each service type any patient attended, as the command line lays out the table.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>Says how many patients' rows are left out when there are more than the list shows.</summary>
    public string? Note { get; set; }
    public List<SummaryRow> Rows { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class SummaryRow
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Period { get; set; } = "";
    public List<int> Sessions { get; set; } = [];
    public string Total { get; set; } = "";
    public string DistinctDays { get; set; } = "";
    public string Minutes { get; set; } = "";
    public int WeeksExamined { get; set; }
    public int WeeksMet { get; set; }
    public int WeeksNotMet { get; set; }
    public int WeeksUndetermined { get; set; }
    public int WeeksWithNoGoal { get; set; }
    public int PartialWeeks { get; set; }
    public List<string> Warnings { get; set; } = [];
}

// ---------- source lines ----------

public sealed class SourceView
{
    public string Document { get; set; } = "";
    public string File { get; set; } = "";
    public int LineCount { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public string Heading { get; set; } = "";
    public List<SourceLine> Lines { get; set; } = [];
    public string StatementsHeading { get; set; } = "";
    public List<SourceStatement> Statements { get; set; } = [];
    public string? Note { get; set; }

    /// <summary>
    /// When several documents carry the identifier and the source cannot be tied to one of them,
    /// each of them, with its file name. The lines above are then left empty.
    /// </summary>
    public List<SourceView> Candidates { get; set; } = [];
}

public sealed class SourceLine
{
    public int Number { get; set; }
    public string Text { get; set; } = "";
    public bool Cited { get; set; }
}

public sealed class SourceStatement
{
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Encounter { get; set; }
    public string Record { get; set; } = "";
    public string Signed { get; set; } = "";
    public List<RecordedValue> Recorded { get; set; } = [];
    public string Quote { get; set; } = "";
}

public sealed class RecordedValue
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

// ---------- stage 2: the model, jobs, answers, documents and the gap log ----------

public sealed class ModelStatusView
{
    public bool Ready { get; set; }

    /// <summary>What must be configured before a model can be called, when something is missing.</summary>
    public string? Missing { get; set; }
}

/// <summary>Text written by a model or a document, divided into parts the page builds as elements. Nothing in it is markup.</summary>
public sealed class Block
{
    /// <summary>heading, paragraph, list, table or code. Used by the page to choose an element, never shown.</summary>
    public string Kind { get; set; } = "";
    public int? Level { get; set; }
    public List<Span>? Spans { get; set; }
    public bool? Ordered { get; set; }
    public List<ListEntry>? Items { get; set; }
    public List<List<Span>>? Headers { get; set; }
    public List<List<List<Span>>>? Rows { get; set; }
    public string? Text { get; set; }
}

public sealed class Span
{
    public string Text { get; set; } = "";
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Code { get; set; }
}

public sealed class ListEntry
{
    public List<Span> Spans { get; set; } = [];
    public bool? Ordered { get; set; }
    public List<ListEntry>? Children { get; set; }
}

public sealed class CheckRow
{
    public string Check { get; set; } = "";
    public string Result { get; set; } = "";
}

public sealed class CalculationView
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Inputs { get; set; } = "";
    public string Ran { get; set; } = "";
    public bool Failed { get; set; }

    /// <summary>The tables and text the command line prints for this calculation, divided into parts.</summary>
    public List<Block> Blocks { get; set; } = [];
}

public sealed class AnswerView
{
    public long Id { get; set; }
    public string Question { get; set; } = "";

    /// <summary>The default patient's key, when the question was asked with one.</summary>
    public string? PatientKey { get; set; }
    public string? Patient { get; set; }
    public string AskedAt { get; set; } = "";
    public bool FromSaved { get; set; }
    public string How { get; set; } = "";

    /// <summary>Said above the written answer when the checks found a source or a number the results do not hold.</summary>
    public string? Warning { get; set; }
    public string NarrativeBy { get; set; } = "";
    public List<Block> Narrative { get; set; } = [];
    public string ChecksBy { get; set; } = "";
    public bool? Passed { get; set; }
    public List<CheckRow> Checks { get; set; } = [];
    public string CalculationsBy { get; set; } = "";
    public List<CalculationView> Calculations { get; set; } = [];
    public List<RecordedValue> Produced { get; set; } = [];

    /// <summary>Said when the saved answer could not be divided into its parts and is shown as saved.</summary>
    public string? Note { get; set; }

    /// <summary>The patient each document identifier belongs to, so each source can open the trace for that patient.</summary>
    public Dictionary<string, string> SourcePatients { get; set; } = [];
}

public sealed class AnswerListView
{
    public int Total { get; set; }
    public List<AnswerRow> Answers { get; set; } = [];
    public string? Note { get; set; }
    public string? Empty { get; set; }
}

public sealed class AnswerRow
{
    public long Id { get; set; }
    public string Question { get; set; } = "";
    public string? Patient { get; set; }
    public string AskedAt { get; set; } = "";
    public bool FromSaved { get; set; }
    public string How { get; set; } = "";
}

public sealed class JobView
{
    public string Id { get; set; } = "";

    /// <summary>ask or ingest. Used by the page to choose a screen, never shown.</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Running { get; set; }
    public bool Failed { get; set; }
    public string State { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Elapsed { get; set; } = "";
    public int ElapsedSeconds { get; set; }
    public List<JobFileView> Files { get; set; } = [];
    public List<JobStepView> Calculations { get; set; } = [];
    public string? Error { get; set; }

    /// <summary>The saved answer a finished question can be opened as.</summary>
    public long? AnswerId { get; set; }
    public bool? FromSaved { get; set; }
    public List<string> Summary { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class JobFileView
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Tone { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>The document the file was registered as, so the page can link to it once it is known.</summary>
    public string? Document { get; set; }
}

public sealed class JobStepView
{
    public string Title { get; set; } = "";
    public string Inputs { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class DocumentListView
{
    public int Total { get; set; }
    public List<DocumentRow> Documents { get; set; } = [];
    public string? Empty { get; set; }
}

public sealed class DocumentRow
{
    /// <summary>The document's hash, which the page puts in the address. Never shown.</summary>
    public string Id { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? PatientKey { get; set; }
    public string Patient { get; set; } = "";
    public int Kept { get; set; }
    public int Rejected { get; set; }
    public string Status { get; set; } = "";
    public string StatusDetail { get; set; } = "";
    public string Tone { get; set; } = "";
}

public sealed class DocumentView
{
    public string Id { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string FileName { get; set; } = "";
    public string File { get; set; } = "";
    public List<string> AlsoSeenAs { get; set; } = [];
    public string? PatientKey { get; set; }
    public string Patient { get; set; } = "";
    public int Kept { get; set; }
    public int Rejected { get; set; }
    public string Status { get; set; } = "";
    public string StatusDetail { get; set; } = "";
    public string Tone { get; set; } = "";
    public int LineCount { get; set; }
    public List<DocumentLine> Lines { get; set; } = [];

    /// <summary>Every assertion kept from the document, as step 4 of the trace lists them.</summary>
    public List<SourceStatement> Statements { get; set; } = [];
}

public sealed class DocumentLine
{
    public int Number { get; set; }
    public string Text { get; set; } = "";

    /// <summary>The positions in <see cref="DocumentView.Statements"/> of the assertions extracted from this line.</summary>
    public List<int> Statements { get; set; } = [];
}

public sealed class GapLogView
{
    public int Passages { get; set; }
    public string? Empty { get; set; }
    public List<GapGroupView> Groups { get; set; } = [];
}

public sealed class GapGroupView
{
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public List<string> Questions { get; set; } = [];
    public List<GapPassageView> Passages { get; set; } = [];
}

public sealed class GapPassageView
{
    public string Source { get; set; } = "";
    public string? PatientKey { get; set; }
    public string SearchTerms { get; set; } = "";
    public string Passage { get; set; } = "";
}

public sealed class AskRequest
{
    public string? Question { get; set; }
    public string? Patient { get; set; }
}

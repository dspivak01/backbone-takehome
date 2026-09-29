namespace ClinicalAbstraction.Domain;

/// <summary>One value that one or more assertions state for a field.</summary>
public sealed class Candidate
{
    public string Value { get; set; } = "";
    public int Tier { get; set; }
    public List<string> Citations { get; set; } = [];
    public List<long> AssertionIds { get; set; } = [];
}

/// <summary>An assertion that was set aside, and the rule that set it aside.</summary>
public sealed class Overruled
{
    public string Value { get; set; } = "";
    public string Citation { get; set; } = "";
    public long AssertionId { get; set; }
    public string Rule { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>How one field of an event was decided.</summary>
public sealed class FieldDecision
{
    public string Field { get; set; } = "";

    /// <summary>resolved, resolved_by_corroboration, unresolved, or missing.</summary>
    public string Status { get; set; } = "missing";

    /// <summary>The decided value. Empty when the field is unresolved or missing.</summary>
    public string? Value { get; set; }

    public int? Tier { get; set; }
    public string Rule { get; set; } = "";
    public string Explanation { get; set; } = "";

    /// <summary>The values still standing: one when resolved, several when unresolved.</summary>
    public List<Candidate> Candidates { get; set; } = [];

    public List<Overruled> Overruled { get; set; } = [];

    /// <summary>
    /// What documentation would settle this field, when it is unresolved. Each item is also in the
    /// event's own list. Null when the field needs nothing, and in events saved before fields
    /// recorded this; those show the event's list for the encounter as a whole.
    /// </summary>
    public List<string>? Needs { get; set; }

    public bool IsSettled => Status is "resolved" or "resolved_by_corroboration";
}

public sealed class TimeSpanMinutes
{
    public int Start { get; set; }
    public int End { get; set; }
    public string Reason { get; set; } = "";
    public List<string> Citations { get; set; } = [];

    public override string ToString() => $"{Parse.Clock(Start)}-{Parse.Clock(End)}";
}

public sealed class Discrepancy
{
    public string Rule { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Citations { get; set; } = [];
}

public sealed class AssertionLink
{
    public long AssertionId { get; set; }
    public string Citation { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Quote { get; set; } = "";
    public string RecordType { get; set; } = "";
    public string Signed { get; set; } = "";
    public int Tier { get; set; }
    public bool IsCopy { get; set; }
    public string? StatementTime { get; set; }

    /// <summary>encounter_id, or date_and_category when the assertion carried no identifier.</summary>
    public string LinkMethod { get; set; } = "encounter_id";
}

/// <summary>What the system concludes about one scheduled service contact.</summary>
public sealed class EncounterEvent
{
    public string EventKey { get; set; } = "";
    public string PatientKey { get; set; } = "";
    public string? EncounterId { get; set; }
    public DateOnly? ServiceDate { get; set; }

    public FieldDecision Category { get; set; } = new() { Field = "service category" };
    public List<string> ServiceLabels { get; set; } = [];
    public string? Modality { get; set; }
    public FieldDecision Disposition { get; set; } = new() { Field = "disposition" };
    public FieldDecision PatientPresent { get; set; } = new() { Field = "patient present" };
    public FieldDecision Start { get; set; } = new() { Field = "presence start" };
    public FieldDecision End { get; set; } = new() { Field = "presence end" };

    public TimeSpanMinutes? SessionInterval { get; set; }
    public List<TimeSpanMinutes> Excluded { get; set; } = [];

    /// <summary>True when the patient's times come from an attendance or desk record, not a clinician's note.</summary>
    public bool PresenceFromRoster { get; set; }

    /// <summary>session, not_session, or uncertain.</summary>
    public string Occurrence { get; set; } = "uncertain";
    public string? NotSessionReason { get; set; }

    /// <summary>Minutes the patient was present and in contact. Not yet judged against any plan.</summary>
    public int MinutesMin { get; set; }
    public int MinutesMax { get; set; }
    public string Arithmetic { get; set; } = "";

    public List<string> Flags { get; set; } = [];
    public List<Discrepancy> Discrepancies { get; set; } = [];

    /// <summary>What documentation would settle each unresolved item.</summary>
    public List<string> Needs { get; set; } = [];

    public List<AssertionLink> Assertions { get; set; } = [];
    public List<string> SourceDocuments { get; set; } = [];

    public string CategoryValue => Category.Value ?? "other";
    public bool MinutesSettled => MinutesMin == MinutesMax;
}

public sealed class MeasurementEvent
{
    public string PatientKey { get; set; } = "";
    public string Instrument { get; set; } = "";
    public DateOnly? CompletedOn { get; set; }
    public string? CompletedAt { get; set; }
    public int? TotalScore { get; set; }
    public string? FormId { get; set; }
    public List<ItemScore> ItemScores { get; set; } = [];
    public List<string> OriginalCitations { get; set; } = [];

    /// <summary>Copies, imports and restatements of this result. They are not separate assessments.</summary>
    public List<string> RepeatCitations { get; set; } = [];
    public List<long> AssertionIds { get; set; } = [];
    public List<Discrepancy> Discrepancies { get; set; } = [];
}

public sealed class PlanVersion
{
    public string PatientKey { get; set; } = "";
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public int? MinTherapyDaysPerWeek { get; set; }
    public int? MinMinutesPerWeek { get; set; }
    public string WeekDefinition { get; set; } = "monday_sunday";
    public List<string> CountedCategories { get; set; } = [];
    public List<string> ExcludedCategories { get; set; } = [];
    public List<string> Citations { get; set; } = [];
    public List<long> AssertionIds { get; set; } = [];
    public string? SignedAt { get; set; }
}

public sealed class Episode
{
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    public List<string> Citations { get; set; } = [];
}

/// <summary>An assertion that could not be placed, kept out of the counts and reported.</summary>
public sealed class UnlinkedAssertion
{
    public long AssertionId { get; set; }
    public string Citation { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>A document record that is not a session, listed so exclusions can be explained.</summary>
public sealed class NonEncounterRecord
{
    public string Citation { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateOnly? Date { get; set; }
    public string Description { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>Everything the system concludes about one patient. This is the saved abstraction.</summary>
public sealed class PatientAbstraction
{
    public string PatientKey { get; set; } = "";
    public string? Mrn { get; set; }
    public string? Name { get; set; }
    public string? DateOfBirth { get; set; }
    public string Version { get; set; } = "";
    public Episode Episode { get; set; } = new();
    public List<PlanVersion> Plans { get; set; } = [];
    public List<EncounterEvent> Encounters { get; set; } = [];
    public List<MeasurementEvent> Measurements { get; set; } = [];
    public List<NonEncounterRecord> NonEncounterRecords { get; set; } = [];
    public List<UnlinkedAssertion> Unlinked { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int DocumentCount { get; set; }
    public int AssertionCount { get; set; }
}

namespace ClinicalAbstraction.Domain;

/// <summary>
/// The fixed value lists shared by the extractor's tool schema, validation, and the rules.
/// Code only ever compares these values, never the labels printed in a document.
/// </summary>
public static class Vocabulary
{
    public const string ExtractorVersion = "v1";

    public static readonly string[] Kinds =
    [
        "encounter", "attendance", "presence", "excluded_interval", "stated_duration", "correction",
        "no_service_contact", "plan_requirement", "measure", "observation", "authorization", "charge", "other",
    ];

    /// <summary>Kinds that describe a service contact and so need to be linked to an encounter.</summary>
    public static readonly HashSet<string> EncounterKinds =
    [
        "encounter", "attendance", "presence", "excluded_interval", "stated_duration", "correction", "charge",
    ];

    public static readonly string[] ServiceCategories =
    [
        "individual_therapy", "group_therapy", "family_therapy", "collateral_contact",
        "medication_management", "care_coordination", "administrative", "other",
    ];

    public static readonly string[] RecordTypes =
    [
        "clinical_note", "attendance_record", "correction", "appointment_export", "desk_log",
        "scheduling_entry", "platform_log", "draft_note", "billing_charge", "treatment_plan",
        "authorization_letter", "measure_record", "import_receipt", "administrative_notice", "other",
    ];

    public static readonly string[] SignedValues = ["yes", "no", "extract_of_signed"];

    public static readonly string[] Dispositions =
    [
        "completed", "attended", "attended_in_full", "attended_in_part",
        "no_show", "clinic_cancelled", "patient_cancelled", "scheduled", "not_stated",
    ];

    public static readonly string[] PatientPresentValues = ["yes", "no", "part", "not_stated"];

    public static readonly string[] Modalities = ["in_person", "video", "telephone", "not_stated"];

    public static readonly string[] IntervalReasons = ["break", "connection_lost", "other"];

    public static readonly string[] DurationMeasures = ["patient_present", "session", "visit"];

    public static readonly string[] CorrectedFields = ["start", "end", "arrival", "departure", "status", "break"];

    public static readonly string[] WeekDefinitions = ["monday_sunday", "sunday_saturday", "not_stated"];

    public static readonly string[] ObservationCategories =
    [
        "patient_reported_symptoms", "symptoms", "functioning", "safety",
        "reason_for_contact", "intervention", "clinician_assessment", "recommendation",
    ];

    /// <summary>Dispositions meaning the appointment took place. They do not say the patient was there.</summary>
    public static readonly HashSet<string> TookPlace = ["completed", "attended", "attended_in_full", "attended_in_part"];

    /// <summary>Dispositions meaning the appointment did not take place.</summary>
    public static readonly HashSet<string> DidNotTakePlace = ["no_show", "clinic_cancelled", "patient_cancelled"];

    public static string DescribeCategory(string? category) => category switch
    {
        "individual_therapy" => "Individual therapy",
        "group_therapy" => "Group therapy",
        "family_therapy" => "Family therapy",
        "collateral_contact" => "Collateral contact",
        "medication_management" => "Medication management",
        "care_coordination" => "Care coordination",
        "administrative" => "Administrative",
        "other" => "Other",
        null => "Not stated",
        _ => category,
    };

    /// <summary>
    /// One field's value as words inside a sentence, for example group therapy, or "took place"
    /// in quotation marks. Sentences are built with these words, never with the codes.
    /// </summary>
    public static string DescribeValue(string field, string? value) => (field, value) switch
    {
        (_, null) => "not stated",
        ("service category", _) => DescribeCategory(value).ToLowerInvariant(),
        ("disposition", "took_place") => "\"took place\"",
        ("disposition", _) => $"\"{DescribeDisposition(value)}\"",
        ("patient present", "absent") => "\"not present\"",
        ("patient present", "present") => "\"present\"",
        _ => value,
    };

    /// <summary>What sort of statement an assertion is, in lower-case words for use inside a sentence.</summary>
    public static string DescribeKind(string kind) => kind switch
    {
        "encounter" => "encounter description",
        "excluded_interval" => "break or lost connection",
        "stated_duration" => "stated duration",
        "no_service_contact" => "record of no service contact",
        "plan_requirement" => "treatment plan requirement",
        "measure" => "symptom assessment",
        _ => kind.Replace('_', ' '),
    };

    public static string DescribeDisposition(string? disposition) => disposition switch
    {
        "completed" => "completed",
        "attended" => "attended",
        "attended_in_full" => "attended in full",
        "attended_in_part" => "attended in part",
        "no_show" => "no show",
        "clinic_cancelled" => "cancelled by the clinic",
        "patient_cancelled" => "cancelled by the patient",
        "scheduled" => "scheduled",
        _ => "not stated",
    };
}

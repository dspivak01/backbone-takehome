using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Extraction;

/// <summary>
/// The shape the model must fill in for each document. It is built from the fixed value lists in
/// <see cref="Vocabulary"/>, so the schema and the rules can never drift apart.
/// </summary>
public static class ExtractionSchema
{
    public const string ToolName = "record_assertions";

    public const string ToolDescription =
        "Record every assertion found in the document, each with its line range, an exact quote, and a description of its source.";

    public const string HeaderToolName = "record_document_header";

    public const string HeaderToolDescription =
        "Record only what the document is and which patient it is about. Used when that information is needed on its own.";

    public static readonly string[] HeaderRequired = ["document_structure", "printed_document_id", "patient"];

    public static Dictionary<string, JsonElement> HeaderProperties() =>
        Properties().Where(p => p.Key != "assertions").ToDictionary(p => p.Key, p => p.Value);

    public static Dictionary<string, JsonElement> Properties()
    {
        var properties = new JsonObject
        {
            ["document_structure"] = Text("One or two sentences describing what this file contains and how it is laid out, including any separate sections or attached copies. Write this first."),
            ["printed_document_id"] = Nullable("The document identifier printed in the file, if any."),
            ["patient"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The patient this document is about.",
                ["required"] = Array("name", "date_of_birth", "mrn"),
                ["properties"] = new JsonObject
                {
                    ["name"] = Nullable("Patient name as printed."),
                    ["date_of_birth"] = Nullable("Date of birth as yyyy-MM-dd."),
                    ["mrn"] = Nullable("Medical record number as printed."),
                },
            },
            ["assertions"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "A JSON array of assertion objects. Do not write it as a string.",
                ["items"] = AssertionItem(),
            },
        };
        return properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }

    public static readonly string[] Required = ["document_structure", "printed_document_id", "patient", "assertions"];

    private static JsonObject AssertionItem() => new()
    {
        ["type"] = "object",
        ["required"] = Array("kind", "line_start", "line_end", "quote", "record_type", "signed"),
        ["properties"] = new JsonObject
        {
            // Location and identity
            ["kind"] = Choice(Vocabulary.Kinds, "What sort of statement this is."),
            ["line_start"] = Integer("First line of the statement."),
            ["line_end"] = Integer("Last line of the statement."),
            ["quote"] = Text("One contiguous excerpt copied exactly from those lines, up to about 200 characters."),
            ["encounter_id"] = Nullable("The encounter identifier the statement is about, as printed."),
            ["other_ids"] = TextList("Other identifiers printed with the statement: appointment, call, form, charge or authorization numbers."),
            ["service_date"] = Nullable("The date the service happened or was scheduled, as yyyy-MM-dd."),
            ["date_as_written"] = Nullable("That date exactly as the document prints it."),
            ["patient_mrn"] = Nullable("Only when this statement is about a different patient from the document's patient."),

            // Source of the statement
            ["record_type"] = Choice(Vocabulary.RecordTypes, "The type of record this statement comes from. For a copy, the type of the record that was copied."),
            ["signed"] = Choice(Vocabulary.SignedValues, "yes: the statement carries a clinician or staff signature or attestation. extract_of_signed: an extract or copy that says it was prepared from a signed record. no: anything else, including exports, logs, drafts and charges."),
            ["signer"] = Nullable("Who signed or entered the statement."),
            ["signed_at"] = Nullable("When it was signed, as yyyy-MM-ddTHH:mm. For a copy, leave empty and use original_signed_at."),
            ["recorded_at"] = Nullable("For unsigned statements: when it was entered, created, exported or posted, as yyyy-MM-ddTHH:mm."),
            ["is_copy"] = new JsonObject { ["type"] = "boolean", ["description"] = "True when the statement is a retransmission, import or copy of an earlier record." },
            ["original_signed_at"] = Nullable("For a copy: when the original record was signed, as yyyy-MM-ddTHH:mm."),
            ["received_at"] = Nullable("For a copy or import: when it was received or filed, as yyyy-MM-ddTHH:mm."),

            // encounter
            ["service_category"] = NullableChoice(Vocabulary.ServiceCategories, "The kind of service, chosen from the fixed list."),
            ["service_label"] = Nullable("The service name exactly as printed."),
            ["modality"] = NullableChoice(Vocabulary.Modalities, "How the contact took place."),
            ["clinicians"] = TextList("Clinicians or staff who took part."),
            ["scheduled_start"] = Nullable("Booked start time, HH:mm."),
            ["scheduled_end"] = Nullable("Booked end time, HH:mm."),
            ["session_start"] = Nullable("Actual start of the session as a whole, HH:mm, when stated separately from the patient's own presence."),
            ["session_end"] = Nullable("Actual end of the session as a whole, HH:mm."),

            // attendance
            ["disposition"] = NullableChoice(Vocabulary.Dispositions, "What happened to the appointment."),
            ["patient_present"] = NullableChoice(Vocabulary.PatientPresentValues, "Whether the patient personally took part."),

            // presence and excluded intervals
            ["arrival"] = Nullable("Patient arrival time, HH:mm."),
            ["departure"] = Nullable("Patient departure time, HH:mm."),
            ["intervals"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "For presence: the intervals when the patient was in contact. For excluded_interval: the intervals to remove.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["start"] = Text("HH:mm"), ["end"] = Text("HH:mm") },
                    ["required"] = Array("start", "end"),
                },
            },
            ["interval_reason"] = NullableChoice(Vocabulary.IntervalReasons, "Why an excluded interval is excluded."),

            // stated_duration
            ["minutes"] = NullableInteger("A duration in minutes exactly as the document states it."),
            ["duration_measures"] = NullableChoice(Vocabulary.DurationMeasures, "What the stated duration measures."),

            // correction
            ["corrected_field"] = NullableChoice(Vocabulary.CorrectedFields, "Which field the correction changes."),
            ["old_value"] = Nullable("The value being replaced, as written."),
            ["new_value"] = Nullable("The corrected value, as written."),

            // no_service_contact
            ["what_did_not_occur"] = Nullable("What the document says did not take place."),

            // plan_requirement
            ["min_therapy_days_per_week"] = NullableInteger("Minimum therapy days per week."),
            ["min_minutes_per_week"] = NullableInteger("Minimum minutes per week."),
            ["week_definition"] = NullableChoice(Vocabulary.WeekDefinitions, "How the plan defines a week."),
            ["counted_categories"] = ChoiceList(Vocabulary.ServiceCategories, "Service categories the plan says count toward the goal."),
            ["excluded_categories"] = ChoiceList(Vocabulary.ServiceCategories, "Service categories the plan says do not count."),
            ["effective_from"] = Nullable("First day the requirement applies, yyyy-MM-dd."),
            ["effective_to"] = Nullable("Last day the requirement applies, yyyy-MM-dd."),
            ["episode_start"] = Nullable("First day of the treatment episode, yyyy-MM-dd."),
            ["episode_end"] = Nullable("Last day of the treatment episode, yyyy-MM-dd."),

            // measure
            ["instrument"] = Nullable("Name of the instrument, such as PHQ-9."),
            ["total_score"] = NullableInteger("Total score."),
            ["completed_at"] = Nullable("When the patient completed it, yyyy-MM-dd or yyyy-MM-ddTHH:mm."),
            ["form_id"] = Nullable("Form identifier, if printed."),
            ["item_scores"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["item"] = Text("Item name or number."), ["score"] = Integer("Score.") },
                },
            },
            ["is_restatement"] = new JsonObject { ["type"] = "boolean", ["description"] = "True when the document only mentions a score that was recorded on another occasion." },

            // observation
            ["observation_category"] = NullableChoice(Vocabulary.ObservationCategories, "What the observation is about."),
            ["summary"] = Nullable("A neutral paraphrase of the statement in at most 30 words, adding nothing the text does not say."),

            // authorization and charge
            ["authorization_number"] = Nullable("Authorization number."),
            ["quantity"] = NullableInteger("Authorized or charged quantity."),
            ["unit"] = Nullable("What one unit of the quantity is."),
            ["period_start"] = Nullable("yyyy-MM-dd"),
            ["period_end"] = Nullable("yyyy-MM-dd"),
            ["charge_id"] = Nullable("Charge identifier."),
            ["description"] = Nullable("Charge or item description as printed."),
            ["posted_at"] = Nullable("When the charge was posted, yyyy-MM-ddTHH:mm."),

            // other
            ["other_category"] = Nullable("A short category name you propose for a statement that fits no other kind."),
        },
    };

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Nullable(string description) => new() { ["type"] = Array("string", "null"), ["description"] = description };

    private static JsonObject NullableInteger(string description) => new() { ["type"] = Array("integer", "null"), ["description"] = description };

    private static JsonObject Choice(string[] values, string description) =>
        new() { ["type"] = "string", ["enum"] = Array(values), ["description"] = description };

    private static JsonObject NullableChoice(string[] values, string description)
    {
        var options = Array(values);
        options.Add(null);
        return new JsonObject { ["type"] = Array("string", "null"), ["enum"] = options, ["description"] = description };
    }

    private static JsonObject TextList(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private static JsonObject ChoiceList(string[] values, string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Array(values) }, ["description"] = description };

    private static JsonArray Array(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}

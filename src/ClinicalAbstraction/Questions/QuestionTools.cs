using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Questions;

/// <summary>
/// The calculations offered to the model as tools. The model chooses which to run and with what
/// parameters. Code runs them. The model never sees a document unless a calculation returns a quote.
/// </summary>
public sealed class QuestionTools(Calculations calculations, string question)
{
    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static readonly JsonObject Patient = Text("The patient_key returned by find_patient.");
    private static readonly string DateNote = "yyyy-MM-dd. Leave out to use the patient's episode. When given, the date itself is included.";

    private static Tool Define(string name, string description, string[] required, params (string Name, JsonObject Schema)[] properties) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new()
        {
            Properties = properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Schema)),
            Required = required,
        },
    };

    public static List<ToolUnion> Definitions() =>
    [
        Define("find_patient",
            "Find a patient by name or medical record number. Call this first. Returns the patient_key, episode dates and what the record holds. If a name or number returns no match or several, say so in the answer and do not guess. With an empty query it lists every patient in the collection, and it is never a reason to choose one of them.",
            [], ("query", Text("A name, part of a name, or an MRN. Leave empty to list every patient in the collection."))),

        Define("session_counts",
            "Therapy sessions attended in a date range: count by service type, total, distinct days, every counted session with its minutes and sources, every appointment that was not counted with the reason, contacts described by more than one document, and records that are not appointments.",
            ["patient_key"], ("patient_key", (JsonObject)Patient.DeepClone()), ("from", Text(DateNote)), ("to", Text(DateNote))),

        Define("weekly_minutes_and_goals",
            "For each week: therapy days, patient therapy minutes and hours with the arithmetic, the treatment-plan goal in effect, and whether the goal was met, not met, or cannot be determined. Also gives the overall total and what the record does not settle.",
            ["patient_key"], ("patient_key", (JsonObject)Patient.DeepClone()), ("from", Text(DateNote)), ("to", Text(DateNote))),

        Define("consecutive_weeks_below_goal",
            "Which patients had two consecutive weeks below the treatment-plan goal, and which patients' inclusion depends on unresolved documentation. Leave out patient_key to examine every patient in the collection.",
            [], ("patient_key", Text("Optional. One patient's key.")), ("from", Text(DateNote)), ("to", Text(DateNote))),

        Define("plan_change_comparison",
            "Types and amounts of care delivered under each version of the treatment plan, before and after any plan change. Says so when the record contains no plan change.",
            ["patient_key"], ("patient_key", (JsonObject)Patient.DeepClone())),

        Define("day_reconstruction",
            "Everything recorded for one date: each contact, how every field was decided, which statements were set aside and by which rule, every disagreement between documents, and the observations recorded that day. Leave out patient_key when the question names no patient: the result then covers every patient who has a contact on that date, grouped by patient, and says which patients have none.",
            ["date"], ("patient_key", Text("Optional. The patient_key returned by find_patient. Leave out to cover every patient who has a contact on the date.")), ("date", Text("yyyy-MM-dd"))),

        Define("patients_with_contacts",
            "Which patients have an appointment or contact recorded in a period, with how many, and how many patients the collection holds. Use it to find who a question applies to when the question names no patient and gives a date or a period. For one date, give the same date as from and to.",
            [], ("from", Text("yyyy-MM-dd. The date itself is included. Leave out for no earliest date.")), ("to", Text("yyyy-MM-dd. The date itself is included. Leave out for no latest date."))),

        Define("collection_summary",
            "One row for each patient who fits: sessions attended by service type, total sessions, distinct therapy days, therapy minutes, and how many weeks met the goal, did not meet it, or cannot be determined. Use it when a question about sessions, days, minutes or goals names no patient. With dates, it covers every patient who has a contact in the period. With no dates, it covers every patient in the collection, each over their own episode.",
            [], ("from", Text("yyyy-MM-dd. The date itself is included. Leave out to use each patient's own episode.")), ("to", Text("yyyy-MM-dd. The date itself is included. Leave out to use each patient's own episode."))),

        Define("symptom_measures",
            "Symptom assessments in date order, with the change between them, copies and later mentions listed separately, and the limits on what the results can support.",
            ["patient_key"], ("patient_key", (JsonObject)Patient.DeepClone()), ("instrument", Text("Optional instrument name, such as PHQ-9."))),

        Define("observations",
            "Quoted statements from the record about symptoms, functioning, safety, reasons for a contact, interventions, clinician assessments and recommendations, in date order.",
            ["patient_key"], ("patient_key", (JsonObject)Patient.DeepClone()), ("from", Text(DateNote)), ("to", Text(DateNote)),
            ("category", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Vocabulary.ObservationCategories.Select(c => (JsonNode)c).ToArray()),
                ["description"] = "Optional. Leave out for all categories.",
            })),

        Define("search_passages",
            "Keyword search over one patient's documents. Use only when the other tools do not hold what the question asks for. Results are passages of text, not part of the abstraction: never count or total from them, and say in the answer that they came from text search.",
            ["patient_key", "terms"], ("patient_key", (JsonObject)Patient.DeepClone()),
            ("terms", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Words or short phrases to look for." }),
            ("suggested_kind", Text("A short name for the kind of fact you were looking for, such as attendance_reason."))),
    ];

    /// <summary>Runs one tool call and returns the result object. Errors are returned to the model as text.</summary>
    public object Run(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        string? Get(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        string Need(string key) => Get(key) ?? throw new ArgumentException($"'{key}' is required.");

        return name switch
        {
            "find_patient" => calculations.FindPatient(Get("query") ?? ""),
            "session_counts" => calculations.SessionCounts(Need("patient_key"), Get("from"), Get("to")),
            "weekly_minutes_and_goals" => calculations.Weekly(Need("patient_key"), Get("from"), Get("to")),
            "consecutive_weeks_below_goal" => calculations.ConsecutiveWeeks(Get("patient_key"), Get("from"), Get("to")),
            "plan_change_comparison" => calculations.PlanChange(Need("patient_key")),
            "day_reconstruction" => Get("patient_key") is { } patient ? calculations.Day(patient, Need("date")) : calculations.DayAcrossPatients(Need("date")),
            "patients_with_contacts" => calculations.PatientsInPeriod(Get("from"), Get("to")),
            "collection_summary" => calculations.CollectionSummary(Get("from"), Get("to")),
            "symptom_measures" => calculations.Measures(Need("patient_key"), Get("instrument")),
            "observations" => calculations.Observations(Need("patient_key"), Get("from"), Get("to"), Get("category")),
            "search_passages" => calculations.Search(
                Need("patient_key"),
                input.TryGetValue("terms", out var terms) && terms.ValueKind == JsonValueKind.Array
                    ? terms.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                    : [],
                question, Get("suggested_kind")),
            _ => throw new ArgumentException($"Unknown tool '{name}'."),
        };
    }
}

using System.Text.RegularExpressions;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Web;

/// <summary>
/// Turns the values the code works with, such as "not_addressed_by_plan" or "took_place", into
/// words a reviewer with no knowledge of the code can read. The page shows only what this class
/// produces, so no internal name reaches the screen and the page never has to reword anything.
/// </summary>
public static partial class Wording
{
    // ---------- results and statuses ----------

    /// <summary>A week's result, always as words, because colour alone must never carry it.</summary>
    public static string Result(string result) => result switch
    {
        WeeklyCalculator.Met => "Met",
        WeeklyCalculator.NotMet => "Not met",
        WeeklyCalculator.Undetermined => "Cannot be determined",
        _ => "No goal in effect",
    };

    /// <summary>The colour family the page gives a result. It is used for styling only and never shown.</summary>
    public static string Tone(string result) => result switch
    {
        WeeklyCalculator.Met => "met",
        WeeklyCalculator.NotMet => "not-met",
        WeeklyCalculator.Undetermined => "undetermined",
        _ => "none",
    };

    /// <summary>Tone for a result already in words, as a consecutive-weeks pair carries it.</summary>
    public static string ToneOfWords(string words) => words.ToLowerInvariant() switch
    {
        "met" => "met",
        "not met" => "not-met",
        "cannot be determined" => "undetermined",
        _ => "none",
    };

    /// <summary>How one contact was judged against the treatment plan.</summary>
    public static string ContactStatus(string status) => status switch
    {
        "counted" => "Counted",
        "excluded_by_plan" => "Not counted: the plan excludes this service",
        "not_a_session" => "Not counted: not a session",
        "not_addressed_by_plan" => "May count: the plan does not say whether this service counts",
        "uncertain" => "May count: the record does not settle it",
        _ => Plain(status),
    };

    public static string ContactTone(ContactLine c) => c.Counted ? "counted" : c.MayCount ? "may-count" : "not-counted";

    /// <summary>What the system concludes about an appointment, before any plan is applied.</summary>
    public static string Occurrence(EncounterEvent e) => e.Occurrence switch
    {
        "session" => "A session took place.",
        "not_session" => e.NotSessionReason ?? "Not a session.",
        _ => "The record does not establish whether a session took place.",
    };

    /// <summary>How one field of an encounter was decided.</summary>
    public static string Decision(string status) => status switch
    {
        "resolved" => "Settled",
        "resolved_by_corroboration" => "Settled by an independent record",
        "unresolved" => "Not settled",
        _ => "Not stated",
    };

    /// <summary>The groups of the consecutive-weeks result, worded as the command line words them.</summary>
    public static string ConsecutiveStatus(string status) => status switch
    {
        "included" => "Two consecutive weeks below the goal",
        "depends_on_unresolved_documentation" => "Inclusion depends on unresolved documentation",
        "only_with_a_partial_week" => "Below only when a partial week is counted",
        _ => "Not included",
    };

    // ---------- values ----------

    /// <summary>A decided or candidate value of an encounter field, for a table cell.</summary>
    public static string Value(string field, string? value)
    {
        if (value is null) return "Not stated";
        return field switch
        {
            "service category" => Vocabulary.DescribeCategory(value),
            "disposition" => value switch
            {
                "took_place" => "Took place",
                "no_show" => "No show",
                "clinic_cancelled" => "Cancelled by the clinic",
                "patient_cancelled" => "Cancelled by the patient",
                _ => Sentence(Plain(value)),
            },
            "patient present" => value switch
            {
                "present" => "Present",
                "absent" => "Not present",
                _ => Sentence(Plain(value)),
            },
            _ => Sentence(Plain(value)),
        };
    }

    public static string Kind(string kind) => kind switch
    {
        "encounter" => "Encounter description",
        "attendance" => "Attendance",
        "presence" => "Presence",
        "excluded_interval" => "Break or lost connection",
        "stated_duration" => "Stated duration",
        "correction" => "Correction",
        "no_service_contact" => "Record of no service contact",
        "plan_requirement" => "Treatment plan requirement",
        "measure" => "Symptom assessment",
        "observation" => "Observation",
        "authorization" => "Authorization",
        "charge" => "Charge",
        "other" => "Other",
        _ => Sentence(Plain(kind)),
    };

    public static string Record(string recordType, bool isCopy = false) => (recordType switch
    {
        "clinical_note" => "Clinical note",
        "attendance_record" => "Attendance record",
        "correction" => "Correction",
        "appointment_export" => "Appointment export",
        "desk_log" => "Desk log",
        "scheduling_entry" => "Scheduling entry",
        "platform_log" => "Platform log",
        "draft_note" => "Draft note",
        "billing_charge" => "Billing charge",
        "treatment_plan" => "Treatment plan",
        "authorization_letter" => "Authorization letter",
        "measure_record" => "Symptom measure record",
        "import_receipt" => "Import receipt",
        "administrative_notice" => "Administrative notice",
        "other" => "Other record",
        _ => Sentence(Plain(recordType)),
    }) + (isCopy ? " (copy)" : "");

    public static string Signed(string signed) => signed switch
    {
        "yes" => "Signed",
        "no" => "Not signed",
        "extract_of_signed" => "Extract of a signed record",
        _ => Sentence(Plain(signed)),
    };

    /// <summary>The standing of a statement as evidence, as the tiers in Reconciliation/Tiers.cs define it.</summary>
    public static string Tier(int tier) => tier switch
    {
        1 => "Tier 1: signed record",
        2 => "Tier 2: administrative record",
        _ => "Tier 3: draft or billing entry, never evidence",
    };

    // Each reconciliation rule is shown as what it did, in words, with its code beside it, as the
    // rules are described in docs/approach.md.

    /// <summary>How the rule that decided a field is shown.</summary>
    public static string DecisionRule(FieldDecision d) => d.Status switch
    {
        "resolved_by_corroboration" => "Records of equal standing disagree, and an independent record supports one value (rule R4)",
        "unresolved" => "Records of equal standing disagree, and nothing settles it (rule R4)",
        "missing" => "No record that counts as evidence states this (rule R4)",
        _ when d.Rule == "R4" => "The most authoritative record decides (rule R4)",
        _ => RuleCode(d.Rule),
    };

    /// <summary>How the rule that set an assertion aside is shown.</summary>
    public static string SetAsideRule(string rule) => rule switch
    {
        "R3" => "Correction applied (rule R3)",
        "R4" => "Not evidence: an unsigned draft or a billing entry (rule R4)",
        "R4 with corroboration" => "No independent record supports it (rule R4)",
        "R5" => "A less authoritative record that disagrees changes nothing (rule R5)",
        "R6" => "Recorded before the session began (rule R6)",
        _ => RuleCode(rule),
    };

    /// <summary>How the rule behind a disagreement in the record is shown.</summary>
    public static string DisagreementRule(string rule) => rule switch
    {
        "R1" => "Linking assertions to one event (rule R1)",
        "R2" => "Known only from a copy (rule R2)",
        "R3" => "A correction that needs review (rule R3)",
        "R4" => "Records of equal standing disagree (rule R4)",
        "R5" => "A less authoritative record disagrees and changes nothing (rule R5)",
        "R7" => "A stated duration differs from the recorded times (rule R7)",
        _ => RuleCode(rule),
    };

    private static string RuleCode(string rule) => rule.Length == 0 ? "No rule applied" : $"Rule {rule}";

    public static string Modality(string? modality) => modality switch
    {
        null or "not_stated" => "Not stated",
        "in_person" => "In person",
        "video" => "Video",
        "telephone" => "Telephone",
        _ => Sentence(Plain(modality)),
    };

    public static string LinkMethod(string method) => method switch
    {
        "encounter_id" => "Linked by the encounter identifier",
        "date_and_category" => "Linked by service date and service type, because it carried no encounter identifier",
        "every_session_in_document" => "Applied to every group session its document describes, because it named no session and no date",
        _ => Sentence(Plain(method)),
    };

    public static string ObservationCategory(string? category) => category switch
    {
        null or "not categorised" => "Not categorised",
        "patient_reported_symptoms" => "Symptoms the patient reported",
        "symptoms" => "Symptoms",
        "functioning" => "Functioning",
        "safety" => "Safety",
        "reason_for_contact" => "Reason for contact",
        "intervention" => "Intervention",
        "clinician_assessment" => "Clinician's assessment",
        "recommendation" => "Recommendation",
        _ => Sentence(Plain(category)),
    };

    public static string WeekDefinition(string definition) =>
        definition == "sunday_saturday" ? "Sunday to Saturday" : "Monday to Sunday";

    /// <summary>A calculation the model can ask for, by what it works out.</summary>
    public static string Calculation(string name) => name switch
    {
        "find_patient" => "Find the patient",
        "session_counts" => "Sessions attended",
        "weekly_minutes_and_goals" => "Weekly minutes and goals",
        "consecutive_weeks_below_goal" => "Consecutive weeks below the goal",
        "plan_change_comparison" => "Care before and after a plan change",
        "day_reconstruction" => "Everything recorded for one day",
        "patients_with_contacts" => "Patients with a contact in a period",
        "collection_summary" => "Summary by patient",
        "symptom_measures" => "Symptom assessments",
        "observations" => "Quoted observations",
        "search_passages" => "Text search of the documents",
        _ => Words(name),
    };

    /// <summary>The inputs the model chose for a calculation, in words: "patient HG-M042; from 2026-01-05".</summary>
    public static string CalculationInputs(string json)
    {
        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return json;
            var parts = parsed.RootElement.EnumerateObject().Select(p =>
            {
                var value = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.Value.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray().Select(v => v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.GetRawText())),
                    _ => p.Value.GetRawText(),
                };
                return p.Name switch
                {
                    "patient_key" => $"patient {value}",
                    "query" => value.Length == 0 ? "every patient" : $"search for \"{value}\"",
                    "terms" => $"search terms {value}",
                    "suggested_kind" => $"kind of fact sought: {Plain(value)}",
                    "category" => $"category {ObservationCategory(value).ToLowerInvariant()}",
                    _ => $"{Plain(p.Name)} {value}",
                };
            }).ToList();
            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// The name an encounter goes by on the page. Most carry the identifier printed in the record.
    /// One grouped without an identifier has an internal key such as "NOID|2026-02-26|family_therapy",
    /// which is described in words instead.
    /// </summary>
    public static string EncounterLabel(string eventKey, string? encounterId) =>
        encounterId ?? (NoIdKey().Match(eventKey) is { Success: true } m
            ? $"No identifier: {Vocabulary.DescribeCategory(m.Groups["category"].Value).ToLowerInvariant()} on {m.Groups["date"].Value}"
            : eventKey);

    // ---------- text written by the code ----------
    // Sentences are built with words for every value, where they are built, so nothing here
    // rewrites a sentence. Sources, document identifiers, file names, quotes and anything else a
    // document says reach the page exactly as recorded.

    /// <summary>Text written by the code, starting with a capital letter. Nothing in it is changed.</summary>
    public static string Text(string? text) => string.IsNullOrEmpty(text) ? "" : Sentence(text.Trim());

    /// <summary>Text written by the code, ended as a complete sentence.</summary>
    public static string FullSentence(string? text)
    {
        var result = Text(text);
        return result.Length == 0 || result[^1] is '.' or '?' ? result : result + ".";
    }

    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A value as words, such as "Connection lost" for connection_lost.</summary>
    public static string Words(string value) => Sentence(Plain(value));

    /// <summary>A date and time as the page shows it: 2026-01-16 08:17 rather than 2026-01-16T08:17.</summary>
    public static string Timestamp(string text) => IsoTime().IsMatch(text) ? text.Replace('T', ' ') : text;

    private static string Plain(string value) => value.Replace('_', ' ');

    /// <summary>
    /// A sentence about one encounter as it was saved. Encounters saved by an earlier version of
    /// the reconciler named a field's value by its code, for example "tier 1 (signed record) states
    /// took_place.", where the reconciler now writes the value's words. Only those codes are given
    /// their words, and only where they stand alone: never inside one of the encounter's sources
    /// or document identifiers, inside a file name, or inside quotation marks, which is where a
    /// sentence carries a document's own text. A sentence saved with words is left as it is.
    /// </summary>
    public static string Saved(string? text, IEnumerable<string> identifiers)
    {
        var result = Text(text);
        if (result.Length == 0 || !SavedCode().IsMatch(result)) return result;

        var kept = Quoted().Matches(result).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        foreach (var id in identifiers.Where(i => i.Length > 0).Distinct())
            for (var at = result.IndexOf(id, StringComparison.Ordinal); at >= 0; at = result.IndexOf(id, at + id.Length, StringComparison.Ordinal))
                kept.Add((at, at + id.Length));

        return SavedCode().Replace(result, m => kept.Any(k => m.Index < k.End && m.Index + m.Length > k.Start) ? m.Value : SavedCodes[m.Value]);
    }

    /// <summary>The codes the reconciler once wrote into sentences, with the words it writes now.</summary>
    private static readonly Dictionary<string, string> SavedCodes =
        Vocabulary.ServiceCategories.Select(c => (Code: c, Words: Vocabulary.DescribeValue("service category", c)))
            .Concat(new[] { "took_place", "no_show", "clinic_cancelled", "patient_cancelled" }.Select(c => (Code: c, Words: Vocabulary.DescribeValue("disposition", c))))
            .Append((Code: "absent", Words: Vocabulary.DescribeValue("patient present", "absent")))
            .Where(p => p.Code.Contains('_') || p.Code == "absent")
            .ToDictionary(p => p.Code, p => p.Words, StringComparer.Ordinal);

    [GeneratedRegex(@"(?<![\w./\\-])(individual_therapy|group_therapy|family_therapy|collateral_contact|medication_management|care_coordination|took_place|no_show|clinic_cancelled|patient_cancelled|absent)(?![\w/\\-]|\.\w)")]
    private static partial Regex SavedCode();

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")]
    private static partial Regex IsoTime();

    [GeneratedRegex(@"NOID\|(?<date>\d{4}-\d{2}-\d{2})\|(?<category>[a-z_]+)")]
    private static partial Regex NoIdKey();
}

using System.Text.Json;
using Anthropic.Models.Messages;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Extraction;

public sealed record ExtractionResult(string DocHash, string? PrintedId, string PatientKey, int Accepted, int Relocated, int Rejected, int CriticalRejected, int Attempts)
{
    /// <summary>Every patient an accepted assertion is about. A roster can name patients other than the document's own.</summary>
    public IReadOnlyList<string> PatientKeys { get; init; } = [PatientKey];
}

/// <summary>
/// Turns one document into assertions. The model proposes them; code decides which to keep.
/// An assertion is kept only if its kind is known and its quote is found in the document.
/// </summary>
public sealed class Extractor(Database database, ModelClient model)
{
    private const int MaxAttempts = 3;

    /// <summary>Kinds whose loss would change session counts or minutes.</summary>
    private static readonly HashSet<string> CriticalKinds = ["encounter", "attendance", "presence", "excluded_interval", "correction", "plan_requirement"];

    private sealed class RawPatient
    {
        public string? Name { get; set; }
        public string? DateOfBirth { get; set; }
        public string? Mrn { get; set; }
    }

    /// <summary>The fields common to every assertion, as the model returns them.</summary>
    private sealed class RawAssertion
    {
        public string? Kind { get; set; }
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public string? Quote { get; set; }
        public string? EncounterId { get; set; }
        public List<string>? OtherIds { get; set; }
        public string? ServiceDate { get; set; }
        public string? DateAsWritten { get; set; }
        public string? PatientMrn { get; set; }
        public string? RecordType { get; set; }
        public string? Signed { get; set; }
        public string? Signer { get; set; }
        public string? SignedAt { get; set; }
        public string? RecordedAt { get; set; }
        public bool? IsCopy { get; set; }
        public string? OriginalSignedAt { get; set; }
        public string? ReceivedAt { get; set; }
    }

    private sealed record Attempt(string RawJson, string? Structure, string? PrintedId, RawPatient? Patient, List<Assertion> Accepted, List<(string Reason, string? Kind, bool Critical, string Json)> Rejected, string StopReason);

    public async Task<ExtractionResult> ExtractAsync(StoredDocument document, CancellationToken cancellationToken = default)
    {
        var modelId = model.Settings.ResolveModelId(model.Settings.ExtractionModel);

        var best = await AttemptAsync(document, modelId, cancellationToken);
        var attempts = 1;

        // A result with nothing usable in it, or one cut off at the token limit, is tried again up to
        // twice. A result that lost individual assertions is tried again once. The best attempt is kept.
        bool Unusable(Attempt a) => a.Accepted.Count == 0 || a.StopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase);
        while (attempts < MaxAttempts && (Unusable(best) || (attempts == 1 && best.Rejected.Count > 0)))
        {
            var next = await AttemptAsync(document, modelId, cancellationToken);
            attempts++;
            if (Score(next) > Score(best)) best = next;
        }

        // The model sometimes returns the assertions and leaves out who the document is about.
        // A short second request asks only for that, so no document is left unassigned.
        if (PatientKeyFor(best.Patient) == "unassigned")
        {
            var header = await HeaderAsync(document, modelId, cancellationToken);
            attempts++;
            best = best with
            {
                Patient = header.Patient ?? best.Patient,
                PrintedId = best.PrintedId ?? header.PrintedId,
                Structure = best.Structure ?? header.Structure,
            };
        }

        // The identifier printed in the document is read by code when it follows the usual label.
        // The file name is the last resort, so every citation names a document a reviewer can find.
        best = best with { PrintedId = PrintedIdIn(document.Text) ?? best.PrintedId ?? Path.GetFileNameWithoutExtension(document.Path) };

        var patientKey = PatientKeyFor(best.Patient);
        foreach (var assertion in best.Accepted)
        {
            assertion.PrintedDocId = best.PrintedId;
            if (string.IsNullOrEmpty(assertion.PatientKey)) assertion.PatientKey = patientKey;
        }

        if (patientKey != "unassigned") database.UpsertPatient(patientKey, best.Patient?.Mrn?.Trim(), best.Patient?.Name?.Trim(), best.Patient?.DateOfBirth?.Trim());

        // An assertion can name a patient other than the document's own. Each of them needs a patient
        // row, or their events are never built. Only the identifier is known at this point.
        var patientKeys = best.Accepted.Select(x => x.PatientKey).Append(patientKey).Distinct().ToList();
        foreach (var other in patientKeys.Where(k => k != patientKey && k != "unassigned"))
            database.UpsertPatient(other, null, null, null);
        database.SetPrintedId(document.DocHash, best.PrintedId);

        // A document that yields nothing is a failure, never an empty success.
        var truncated = best.StopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase);
        var status = best.Accepted.Count == 0 ? "failed"
            : truncated ? "truncated"
            : best.Rejected.Any(r => r.Critical) ? "incomplete"
            : "complete";
        database.SaveExtraction(
            new ExtractionRecord(document.DocHash, Vocabulary.ExtractorVersion, modelId, status, best.Structure, best.Accepted.Count, best.Rejected.Count, patientKey),
            best.RawJson, best.Accepted, best.Rejected);

        return new ExtractionResult(
            document.DocHash, best.PrintedId, patientKey, best.Accepted.Count,
            best.Accepted.Count(a => a.QuoteStatus == "relocated"), best.Rejected.Count, best.Rejected.Count(r => r.Critical), attempts)
        { PatientKeys = patientKeys };
    }

    private static int Score(Attempt attempt) =>
        attempt.Accepted.Count - 10 * attempt.Rejected.Count(r => r.Critical) - attempt.Rejected.Count
        - (attempt.StopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ? 1000 : 0);

    /// <summary>
    /// Both requests send the same instructions and the same two tools, so the provider's cache
    /// serves them both. Only the tool the model is told to call differs.
    /// </summary>
    private static MessageCreateParams Request(StoredDocument document, string modelId, string toolName, int maxTokens) => new()
    {
        Model = modelId,
        MaxTokens = maxTokens,
        System = new List<TextBlockParam> { new() { Text = ExtractionPrompt.System, CacheControl = new CacheControlEphemeral() } },
        Messages = [new() { Role = Role.User, Content = DocumentRegistry.WithLineNumbers(document.Text) }],
        Tools =
        [
            new Tool
            {
                Name = ExtractionSchema.ToolName,
                Description = ExtractionSchema.ToolDescription,
                InputSchema = new() { Properties = ExtractionSchema.Properties(), Required = ExtractionSchema.Required },
            },
            new Tool
            {
                Name = ExtractionSchema.HeaderToolName,
                Description = ExtractionSchema.HeaderToolDescription,
                InputSchema = new() { Properties = ExtractionSchema.HeaderProperties(), Required = ExtractionSchema.HeaderRequired },
            },
        ],
        ToolChoice = new ToolChoiceTool { Name = toolName },
    };

    private static string ToolInput(Message response)
    {
        foreach (var block in response.Content)
            if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                return JsonSerializer.Serialize(toolUse.Input);
        return "{}";
    }

    private async Task<Attempt> AttemptAsync(StoredDocument document, string modelId, CancellationToken cancellationToken)
    {
        var response = await model.SendAsync(Request(document, modelId, ExtractionSchema.ToolName, 16000), "extraction", document.DocHash, cancellationToken);
        return Parse(document, modelId, ToolInput(response), ModelClient.Plain(response.StopReason?.ToString()));
    }

    private async Task<(string? Structure, string? PrintedId, RawPatient? Patient)> HeaderAsync(StoredDocument document, string modelId, CancellationToken cancellationToken)
    {
        var response = await model.SendAsync(Request(document, modelId, ExtractionSchema.HeaderToolName, 2000), "extraction header", document.DocHash, cancellationToken);
        using var parsed = JsonDocument.Parse(ToolInput(response));
        var root = Unwrap(parsed.RootElement);
        RawPatient? patient = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("patient", out var element) && Unwrap(element) is { ValueKind: JsonValueKind.Object } found)
            patient = found.Deserialize<RawPatient>(Json.Options);
        return root.ValueKind == JsonValueKind.Object
            ? (ReadString(root, "document_structure"), Blank(ReadString(root, "printed_document_id")), patient)
            : (null, null, null);
    }

    private static string? PrintedIdIn(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"^\s*(?:Document|Record|Doc)\s+(?:ID|Id|id|No\.?|Number)\s*[:#]\s*(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim().TrimEnd('.', ',') : null;
    }

    /// <summary>Reads a model result without calling a model, so the reading rules can be tested.</summary>
    public static (List<Assertion> Accepted, List<string> RejectedReasons) Read(StoredDocument document, string rawJson)
    {
        var attempt = Parse(document, "none", rawJson, "tool_use");
        return (attempt.Accepted, attempt.Rejected.Select(r => r.Reason).ToList());
    }

    private static Attempt Parse(StoredDocument document, string modelId, string rawJson, string stopReason)
    {
        var lines = document.Text.Split('\n');
        var accepted = new List<Assertion>();
        var rejected = new List<(string, string?, bool, string)>();

        using var parsed = JsonDocument.Parse(rawJson);
        var root = Unwrap(parsed.RootElement);
        var structure = ReadString(root, "document_structure");
        var printedId = ReadString(root, "printed_document_id");
        RawPatient? patient = null;
        if (root.TryGetProperty("patient", out var patientElement) && patientElement.ValueKind == JsonValueKind.Object)
            patient = patientElement.Deserialize<RawPatient>(Json.Options);

        {
            foreach (var item in Items(root, rejected))
            {
                var itemJson = item.GetRawText();
                RawAssertion? raw;
                AssertionFields? fields;
                try
                {
                    raw = item.Deserialize<RawAssertion>(Json.Options);
                    fields = item.Deserialize<AssertionFields>(Json.Options);
                }
                catch (JsonException e)
                {
                    rejected.Add(($"could not be read: {e.Message}", null, true, itemJson));
                    continue;
                }

                if (raw is null || fields is null || raw.Kind is null || !Vocabulary.Kinds.Contains(raw.Kind))
                {
                    rejected.Add(($"unknown kind '{raw?.Kind}'", raw?.Kind, true, itemJson));
                    continue;
                }

                var critical = CriticalKinds.Contains(raw.Kind);
                var check = QuoteVerifier.Check(lines, raw.LineStart, raw.LineEnd, raw.Quote ?? "");
                if (!check.Found)
                {
                    rejected.Add((check.Status, raw.Kind, critical, itemJson));
                    continue;
                }

                Clean(fields);
                accepted.Add(new Assertion
                {
                    DocHash = document.DocHash,
                    PatientKey = string.IsNullOrWhiteSpace(raw.PatientMrn) ? "" : NormaliseKey(raw.PatientMrn),
                    EncounterId = Blank(raw.EncounterId),
                    OtherIds = raw.OtherIds is { Count: > 0 } ? raw.OtherIds : null,
                    ServiceDate = Domain.Parse.Date(raw.ServiceDate),
                    DateAsWritten = Blank(raw.DateAsWritten),
                    Kind = raw.Kind,
                    Fields = fields,
                    LineStart = check.LineStart,
                    LineEnd = check.LineEnd,
                    Quote = raw.Quote!.Trim(),
                    QuoteStatus = check.Status,
                    RecordType = Vocabulary.RecordTypes.Contains(raw.RecordType) ? raw.RecordType! : "other",
                    Signed = Vocabulary.SignedValues.Contains(raw.Signed) ? raw.Signed! : "no",
                    Signer = Blank(raw.Signer),
                    SignedAt = Domain.Parse.DateTimeLocal(raw.SignedAt),
                    RecordedAt = Domain.Parse.DateTimeLocal(raw.RecordedAt),
                    IsCopy = raw.IsCopy ?? false,
                    OriginalSignedAt = Domain.Parse.DateTimeLocal(raw.OriginalSignedAt),
                    ReceivedAt = Domain.Parse.DateTimeLocal(raw.ReceivedAt),
                    ExtractorVersion = Vocabulary.ExtractorVersion,
                    Model = modelId,
                });
            }
        }

        return new Attempt(rawJson, structure, Blank(printedId), patient, accepted, rejected, stopReason);
    }

    /// <summary>
    /// The assertion objects in a result. The list normally arrives as a JSON array. When it arrives
    /// as a string that does not parse as a whole, each object in it is read on its own, so one
    /// malformed object costs that object and not the document.
    /// </summary>
    private static List<JsonElement> Items(JsonElement root, List<(string, string?, bool, string)> rejected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("assertions", out var raw)) return [];

        var unwrapped = Unwrap(raw);
        if (unwrapped.ValueKind == JsonValueKind.Array) return unwrapped.EnumerateArray().Select(e => e.Clone()).ToList();
        if (unwrapped.ValueKind != JsonValueKind.String) return [];

        var items = new List<JsonElement>();
        foreach (var text in TopLevelObjects(unwrapped.GetString() ?? ""))
        {
            try
            {
                using var parsed = JsonDocument.Parse(text);
                items.Add(parsed.RootElement.Clone());
            }
            catch (JsonException e)
            {
                rejected.Add(($"malformed output: {e.Message}", null, true, text));
            }
        }
        return items;
    }

    /// <summary>Splits text into the objects it contains by matching braces, skipping braces inside strings.</summary>
    private static IEnumerable<string> TopLevelObjects(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0) yield return text[start..(i + 1)];
            }
        }
    }

    /// <summary>
    /// Models sometimes return a nested value as a string of JSON, or wrap the whole result inside
    /// one of its own properties. This reads through both, so a formatting slip does not lose a document.
    /// </summary>
    private static JsonElement Unwrap(JsonElement element)
    {
        for (var depth = 0; depth < 3; depth++)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text) || (text.TrimStart()[0] != '{' && text.TrimStart()[0] != '[')) return element;
                try { element = JsonDocument.Parse(text).RootElement.Clone(); }
                catch (JsonException) { return element; }
                continue;
            }
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("assertions", out var inner)
                && inner.ValueKind == JsonValueKind.String
                && Unwrap(inner) is { ValueKind: JsonValueKind.Object } whole
                && whole.TryGetProperty("assertions", out _))
            {
                element = whole;
                continue;
            }
            return element;
        }
        return element;
    }

    /// <summary>Drops values outside the fixed lists, so the rules only ever see values they know.</summary>
    private static void Clean(AssertionFields f)
    {
        f.ServiceCategory = Keep(f.ServiceCategory, Vocabulary.ServiceCategories);
        f.Modality = Keep(f.Modality, Vocabulary.Modalities);
        f.Disposition = Keep(f.Disposition, Vocabulary.Dispositions);
        f.PatientPresent = Keep(f.PatientPresent, Vocabulary.PatientPresentValues);
        f.IntervalReason = Keep(f.IntervalReason, Vocabulary.IntervalReasons);
        f.DurationMeasures = Keep(f.DurationMeasures, Vocabulary.DurationMeasures);
        f.CorrectedField = Keep(f.CorrectedField, Vocabulary.CorrectedFields);
        f.WeekDefinition = Keep(f.WeekDefinition, Vocabulary.WeekDefinitions);
        f.ObservationCategory = Keep(f.ObservationCategory, Vocabulary.ObservationCategories);
        f.CountedCategories = f.CountedCategories?.Where(c => Vocabulary.ServiceCategories.Contains(c)).Distinct().ToList();
        f.ExcludedCategories = f.ExcludedCategories?.Where(c => Vocabulary.ServiceCategories.Contains(c)).Distinct().ToList();
        if (f.CountedCategories is { Count: 0 }) f.CountedCategories = null;
        if (f.ExcludedCategories is { Count: 0 }) f.ExcludedCategories = null;
        if (f.Intervals is { Count: 0 }) f.Intervals = null;
        if (f.ItemScores is { Count: 0 }) f.ItemScores = null;
        if (f.Clinicians is { Count: 0 }) f.Clinicians = null;
    }

    private static string? Keep(string? value, string[] allowed) => value is not null && allowed.Contains(value) ? value : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The medical record number identifies a patient. Name and date of birth together are the fallback.</summary>
    private static string PatientKeyFor(RawPatient? patient)
    {
        if (patient is null) return "unassigned";
        if (!string.IsNullOrWhiteSpace(patient.Mrn)) return NormaliseKey(patient.Mrn);
        if (!string.IsNullOrWhiteSpace(patient.Name) && !string.IsNullOrWhiteSpace(patient.DateOfBirth))
            return NormaliseKey($"{patient.Name}|{patient.DateOfBirth}");
        return "unassigned";
    }

    public static string NormaliseKey(string value) => value.Trim().ToUpperInvariant();
}

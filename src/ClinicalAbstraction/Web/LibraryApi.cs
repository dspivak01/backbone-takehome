using System.Globalization;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Web;

/// <summary>
/// What the database holds beside the abstraction: the documents, the answers to earlier
/// questions, and the gap log. Like <see cref="ReviewApi"/>, every method takes plain parameters,
/// returns an object ready to show, and writes nothing.
/// </summary>
public sealed class LibraryApi(Database database)
{
    /// <summary>The list of earlier questions shows at most this many, newest first.</summary>
    public const int AnswersShown = 100;

    // ---------- documents ----------

    public DocumentListView Documents()
    {
        var documents = database.ListDocuments();
        var extractions = Extractions();
        var names = PatientNames();
        var uploaded = Uploads.OriginalNames(database);
        return new DocumentListView
        {
            Total = documents.Count,
            Empty = documents.Count == 0 ? "No documents have been added to this database yet. Choose text files above, or name a folder on this machine." : null,
            Documents = documents.Select(d =>
            {
                var extraction = extractions.GetValueOrDefault(d.DocHash);
                var (status, detail, tone) = Status(extraction?.Status);
                var key = PatientOf(extraction?.PatientKey);
                return new DocumentRow
                {
                    Id = d.DocHash, Identifier = Identifier(d), FileName = FileName(d, uploaded),
                    PatientKey = key, Patient = PatientLabel(key, names),
                    Kept = extraction?.Assertions ?? 0, Rejected = extraction?.Rejected ?? 0, Status = status, StatusDetail = detail, Tone = tone,
                };
            }).ToList(),
        };
    }

    /// <summary>One document's text with line numbers, and every assertion kept from it, by line.</summary>
    public DocumentView Document(string? id)
    {
        var wanted = (id ?? "").Trim();
        if (wanted.Length == 0) throw new RequestException("Name a document.");
        var documents = database.ListDocuments();
        var d = documents.FirstOrDefault(x => x.DocHash == wanted)
            ?? (wanted.Length >= SourceLookup.ShortestHashPrefix && documents.Where(x => x.DocHash.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList() is [var one] ? one : null)
            ?? (documents.Where(x => string.Equals(x.PrintedId, wanted, StringComparison.OrdinalIgnoreCase)).ToList() is [var printed] ? printed : null)
            ?? throw new NotFoundException($"No document \"{wanted}\" is in this database.");

        var extraction = Extractions().GetValueOrDefault(d.DocHash);
        var (status, detail, tone) = Status(extraction?.Status);
        // In the order they were saved, which is the order step 4 of the trace lists them in.
        var assertions = database.GetAssertions().Where(a => a.DocHash == d.DocHash).ToList();
        var key = PatientOf(extraction?.PatientKey) ?? PatientOf(assertions.Select(a => a.PatientKey).FirstOrDefault());
        var lines = d.Text.Split('\n');
        var uploaded = Uploads.OriginalNames(database);

        return new DocumentView
        {
            Id = d.DocHash, Identifier = Identifier(d), FileName = FileName(d, uploaded), File = d.Path,
            AlsoSeenAs = database.ListCopies(d.DocHash).Where(p => p != d.Path).Select(p => uploaded.TryGetValue(p, out var name) ? $"{name} (added from this page)" : p).ToList(),
            PatientKey = key, Patient = PatientLabel(key, PatientNames()),
            Kept = extraction?.Assertions ?? 0, Rejected = extraction?.Rejected ?? 0, Status = status, StatusDetail = detail, Tone = tone,
            LineCount = lines.Length,
            Lines = lines.Select((text, i) => new DocumentLine
            {
                Number = i + 1, Text = text,
                Statements = assertions.Select((a, n) => (a, n)).Where(x => x.a.LineStart <= i + 1 && i + 1 <= x.a.LineEnd).Select(x => x.n).ToList(),
            }).ToList(),
            Statements = assertions.Select(ReviewApi.Statement).ToList(),
        };
    }

    /// <summary>The latest extraction of each document.</summary>
    private Dictionary<string, Extraction> Extractions()
    {
        var latest = new Dictionary<string, Extraction>();
        foreach (var e in database.ListExtractions()) latest[e.DocHash] = new Extraction(e.PatientKey, e.Status, e.Assertions, e.Rejected);
        return latest;
    }

    private sealed record Extraction(string PatientKey, string Status, int Assertions, int Rejected);

    private Dictionary<string, string?> PatientNames() => database.ListPatients().ToDictionary(p => p.PatientKey, p => p.Name);

    private static string? PatientOf(string? key) => string.IsNullOrEmpty(key) || key == "unassigned" ? null : key;

    private static string PatientLabel(string? key, Dictionary<string, string?> names) =>
        key is null ? "Not identified" : $"{names.GetValueOrDefault(key) ?? "Name not recorded"} ({key})";

    private static string Identifier(StoredDocument d) => d.PrintedId ?? d.DocHash[..Math.Min(12, d.DocHash.Length)];

    /// <summary>The name the file arrived with when it was added from the page, or the name of the file it was read from.</summary>
    private static string FileName(StoredDocument d, Dictionary<string, string> uploaded) =>
        uploaded.TryGetValue(d.Path, out var name) ? name : Path.GetFileName(d.Path);

    /// <summary>A document's extraction in a word or two, what that means, and the colour family the page gives it.</summary>
    private static (string Status, string Detail, string Tone) Status(string? status) => status switch
    {
        null => ("Not read yet", "The model has not read this document. It will be read the next time documents are added.", "none"),
        "complete" => ("Complete", "Every assertion the model returned about attendance, presence and the plan was kept.", "met"),
        "incomplete" => ("Incomplete", "Assertions about attendance, presence or the plan were rejected because their quotes were not found in the document, so totals that depend on this document may be understated.", "undetermined"),
        "truncated" => ("Cut off", "The model's reply was too long and was cut off. Nothing from this document is in the abstraction, and it will be read again the next time documents are added.", "not-met"),
        "failed" => ("Failed", "Nothing usable could be read from this document, so nothing from it is in the abstraction. It will be read again the next time documents are added.", "not-met"),
        _ => (Wording.Words(status), "", "none"),
    };

    // ---------- the gap log ----------

    /// <summary>Passages found by text search, grouped by the kind of fact the model suggested, as the gaps command groups them.</summary>
    public GapLogView Gaps()
    {
        var gaps = database.ListGaps();
        return new GapLogView
        {
            Passages = gaps.Count,
            Empty = gaps.Count == 0
                ? "The gap log is empty. A passage is added when a question needs a fact the calculations do not hold and the model searches the documents' text for it."
                : null,
            Groups = GapLog.Groups(gaps).Select(g => new GapGroupView
            {
                Kind = g.Kind == GapLog.NotSuggested ? "No kind suggested" : Wording.Words(g.Kind),
                Count = g.Passages.Count,
                Questions = g.Questions,
                Passages = g.Passages.Select(p => new GapPassageView
                {
                    Source = $"{p.PrintedDocId ?? p.DocHash[..Math.Min(12, p.DocHash.Length)]} L{p.Line}",
                    PatientKey = PatientOf(p.PatientKey), SearchTerms = p.SearchTerms, Passage = p.Passage,
                }).ToList(),
            }).ToList(),
        };
    }

    // ---------- answers ----------

    /// <summary>Earlier questions, newest first, each marked when it was served from a saved answer.</summary>
    public AnswerListView Answers()
    {
        var total = database.CountAnswers();
        return new AnswerListView
        {
            Total = total,
            Empty = total == 0 ? "No question has been asked of this database yet." : null,
            Note = total > AnswersShown ? $"Showing the {AnswersShown} most recent of {total} questions." : null,
            Answers = database.ListRecentAnswers(AnswersShown).Select(r =>
            {
                var (question, patient) = QuestionAnswerer.ReadAsked(r.Question);
                return new AnswerRow
                {
                    Id = r.Id, Question = question, Patient = patient, AskedAt = When(r.CreatedAt), FromSaved = r.FromCache,
                    How = r.FromCache ? "Served from a saved answer" : $"Answered by the model in {Seconds(r.LatencyMs)}",
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// One saved answer in its three parts, each saying who wrote it: the written answer by the
    /// model, the checks by code, and the calculation tables by code. The tables are the ones the
    /// command line printed for each calculation, divided into rows and cells.
    /// </summary>
    public AnswerView Answer(string? id)
    {
        if (!long.TryParse((id ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new RequestException("Name an answer by its number.");
        var row = database.FindAnswer(number) ?? throw new NotFoundException($"No answer numbered {number} is in this database.");
        var (question, patient) = QuestionAnswerer.ReadAsked(row.Question);

        var view = new AnswerView
        {
            Id = row.Id, Question = question, PatientKey = patient, AskedAt = When(row.CreatedAt), FromSaved = row.FromCache,
            How = row.FromCache
                ? "Served from a saved answer. The same question had been answered from the same abstraction, with the same model and instructions, so no model was called."
                : $"Answered by the model in {Seconds(row.LatencyMs)}.",
            NarrativeBy = QuestionAnswerer.NarrativeBy, ChecksBy = QuestionAnswerer.ChecksBy, CalculationsBy = QuestionAnswerer.CalculationsBy,
            SourcePatients = SourcePatients(),
        };
        if (patient is not null)
            view.Patient = database.LoadAbstraction(patient) is { } p ? $"{p.Name ?? "Name not recorded"} ({p.PatientKey})" : patient;

        AnswerRecord record;
        try
        {
            record = Json.Read<AnswerRecord>(row.Json);
        }
        catch (Exception)
        {
            view.Note = "This answer was saved in a form the page cannot divide into its parts, so it is shown as it was saved.";
            view.Narrative = MarkdownBlocks.Read(row.Markdown);
            return view;
        }

        view.Question = record.Question.Length > 0 ? record.Question : question;
        view.Narrative = MarkdownBlocks.Read(record.Narrative);
        view.Passed = record.Checks.Passed;
        view.Warning = record.Checks.Passed ? null : Warning(record.Checks);
        view.Checks = QuestionAnswerer.CheckRows(record.Checks).Select(r => new CheckRow { Check = r[0], Result = r[1] }).ToList();
        view.Calculations = QuestionAnswerer.Tabled(record).Select((c, i) => new CalculationView
        {
            Number = i + 1, Title = Wording.Calculation(c.Name), Inputs = Wording.CalculationInputs(c.Input),
            Ran = $"Ran in {c.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)} ms.", Failed = c.Failed, Blocks = MarkdownBlocks.Read(c.Rendered, literal: true),
        }).ToList();
        view.Produced =
        [
            new() { Label = "Model", Value = record.Model },
            new() { Label = "Model calls", Value = record.ModelCalls.ToString(CultureInfo.InvariantCulture) },
            new() { Label = "Tokens", Value = $"{record.InputTokens} input, {record.OutputTokens} output" },
            new() { Label = "Calculations run", Value = record.Calculations.Count == 0 ? "None" : string.Join(", ", record.Calculations.Select(c => Wording.Calculation(c.Name))) },
            new() { Label = "Calculations asked for and not run", Value = record.CalculationsNotRun == 0 ? "None" : $"{record.CalculationsNotRun}. Each gave a date before the list of patients and their episode dates had been returned, so it was held back and asked for again" },
            new() { Label = "Time", Value = $"{record.LatencyMs} ms" },
            new() { Label = "Abstraction version", Value = record.AbstractionVersion },
        ];
        return view;
    }

    /// <summary>The warning shown above the written answer when the checks found something the results do not hold.</summary>
    private static string Warning(NarrativeCheckResult checks)
    {
        var parts = new List<string>();
        if (checks.CitationsNotInResults.Count > 0)
            parts.Add($"{(checks.CitationsNotInResults.Count == 1 ? "a source" : $"{checks.CitationsNotInResults.Count} sources")} that the calculation results do not contain ({string.Join(", ", checks.CitationsNotInResults)})");
        if (checks.NumbersNotInResults.Count > 0)
            parts.Add($"{(checks.NumbersNotInResults.Count == 1 ? "a number" : $"{checks.NumbersNotInResults.Count} numbers")} that neither the calculation results nor the question contain ({string.Join(", ", checks.NumbersNotInResults)})");
        return $"The checks found {string.Join(", and ", parts)} in the written answer. Check the written answer against the calculation tables below, which are authoritative.";
    }

    /// <summary>
    /// The patient whose record each document identifier belongs to, so a source in an answer opens
    /// the trace for that patient. An identifier that documents of two patients share is left out.
    /// </summary>
    private Dictionary<string, string> SourcePatients()
    {
        var extractions = Extractions();
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var d in database.ListDocuments())
        {
            if (PatientOf(extractions.GetValueOrDefault(d.DocHash)?.PatientKey) is not { } key) continue;
            foreach (var name in new[] { d.PrintedId, d.DocHash[..Math.Min(8, d.DocHash.Length)], d.DocHash[..Math.Min(12, d.DocHash.Length)] }.OfType<string>())
            {
                if (!owners.TryGetValue(name, out var set)) owners[name] = set = [];
                set.Add(key);
            }
        }
        return owners.Where(o => o.Value.Count == 1).ToDictionary(o => o.Key, o => o.Value.First(), StringComparer.Ordinal);
    }

    /// <summary>A saved time as the page shows it: 2026-09-28 21:58 UTC.</summary>
    private static string When(string saved) =>
        DateTime.TryParse(saved, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : saved;

    private static string Seconds(long milliseconds) =>
        milliseconds < 1000 ? "less than a second" : $"{(milliseconds / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} seconds";
}

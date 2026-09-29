using System.Diagnostics;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reconciliation;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction;

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Usage();
        var options = new Options(args);
        try
        {
            return options.Command switch
            {
                "ingest" => await IngestAsync(options),
                "reconcile" => Reconcile(options),
                "patients" => Patients(options),
                "calc" => Calc(options),
                "show" => Show(options),
                "source" => Source(options),
                "ask" => await AskAsync(options),
                "export" => Export(options),
                "gaps" => Gaps(options),
                "benchmark" => await Benchmark.RunAsync(options),
                "evaluate" => Benchmark.Evaluate(options),
                "serve" => await Web.Server.RunAsync(options),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    // ---------- ingest and reconcile ----------

    private static async Task<int> IngestAsync(Options options)
    {
        var folder = options.Positional(0) ?? "documents";
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");

        using var database = new Database(options.DatabasePath);
        using var log = new Log(options.LogDirectory, "ingest");
        await IngestFolderAsync(database, folder, log, () => ModelClient.CreateAsync(database, ModelSettings.FromEnvironment()));
        return 0;
    }

    /// <summary>
    /// The work of the ingest command, shared with the review page: registers, extracts and
    /// reconciles the files in a folder, reports the model usage, and folds the write-ahead log
    /// into the database file. Progress, when asked for, is reported file by file.
    /// </summary>
    public static async Task<IngestSummary> IngestFolderAsync(Database database, string folder, Log log, Func<Task<ModelClient>> model, Action<IngestProgress>? progress = null)
    {
        log.Info($"Ingesting {folder} into {database.FilePath}");

        var service = new ReconcileService(database);
        var pipeline = new IngestPipeline(database, log);
        var summary = await pipeline.RunAsync(folder, model, patients => service.Run(patients), progress);

        log.Info($"Extraction: {summary.DocumentsExtracted} documents, {summary.AssertionsAccepted} assertions kept ({summary.QuotesRelocated} with corrected line numbers), {summary.AssertionsRejected} rejected ({summary.CriticalRejected} critical).");
        log.Info($"Reconciled {summary.PatientsReconciled} patient(s).");
        log.Info($"Time: register {summary.RegisterMs} ms, extract {summary.ExtractMs} ms, reconcile {summary.ReconcileMs} ms, total {summary.TotalMs} ms.");
        foreach (var warning in summary.Warnings) log.Info($"Warning: {warning}");
        ReportUsage(database, log);
        database.Checkpoint();
        return summary;
    }

    private static int Reconcile(Options options)
    {
        using var database = new Database(options.DatabasePath);
        var timer = Stopwatch.StartNew();
        var results = new ReconcileService(database).Run();
        foreach (var p in results)
            Console.WriteLine($"{p.Name ?? "name not recorded"} ({p.PatientKey}): {p.Encounters.Count} encounters, {p.Measurements.Count} measurements, {p.Plans.Count} plan version(s), version {p.Version}");
        Console.WriteLine($"Rebuilt {results.Count} patient(s) from saved assertions in {timer.ElapsedMilliseconds} ms. No model was called.");
        database.Checkpoint();
        return 0;
    }

    public static void ReportUsage(Database database, Log log)
    {
        foreach (var u in database.SummariseModelCalls())
        {
            var cost = ModelSettings.EstimateCost(u.Model, u.InputTokens, u.OutputTokens, u.CacheWriteTokens, u.CacheReadTokens);
            log.Info($"Model usage so far, {u.Purpose} with {u.Model}: {u.Calls} calls, {u.InputTokens} input, {u.CacheWriteTokens} cache write, {u.CacheReadTokens} cache read, {u.OutputTokens} output tokens, " +
                     $"{u.LatencyMs} ms in total. Cost at list price: {(cost is null ? "unknown model" : $"${cost:0.0000}")}.");
        }
    }

    // ---------- inspection, with no model ----------

    private static int Patients(Options options)
    {
        using var database = new Database(options.DatabasePath);
        Console.WriteLine(Markdown.Table(["Patient", "Key", "Date of birth", "Episode", "Plans", "Encounters", "Version"],
            new Calculations(database).ListPatients().Select(m => new[]
            {
                m.Name ?? "", m.PatientKey, m.DateOfBirth ?? "", $"{m.EpisodeStart} to {m.EpisodeEnd}", m.PlanVersions.ToString(), m.Encounters.ToString(), m.AbstractionVersion,
            })));
        return 0;
    }

    /// <summary>Runs one calculation with explicit parameters. This path never calls a model.</summary>
    private static int Calc(Options options)
    {
        var name = options.Positional(0) ?? throw new ArgumentException("Name a calculation: sessions, weekly, consecutive, summary, patients, plan-change, day, measures, observations, search.");
        if (name.ToLowerInvariant() is "summary" or "patients" && options.Has("patient"))
            throw new ArgumentException($"'calc {name}' covers every patient who fits, so it does not take --patient. For one patient, use 'calc sessions' and 'calc weekly' with --patient.");

        using var database = new Database(options.DatabasePath);
        var calculations = new Calculations(database);
        var patient = options.Get("patient") ?? SinglePatient(calculations, name);

        var timer = Stopwatch.StartNew();
        object result = name.ToLowerInvariant() switch
        {
            "sessions" => calculations.SessionCounts(patient!, options.Get("from"), options.Get("to")),
            "weekly" or "minutes" or "goals" => calculations.Weekly(patient!, options.Get("from"), options.Get("to")),
            "consecutive" => calculations.ConsecutiveWeeks(options.Get("patient"), options.Get("from"), options.Get("to")),
            "summary" => calculations.CollectionSummary(options.Get("from"), options.Get("to")),
            "patients" => calculations.PatientsInPeriod(options.Get("from"), options.Get("to")),
            "plan-change" => calculations.PlanChange(patient!),
            "day" when patient is null => calculations.DayAcrossPatients(options.Get("date") ?? throw new ArgumentException("Give --date yyyy-MM-dd.")),
            "day" => calculations.Day(patient, options.Get("date") ?? throw new ArgumentException("Give --date yyyy-MM-dd.")),
            "measures" => calculations.Measures(patient!, options.Get("instrument")),
            "observations" => calculations.Observations(patient!, options.Get("from"), options.Get("to"), options.Get("category")),
            "search" => calculations.Search(patient!, (options.Get("terms") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), "command line search", options.Get("kind")),
            _ => throw new ArgumentException($"Unknown calculation '{name}'."),
        };
        timer.Stop();

        Console.WriteLine(options.Has("json") ? Json.Write(result, indented: true) : Markdown.Render(result));
        Console.Error.WriteLine($"Calculated in {timer.Elapsed.TotalMilliseconds:0.0} ms from the saved abstraction. No model was called.");
        return 0;
    }

    /// <summary>
    /// The patient a calculation runs for when none was named. The calculations that cover every
    /// patient who fits need none. A day with no patient named is for the only patient when the
    /// collection holds one, and otherwise for every patient who has a contact that day.
    /// </summary>
    private static string? SinglePatient(Calculations calculations, string calculation)
    {
        if (calculation.ToLowerInvariant() is "consecutive" or "summary" or "patients") return null;
        if (calculation.Equals("day", StringComparison.OrdinalIgnoreCase)) return calculations.OnlyPatient();
        var patients = calculations.ListPatients();
        return patients.Count == 1
            ? patients[0].PatientKey
            : throw new ArgumentException($"The collection holds {patients.Count} patients. Give --patient with one of: {string.Join(", ", patients.Select(p => p.PatientKey))}.");
    }

    private static int Show(Options options)
    {
        using var database = new Database(options.DatabasePath);
        var calculations = new Calculations(database);
        var patient = calculations.Load(options.Get("patient") ?? SinglePatient(calculations, "show")!);

        if (options.Get("encounter") is { } id)
        {
            var e = patient.Encounters.FirstOrDefault(x => string.Equals(x.EncounterId, id, StringComparison.OrdinalIgnoreCase) || x.EventKey == id)
                ?? throw new ArgumentException($"No encounter '{id}' for this patient.");
            Console.WriteLine($"## {e.EncounterId ?? e.EventKey} on {e.ServiceDate:yyyy-MM-dd}\n");
            Console.WriteLine(Markdown.Event(e));
            return 0;
        }

        Console.WriteLine(Timeline.Render(patient));
        return 0;
    }

    /// <summary>
    /// Prints the lines a source reference points to, such as "BH-D005 10" or "BH-D005 L9-10".
    /// The text comes from the copy saved in the database, so the original file is not needed.
    /// </summary>
    private static int Source(Options options)
    {
        // Either the document and the lines as two values, or one value such as "BH-D005 L9-10".
        // An identifier can hold spaces, so everything before the line numbers is the document.
        var reference = string.Join(' ', new[] { options.Positional(0), options.Positional(1) }.Where(p => p is not null));
        var match = System.Text.RegularExpressions.Regex.Match(reference, @"^\s*(?<doc>.+?)\s+L?(?<from>\d+)(?:\s*[-–]\s*L?(?<to>\d+))?\s*$");
        if (!match.Success) throw new ArgumentException("Give a document ID and a line or line range, for example: source BH-D005 10, or source BH-D005 9-10.");

        var id = match.Groups["doc"].Value;
        var from = int.Parse(match.Groups["from"].Value);
        var to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value) : from;
        var context = int.TryParse(options.Get("context"), out var c) && c >= 0 ? c : 1;

        using var database = new Database(options.DatabasePath);
        if (to < from) throw new ArgumentException($"The last line asked for, {to}, comes before the first, {from}.");
        var found = SourceLookup.Find(database, id, from, to, options.Get("patient")?.Trim().ToUpperInvariant())
            ?? throw new ArgumentException($"No document '{id}' in this database. A document is named by its printed identifier, or by the first {SourceLookup.ShortestHashPrefix} or more characters of its hash.");

        var lineWords = from == to ? $"line {from}" : $"lines {from} to {to}";
        if (!found.Shared && (from < 1 || to > found.Documents[0].Lines.Length))
            throw new ArgumentException($"{found.Documents[0].Name} has {found.Documents[0].Lines.Length} lines. Lines {from} to {to} are outside it.");

        Console.WriteLine($"{found.Documents[0].Name}, {lineWords}");
        if (found.Note is { } note) Console.WriteLine(note);
        for (var i = 0; i < found.Documents.Count; i++)
        {
            var (document, _, lines, citing) = found.Documents[i];
            if (found.Shared) Console.WriteLine($"\nDocument {i + 1} of {found.Documents.Count}");
            Console.WriteLine($"File: {document.Path}");
            Console.WriteLine();
            if (from < 1 || to > lines.Length)
            {
                Console.WriteLine($"This document has {lines.Length} lines, so {lineWords} is outside it.");
                continue;
            }
            for (var number = Math.Max(1, from - context); number <= Math.Min(lines.Length, to + context); number++)
            {
                var cited = number >= from && number <= to;
                Console.WriteLine($"{(cited ? ">" : " ")} {number,3}  {lines[number - 1]}");
            }

            Console.WriteLine();
            Console.WriteLine($"Assertions extracted from {(from == to ? "this line" : "these lines")}: {citing.Count}");
            Console.WriteLine();
            Console.WriteLine(Markdown.Table(["Source", "Kind", "Encounter", "Record", "Signed", "What was recorded"],
                citing.Select(a => new[]
                {
                    a.Citation, a.Kind.Replace('_', ' '), a.EncounterId ?? "", a.RecordType.Replace('_', ' ') + (a.IsCopy ? " (copy)" : ""),
                    a.Signed.Replace('_', ' '), Json.Write(a.Fields),
                })));
        }
        return 0;
    }

    private static int Gaps(Options options)
    {
        using var database = new Database(options.DatabasePath);
        var gaps = database.ListGaps();
        Console.WriteLine($"{gaps.Count} passage(s) in the gap log.\n");
        Console.WriteLine(Markdown.Table(["Suggested kind", "Passages", "Questions that led to them"],
            GapLog.Groups(gaps).Select(g => new[] { g.Kind, g.Passages.Count.ToString(), string.Join(" / ", g.Questions.Take(3)) })));
        Console.WriteLine(Markdown.Table(["Source", "Suggested kind", "Search terms", "Passage"],
            gaps.Select(g => new[] { $"{g.PrintedDocId} L{g.Line}", g.SuggestedKind ?? "", g.SearchTerms, g.Passage.Length > 220 ? g.Passage[..220] + " [cut]" : g.Passage })));
        return 0;
    }

    // ---------- export ----------

    private static int Export(Options options)
    {
        using var database = new Database(options.DatabasePath);
        var directory = options.Positional(0) ?? "abstraction/export";
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "assertions.json"), Json.Write(database.GetAssertions(), indented: true));
        File.WriteAllText(Path.Combine(directory, "rejected-assertions.json"), Json.Write(
            database.GetRejected().Select(r => new { document = r.DocHash, r.Reason, r.Kind, r.Critical, returned = r.Json }), indented: true));
        File.WriteAllText(Path.Combine(directory, "documents.json"), Json.Write(
            database.ListDocuments().Select(d => new { d.DocHash, d.RawHash, d.PrintedId, d.Path, d.LineCount, d.ByteSize }), indented: true));
        File.WriteAllText(Path.Combine(directory, "gap-log.json"), Json.Write(
            database.ListGaps().Select(g => new { g.Question, document = g.PrintedDocId, g.Line, g.SuggestedKind, g.SearchTerms, g.Passage }), indented: true));

        foreach (var patient in database.LoadAllAbstractions())
        {
            var name = Safe(patient.PatientKey);
            File.WriteAllText(Path.Combine(directory, $"patient-{name}.json"), Json.Write(patient, indented: true));
            File.WriteAllText(Path.Combine(directory, $"patient-{name}.md"), Timeline.Render(patient));
        }

        Console.WriteLine($"Exported the abstraction to {directory}");
        return 0;
    }

    public static string Safe(string text) => new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    // ---------- questions ----------

    private static async Task<int> AskAsync(Options options)
    {
        using var database = new Database(options.DatabasePath);
        using var log = new Log(options.LogDirectory, "ask");
        var answers = Path.Combine(options.OutputDirectory, "answers");
        Directory.CreateDirectory(answers);

        var questions = new List<(string? Id, string Text)>();
        if (options.Get("file") is { } file)
        {
            foreach (var item in Json.Read<List<QuestionFile>>(File.ReadAllText(file)))
                questions.Add((item.Id, item.Question));
        }
        else
        {
            questions.Add((options.Get("id"), options.Positional(0) ?? throw new ArgumentException("Give a question in quotes, or --file questions.json.")));
        }

        var model = await ModelClient.CreateAsync(database, ModelSettings.FromEnvironment());
        var answerer = new QuestionAnswerer(database, model, log);
        foreach (var (id, text) in questions)
        {
            var answer = await AnswerAndSaveAsync(answerer, answers, id, text, useCache: !options.Has("no-cache"), options.Get("patient"), log);
            if (questions.Count == 1) Console.WriteLine("\n" + answer.Markdown);
        }
        ReportUsage(database, log);
        database.Checkpoint();
        return 0;
    }

    /// <summary>
    /// Answers one question as the ask command does, shared with the review page: the same answerer,
    /// the same reuse of a saved answer, and the answer written to the answers folder.
    /// </summary>
    public static async Task<Answer> AnswerAndSaveAsync(QuestionAnswerer answerer, string answers, string? id, string text, bool useCache, string? patient, Log log, Action<CalculationProgress>? progress = null)
    {
        Directory.CreateDirectory(answers);
        var answer = await answerer.AnswerAsync(id, text, useCache, patient, progress);
        var name = Safe(id ?? $"question-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        File.WriteAllText(Path.Combine(answers, $"{name}.md"), answer.Markdown);
        File.WriteAllText(Path.Combine(answers, $"{name}.json"), answer.Json);
        log.Info($"{id ?? "question"} answered in {answer.LatencyMs} ms{(answer.FromCache ? " (from cache)" : "")}; saved to {Path.Combine(answers, name)}.md");
        return answer;
    }

    private sealed class QuestionFile
    {
        public string? Id { get; set; }
        public string Question { get; set; } = "";
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Clinical abstraction

            Commands that call a model
              ingest <folder>                 Register, extract and reconcile new documents
              ask "<question>"                Answer one question from the saved abstraction
              ask --file questions.json       Answer a set of questions
                                              A question that names no patient is answered for every patient
                                              who fits what it specifies, each under their own heading
                                              Add --patient <key> to answer such questions for one patient instead

            Commands that never call a model
              reconcile                       Rebuild every patient's events from saved assertions
              patients                        List the patients in the collection
              show [--encounter <id>]         A patient's timeline, or one encounter with all its evidence
              source <document> <lines>       The lines a source points to, such as: source BH-D005 10
                                              Add --patient <key> when two documents carry the same identifier
              calc sessions                   Sessions by type, total and distinct days
              calc weekly                     Days, minutes and goal result for each week
              calc consecutive                Patients with two consecutive weeks below the goal
              calc summary                    One row per patient: sessions by type, days, minutes and weeks met
                                              With --from and --to, every patient who has a contact in the period
                                              With no dates, every patient, each over their own episode
              calc patients                   Patients who have a contact between --from and --to, with how many
              calc plan-change                Care delivered before and after a plan change
              calc day --date yyyy-MM-dd      Everything recorded for one day
                                              With several patients and no --patient, every patient who has
                                              a contact that day, grouped by patient
              calc measures                   Symptom assessments in date order
              calc observations               Quoted observations, optionally by --category
              calc search --terms a,b         Keyword search over a patient's documents
              gaps                            What the gap log holds
              export [folder]                 Write the abstraction as JSON and readable text
              benchmark [folder]              Measure timings, model usage and size
              evaluate --reference <file>     Score the abstraction against a file of expected results
              serve [--port <number>]         A review page at http://127.0.0.1:5173 on this machine only,
                                              where every number can be followed back to its document lines

            Options
              --db <file>         Database file (default abstraction/abstraction.db)
              --out <folder>      Output folder (default output)
              --patient <key>     Patient key, normally the MRN. Not taken by calc summary or calc patients
              --from, --to        Dates as yyyy-MM-dd, both included
              --json              Print a calculation result as JSON
              --port <number>     Port for serve (default 5173)

            Environment
              CA_PROVIDER         bedrock or anthropic (default: anthropic when ANTHROPIC_API_KEY is set, else bedrock)
              CA_EXTRACT_MODEL    haiku-4.5, sonnet-5, opus-5, or a full model identifier
              CA_ANSWER_MODEL     as above
              CA_AWS_REGION       Bedrock region (default us-west-2)
              CA_CONCURRENCY      Documents extracted at once (default 6)
            """);
        return 2;
    }
}

/// <summary>Command-line arguments: a command, positional values, and --name value pairs.</summary>
public sealed class Options
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    public Options(string[] args)
    {
        Command = args[0].ToLowerInvariant();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var name = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                _named[name] = hasValue ? args[++i] : "true";
            }
            else
            {
                _positional.Add(args[i]);
            }
        }
    }

    public string? Positional(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Get(string name) => _named.GetValueOrDefault(name);

    public bool Has(string name) => _named.ContainsKey(name);

    public string DatabasePath => Get("db") ?? "abstraction/abstraction.db";

    public string OutputDirectory => Get("out") ?? "output";

    public string LogDirectory => Path.Combine(OutputDirectory, "logs");
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Reconciliation;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reporting;

/// <summary>
/// Measures the saved abstraction and the work done to build it. Everything under "Measured" was
/// timed or counted on this machine. Everything under "Estimated" is arithmetic on those figures.
/// </summary>
public static class Benchmark
{
    private const int Repeats = 25;

    public static async Task<int> RunAsync(Options options)
    {
        var folder = options.Positional(0) ?? "documents";
        using var database = new Database(options.DatabasePath);
        using var log = new Log(options.LogDirectory, "benchmark");
        var calculations = new Calculations(database);
        var b = new StringBuilder();

        b.AppendLine("# Benchmarks\n");
        b.AppendLine($"Run on {DateTime.Now:yyyy-MM-dd HH:mm} against `{options.DatabasePath}`. Machine: {Environment.OSVersion.Platform}, {Environment.ProcessorCount} logical processors, .NET {Environment.Version}.\n");
        b.AppendLine("# Measured\n");

        // ---------- initial processing ----------
        var ingests = database.ListRuns().Where(r => r.Command == "ingest").Select(r => Json.Read<IngestSummary>(r.Json)).ToList();
        var first = ingests.FirstOrDefault(i => i.DocumentsExtracted > 0);
        b.AppendLine("## Initial processing\n");
        if (first is null)
        {
            b.AppendLine("No ingest with extraction is recorded in this database.\n");
        }
        else
        {
            b.AppendLine(Markdown.Table(["Measure", "Value"],
            [
                ["Documents", first.DocumentsExtracted.ToString()],
                ["Total time", Seconds(first.TotalMs)],
                ["Registering and hashing", $"{first.RegisterMs} ms"],
                ["Extraction, wall clock", Seconds(first.ExtractMs)],
                ["Reconciliation", $"{first.ReconcileMs} ms"],
                ["Documents extracted at once", first.Concurrency.ToString()],
                ["Extraction model", first.ExtractionModel],
                ["Assertions kept", first.AssertionsAccepted.ToString()],
                ["Assertions with line numbers corrected", first.QuotesRelocated.ToString()],
                ["Assertions rejected", $"{first.AssertionsRejected} ({first.CriticalRejected} about attendance, presence or the plan)"],
                ["Share of returned assertions whose quote was found", Percent(first.AssertionsAccepted, first.AssertionsAccepted + first.AssertionsRejected)],
            ]));
        }

        // ---------- reuse: ingesting the same folder again ----------
        b.AppendLine("## Reuse across runs\n");
        if (Directory.Exists(folder))
        {
            var callsBefore = database.CountRows()["model_calls"];
            var versionBefore = database.CollectionVersion();
            var pipeline = new IngestPipeline(database, log);
            var again = await pipeline.RunAsync(folder,
                () => ModelClient.CreateAsync(database, ModelSettings.FromEnvironment()),
                patients => new ReconcileService(database).Run(patients));
            b.AppendLine(Markdown.Table(["Measure", "Value"],
            [
                ["Ingesting the same folder again", $"{again.TotalMs} ms"],
                ["Files recognised as already known", $"{again.DuplicateFiles} of {again.FilesSeen}"],
                ["Documents extracted", again.DocumentsExtracted.ToString()],
                ["Model calls made", (database.CountRows()["model_calls"] - callsBefore).ToString()],
                ["Abstraction changed", database.CollectionVersion() == versionBefore ? "No" : "Yes"],
            ]));
        }
        else
        {
            b.AppendLine($"Folder `{folder}` was not found, so re-ingest was not measured.\n");
        }

        var rebuild = Time(() => new ReconcileService(database).Run(), 5);
        b.AppendLine($"Rebuilding every patient's events from saved assertions, with no model: median {rebuild.Median:0.0} ms over 5 runs ({database.ListPatients().Count} patient(s)).\n");

        // ---------- calculations ----------
        b.AppendLine("## Calculation latency\n");
        b.AppendLine($"Each calculation was run {Repeats} times against the saved abstraction. No model is involved.\n");
        var patients = calculations.ListPatients();
        var rows = new List<string[]>();
        if (patients.Count > 0)
        {
            var key = patients[0].PatientKey;
            var date = patients[0].EpisodeStart ?? DateTime.Today.ToString("yyyy-MM-dd");
            void Add(string scope, string name, Action action)
            {
                var t = Time(action, Repeats);
                rows.Add([scope, name, $"{t.Median:0.00} ms", $"{t.Max:0.00} ms"]);
            }
            Add("One patient", "Session counts", () => calculations.SessionCounts(key, null, null));
            Add("One patient", "Minutes and goals by week", () => calculations.Weekly(key, null, null));
            Add("One patient", "Day reconstruction", () => calculations.Day(key, date));
            Add("One patient", "Symptom measures", () => calculations.Measures(key, null));
            Add("One patient", "Observations", () => calculations.Observations(key, null, null, null));
            Add("One patient", "Plan change comparison", () => calculations.PlanChange(key));
            Add("Whole collection", "Consecutive weeks below goal", () => calculations.ConsecutiveWeeks(null, null, null));
        }
        b.AppendLine(Markdown.Table(["Scope", "Calculation", "Median", "Slowest"], rows));

        // ---------- questions ----------
        b.AppendLine("## Question latency\n");
        var answers = database.ListAnswers();
        b.AppendLine(Markdown.Table(["Question", "Time", "Served from saved answer"],
            answers.Select(a => new[] { a.QuestionId ?? (a.Question.Length > 60 ? a.Question[..60] + "..." : a.Question), Seconds(a.LatencyMs), a.FromCache ? "Yes" : "No" })));
        var fresh = answers.Where(a => !a.FromCache).Select(a => (double)a.LatencyMs).Order().ToList();
        if (fresh.Count > 0)
            b.AppendLine($"Questions answered by the model: {fresh.Count}. Median {Seconds((long)fresh[fresh.Count / 2])}, slowest {Seconds((long)fresh[^1])}. Nearly all of this time is the model; the calculations inside each answer take milliseconds.\n");

        // ---------- model usage ----------
        b.AppendLine("## Model usage\n");
        b.AppendLine("Tokens are measured. Cost is the measured tokens priced at Anthropic's list prices; Amazon Bedrock may bill at different rates.\n");
        var usage = database.SummariseModelCalls();
        b.AppendLine(Markdown.Table(["Purpose", "Model", "Calls", "Input tokens", "Cache write", "Cache read", "Output tokens", "Model time", "Cost at list price"],
            usage.Select(u => new[]
            {
                Markdown.Sentence(u.Purpose), u.Model, u.Calls.ToString(), u.InputTokens.ToString("N0"), u.CacheWriteTokens.ToString("N0"), u.CacheReadTokens.ToString("N0"),
                u.OutputTokens.ToString("N0"), Seconds(u.LatencyMs), Dollars(ModelSettings.EstimateCost(u.Model, u.InputTokens, u.OutputTokens, u.CacheWriteTokens, u.CacheReadTokens)),
            })));

        // ---------- size ----------
        database.Checkpoint();
        var size = new FileInfo(database.FilePath).Length;
        var counts = database.CountRows();
        var documents = database.ListDocuments();
        var sourceBytes = documents.Sum(d => d.ByteSize);
        b.AppendLine("## Size of the saved abstraction\n");
        b.AppendLine(Markdown.Table(["Measure", "Value"],
        [
            ["Database file", $"{size:N0} bytes ({size / 1024.0:0} KB)"],
            ["Source documents", $"{documents.Count} documents, {sourceBytes:N0} bytes"],
            ["Database size per source byte", $"{(sourceBytes == 0 ? 0 : size / (double)sourceBytes):0.0}"],
            ["Assertions", counts["assertions"].ToString("N0")],
            ["Assertions per document", $"{(documents.Count == 0 ? 0 : counts["assertions"] / (double)documents.Count):0.0}"],
            ["Events", counts["events"].ToString("N0")],
            ["Stored weekly results", counts["weekly_results"].ToString("N0")],
            ["Patients", counts["patients"].ToString("N0")],
            ["Saved answers", counts["answers"].ToString("N0")],
            ["Gap log entries", counts["gaps"].ToString("N0")],
        ]));
        b.AppendLine("The database also stores each document's text and the model's raw output for every extraction, so that any assertion can be checked against its source without the original files.\n");

        // ---------- estimates ----------
        b.AppendLine("# Estimated\n");
        b.AppendLine("Nothing in this section was measured at scale. Each figure is the measured per-document figure above multiplied out.\n");
        var extraction = usage.Where(u => u.Purpose.StartsWith("extraction")).ToList();
        if (first is not null && extraction.Count > 0 && first.DocumentsExtracted > 0)
        {
            var n = (double)first.DocumentsExtracted;
            var input = extraction.Sum(u => u.InputTokens) / n;
            var write = extraction.Sum(u => u.CacheWriteTokens) / n;
            var read = extraction.Sum(u => u.CacheReadTokens) / n;
            var output = extraction.Sum(u => u.OutputTokens) / n;
            var cost = extraction.Sum(u => ModelSettings.EstimateCost(u.Model, u.InputTokens, u.OutputTokens, u.CacheWriteTokens, u.CacheReadTokens) ?? 0m) / (decimal)n;
            var modelSeconds = extraction.Sum(u => u.LatencyMs) / 1000.0 / n;
            var wallSeconds = first.ExtractMs / 1000.0 / n;

            b.AppendLine("## Per document, from the measured run\n");
            b.AppendLine(Markdown.Table(["Measure", "Value"],
            [
                ["Input tokens at full price", $"{input:N0}"],
                ["Tokens written to the cache", $"{write:N0}"],
                ["Tokens read from the cache", $"{read:N0}"],
                ["Output tokens", $"{output:N0}"],
                ["Cost at list price", $"${cost:0.0000}"],
                ["Model time", $"{modelSeconds:0.0} seconds"],
                ["Wall-clock time at the measured concurrency", $"{wallSeconds:0.0} seconds"],
            ]));

            b.AppendLine("## Extraction at scale\n");
            b.AppendLine(Markdown.Table(["Documents", "Cost at list price", "Time, one at a time", $"Time at {first.Concurrency} at once", "Time at 100 at once", "Assertions"],
                new[] { 500_000, 1_000_000 }.Select(d => new[]
                {
                    d.ToString("N0"), $"${cost * d:N0}", Days(modelSeconds * d), Days(wallSeconds * d), Days(modelSeconds * d / 100),
                    $"{counts["assertions"] / n * d:N0}",
                })));
            b.AppendLine("The times assume the provider's rate limits allow that many requests at once, which was not tested. Processing through a batch interface would trade speed for a lower price and was not measured.\n");
            b.AppendLine($"Database size at the measured {size / n / 1024.0:0.0} KB per document: about {size / n * 500_000 / 1e9:0.0} GB for 500,000 documents and {size / n * 1_000_000 / 1e9:0.0} GB for 1,000,000. SQLite was not tested at that size.\n");
        }

        var text = b.ToString();
        Directory.CreateDirectory(options.OutputDirectory);
        var path = Path.Combine(options.OutputDirectory, "benchmarks.md");
        File.WriteAllText(path, text);
        Console.WriteLine(text);
        log.Info($"Benchmarks written to {path}");
        return 0;
    }

    public static int Evaluate(Options options)
    {
        var referencePath = options.Get("reference") ?? throw new ArgumentException("Give --reference <file>.");
        using var database = new Database(options.DatabasePath);
        var reference = Json.Read<Evaluation.Reference>(File.ReadAllText(referencePath));
        var checks = Evaluation.Score(database, reference);

        var (ingest, table, missing) = IngestFor(database, reference.PatientKey);
        var title = $"Scored against {Path.GetFileName(referencePath)}" + (ingest is null ? "" : $", extracted with {ingest.ExtractionModel}");
        var text = Evaluation.Render(checks, title, Evaluation.NotListed(database, reference));
        text += table ?? $"{missing}\n";

        Console.WriteLine(text);
        if (options.Get("save") is { } save)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(save))!);
            File.WriteAllText(save, text);
        }
        return checks.All(c => c.Passed) ? 0 : 3;
    }

    /// <summary>
    /// The figures for the ingest that added a patient's documents. That is the one recorded ingest
    /// whose running time covers every one of the patient's extractions, and it counts only when no
    /// other patient's document was extracted in it, so that every figure belongs to this patient.
    /// When that cannot be established the table is left out and the sentence says why.
    /// </summary>
    private static (IngestSummary? Ingest, string? Table, string? Missing) IngestFor(Database database, string patientKey)
    {
        static DateTime When(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        var extractions = database.ListExtractions();
        var mine = extractions.Where(e => e.PatientKey == patientKey).ToList();
        if (mine.Count == 0)
            return (null, null, "No extraction of this patient's documents is recorded in this database, so no extraction figures are shown.");

        // Times are recorded to the second, and an ingest's start is worked out from its end, so the
        // window is widened by a second or two either side.
        var runs = database.ListRuns().Where(r => r.Command == "ingest")
            .Select(r => (Summary: Json.Read<IngestSummary>(r.Json), From: When(r.StartedAt).AddSeconds(-1), To: When(r.StartedAt).AddMilliseconds(r.ElapsedMs).AddSeconds(2)))
            .ToList();
        bool Inside(string createdAt, (IngestSummary Summary, DateTime From, DateTime To) run) => When(createdAt) >= run.From && When(createdAt) <= run.To;

        var covering = runs.Where(run => mine.All(e => Inside(e.CreatedAt, run))).ToList();
        if (covering.Count != 1)
            return (null, null, "This patient's documents were not all extracted by one recorded ingest, so the ingest that added the patient cannot be named and no extraction figures are shown.");
        var ingest = covering[0];
        if (extractions.Any(e => e.PatientKey != patientKey && Inside(e.CreatedAt, ingest)))
            return (null, null, "The ingest that added this patient also extracted other patients' documents, so its figures would include theirs. No extraction figures are shown.");

        var documents = mine.Select(e => e.DocHash).ToHashSet();
        var calls = database.ListModelCalls()
            .Where(c => c.Call.Purpose.StartsWith("extraction") && c.Call.Reference is { } r && documents.Contains(r) && Inside(c.CreatedAt, ingest))
            .Select(c => c.Call)
            .ToList();
        var table = Markdown.Table(["Measure", "Value"],
        [
            ["Ingest", $"{ingest.Summary.Folder}, the ingest that added this patient"],
            ["Documents", mine.Count.ToString()],
            ["Assertions kept", mine.Sum(e => e.Assertions).ToString()],
            ["Assertions rejected", mine.Sum(e => e.Rejected).ToString()],
            ["Extraction time, wall clock", Seconds(ingest.Summary.ExtractMs)],
            ["Model calls", calls.Count.ToString()],
            ["Output tokens", calls.Sum(c => c.OutputTokens).ToString("N0")],
            ["Cost at list price", Dollars(calls.Sum(c => ModelSettings.EstimateCost(c.Model, c.InputTokens, c.OutputTokens, c.CacheWriteTokens, c.CacheReadTokens) ?? 0m))],
        ]);
        return (ingest.Summary, table, null);
    }

    private static (double Median, double Max) Time(Action action, int repeats)
    {
        action(); // once to warm up, not counted
        var samples = new List<double>();
        for (var i = 0; i < repeats; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples[samples.Count / 2], samples[^1]);
    }

    private static string Seconds(long milliseconds) => milliseconds < 1000 ? $"{milliseconds} ms" : $"{milliseconds / 1000.0:0.0} s";

    private static string Percent(int part, int whole) => whole == 0 ? "not applicable" : $"{100.0 * part / whole:0.0}%";

    private static string Dollars(decimal? amount) => amount is null ? "unknown model" : $"${amount:0.00}";

    private static string Days(double seconds) => seconds switch
    {
        < 3600 => $"{seconds / 60:0} minutes",
        < 172800 => $"{seconds / 3600:0} hours",
        _ => $"{seconds / 86400:0} days",
    };
}

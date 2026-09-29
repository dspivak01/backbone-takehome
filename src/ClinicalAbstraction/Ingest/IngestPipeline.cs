using System.Diagnostics;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Ingest;

public sealed class IngestSummary
{
    public string Folder { get; set; } = "";
    public int FilesSeen { get; set; }
    public int NewDocuments { get; set; }
    public int DuplicateFiles { get; set; }
    public int DocumentsExtracted { get; set; }
    public int AssertionsAccepted { get; set; }
    public int QuotesRelocated { get; set; }
    public int AssertionsRejected { get; set; }
    public int CriticalRejected { get; set; }
    public int PatientsReconciled { get; set; }
    public long RegisterMs { get; set; }
    public long ExtractMs { get; set; }
    public long ReconcileMs { get; set; }
    public long TotalMs { get; set; }
    public string ExtractionModel { get; set; } = "";
    public int Concurrency { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>What an ingest has just done, reported as it happens so the review page can show each file.</summary>
public enum IngestStep { New, AlreadyKnown, NotText, Extracting, Extracted, Failed, Reconciling }

/// <summary>
/// One step of an ingest. Extraction steps name the document by its hash and by the path it was
/// first seen at, which can be a path from an earlier ingest.
/// </summary>
public sealed record IngestProgress(IngestStep Step, string? Path = null, string? DocHash = null, string? PrintedId = null, int Accepted = 0, int Rejected = 0, Exception? Error = null);

/// <summary>
/// Registers the files in a folder, extracts the ones whose content is new, and rebuilds the
/// events of each patient who received new assertions. Files seen before cost nothing.
/// </summary>
public sealed class IngestPipeline(Database database, Log log)
{
    /// <summary>The files an ingest of a folder reads: every file ending in .txt or with no extension, in every subfolder, except hidden files and anything inside a hidden folder.</summary>
    public static List<string> FilesIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.StartsWith('.')))
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".txt" or "")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    public async Task<IngestSummary> RunAsync(string folder, Func<Task<ModelClient>> modelFactory, Action<IEnumerable<string>?> reconcile, Action<IngestProgress>? progress = null)
    {
        var total = Stopwatch.StartNew();
        var summary = new IngestSummary { Folder = folder };

        // 1. Register every file by content.
        var stage = Stopwatch.StartNew();
        var files = FilesIn(folder);
        var registry = new DocumentRegistry(database);
        var toExtract = new Dictionary<string, StoredDocument>();

        foreach (var file in files)
        {
            // A file named .txt can still hold something else, such as a picture or a word processor
            // file. It is left unread rather than sent to the model as text.
            if (!DocumentRegistry.IsText(file))
            {
                summary.Warnings.Add($"{file} was not read, because it is not a text file.");
                log.Detail($"not text   {file}");
                progress?.Invoke(new IngestProgress(IngestStep.NotText, file));
                continue;
            }

            summary.FilesSeen++;
            var registration = registry.Register(file);
            if (registration.Outcome == RegistrationOutcome.New)
            {
                summary.NewDocuments++;
                log.Detail($"new        {registration.Document.DocHash[..12]}  {file}");
            }
            else
            {
                summary.DuplicateFiles++;
                var how = registration.ByteIdentical ? "same bytes" : "same text after normalising";
                log.Detail($"duplicate  {registration.Document.DocHash[..12]}  {file}  ({how} as {registration.Document.Path})");
            }
            progress?.Invoke(new IngestProgress(registration.Outcome == RegistrationOutcome.New ? IngestStep.New : IngestStep.AlreadyKnown, file, registration.Document.DocHash));

            // A document is extracted when it has no saved result for the current extractor version,
            // or when its last extraction failed or was cut off.
            var saved = database.FindExtraction(registration.Document.DocHash, Vocabulary.ExtractorVersion);
            if (saved is null || saved.Status is "failed" or "truncated")
                toExtract[registration.Document.DocHash] = registration.Document;
        }
        summary.RegisterMs = stage.ElapsedMilliseconds;
        log.Info($"Registered {summary.FilesSeen} files: {summary.NewDocuments} new, {summary.DuplicateFiles} already known. {toExtract.Count} need extraction.");

        // 2. Extract new documents, several at a time.
        stage.Restart();
        var touchedPatients = new HashSet<string>();
        if (toExtract.Count > 0)
        {
            var model = await modelFactory();
            summary.ExtractionModel = model.Settings.ResolveModelId(model.Settings.ExtractionModel);
            summary.Concurrency = model.Settings.Concurrency;
            var extractor = new Extractor(database, model);
            using var limiter = new SemaphoreSlim(model.Settings.Concurrency);
            var gate = new Lock();
            var warmUp = new TaskCompletionSource();

            // The instructions are the same for every document and are cached by the provider. The first
            // document runs alone so the cache exists before the rest start in parallel.
            var queue = toExtract.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
            var tasks = queue.Select(async (document, index) =>
            {
                if (index > 0) await warmUp.Task;
                await limiter.WaitAsync();
                try
                {
                    progress?.Invoke(new IngestProgress(IngestStep.Extracting, document.Path, document.DocHash));
                    var timer = Stopwatch.StartNew();
                    var result = await extractor.ExtractAsync(document);
                    lock (gate)
                    {
                        summary.DocumentsExtracted++;
                        summary.AssertionsAccepted += result.Accepted;
                        summary.QuotesRelocated += result.Relocated;
                        summary.AssertionsRejected += result.Rejected;
                        summary.CriticalRejected += result.CriticalRejected;
                        touchedPatients.UnionWith(result.PatientKeys);
                    }
                    log.Info($"Extracted {result.PrintedId ?? document.DocHash[..12],-10} {result.Accepted,3} assertions, {result.Rejected} rejected, {result.Attempts} attempt(s), {timer.ElapsedMilliseconds} ms  [{Path.GetFileName(document.Path)}]");
                    progress?.Invoke(new IngestProgress(IngestStep.Extracted, document.Path, document.DocHash, result.PrintedId, result.Accepted, result.Rejected));
                }
                catch (Exception e)
                {
                    lock (gate) summary.Warnings.Add($"Extraction failed for {document.Path}: {e.Message}");
                    log.Info($"FAILED     {Path.GetFileName(document.Path)}: {e.Message}");
                    progress?.Invoke(new IngestProgress(IngestStep.Failed, document.Path, document.DocHash, Error: e));
                }
                finally
                {
                    limiter.Release();
                    if (index == 0) warmUp.TrySetResult();
                }
            }).ToList();
            await Task.WhenAll(tasks);
        }
        summary.ExtractMs = stage.ElapsedMilliseconds;

        foreach (var unfinished in database.ListUnfinishedExtractions())
        {
            var name = database.FindDocument(unfinished.DocHash) is { } d ? d.PrintedId ?? Path.GetFileName(d.Path) : unfinished.DocHash[..12];
            summary.Warnings.Add($"Extraction {unfinished.Status} for {name} (patient {unfinished.PatientKey}). Nothing from this document is in the abstraction. It will be tried again on the next ingest.");
        }

        foreach (var (printedId, versions) in database.FindRevisedDocuments())
            summary.Warnings.Add($"Document ID {printedId} appears with {versions} different contents. They are treated as separate documents; check whether one revises the other.");

        // 3. Rebuild events for the patients who received new assertions.
        stage.Restart();
        if (touchedPatients.Count > 0)
        {
            progress?.Invoke(new IngestProgress(IngestStep.Reconciling));
            reconcile(touchedPatients);
            summary.PatientsReconciled = touchedPatients.Count(p => p != "unassigned");
        }
        summary.ReconcileMs = stage.ElapsedMilliseconds;

        summary.TotalMs = total.ElapsedMilliseconds;
        database.RecordRun("ingest", DateTime.UtcNow.AddMilliseconds(-summary.TotalMs), summary.TotalMs, Json.Write(summary));
        return summary;
    }
}

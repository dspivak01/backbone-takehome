using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Web;

/// <summary>
/// The requests that change the database: adding documents and asking a question. Each starts a
/// job that runs the command line's own code, <see cref="Cli.IngestFolderAsync"/> and
/// <see cref="Cli.AnswerAndSaveAsync"/>, so the page does exactly what the ingest and ask commands
/// do. Nothing here depends on the web framework.
/// </summary>
public sealed class WorkApi(Database database, string outputFolder, ModelAccess model, JobStore jobs)
{
    public const int LongestQuestion = 4000;

    public async Task<ModelStatusView> Model()
    {
        var missing = await model.Missing();
        return new ModelStatusView { Ready = missing is null, Missing = missing };
    }

    public JobView Job(string? id)
    {
        var wanted = (id ?? "").Trim();
        if (wanted.Length == 0) throw new RequestException("Name a job by its identifier.");
        return (jobs.Find(wanted) ?? throw new NotFoundException("This job is not known to the server. It may have finished before the server was restarted, in which case its results are already saved.")).View();
    }

    // ---------- questions ----------

    /// <summary>Starts answering a question, as the ask command answers it, with an optional default patient.</summary>
    public async Task<JobView> Ask(string? question, string? patient)
    {
        var text = (question ?? "").Trim();
        if (text.Length == 0) throw new RequestException("Write a question first.");
        if (text.Length > LongestQuestion) throw new RequestException($"The question is longer than {LongestQuestion} characters. Shorten it.");
        var key = string.IsNullOrWhiteSpace(patient) ? null : patient.Trim();
        if (key is not null && database.LoadAbstraction(key) is null)
            throw new RequestException($"No patient with the key \"{key}\" is in this database. Choose one from the list, or none.");
        if (await model.Missing() is { } missing) throw new UnavailableException(missing);

        var job = jobs.Start("ask", $"Question: {text}", alone: false, "", "The question could not be answered.", async job =>
        {
            job.Stage("Waiting for the model to choose the calculations it needs.");
            using var log = new Log(Path.Combine(outputFolder, "logs"), $"ask-{job.Id}");
            var answerer = new QuestionAnswerer(database, await model.Create(database), log);
            var answer = await Cli.AnswerAndSaveAsync(answerer, Path.Combine(outputFolder, "answers"), null, text, useCache: true, key, log, step =>
            {
                job.AddStep(new JobStepView
                {
                    Title = Wording.Calculation(step.Name),
                    Inputs = Wording.CalculationInputs(step.Input),
                    Note = step.HeldBack ? "Held back, because it gave a date before the patients had been listed. The model was asked to list them first."
                        : step.Failed ? "The calculation failed, and the model was told why." : null,
                });
                job.Stage("The model is choosing calculations and writing the answer.");
            });
            Cli.ReportUsage(database, log);
            database.Checkpoint();
            job.Answered(answer.SavedId, answer.FromCache);
        });
        return job.View();
    }

    // ---------- documents ----------

    public const string IngestRunning = "Documents are already being added. Wait until that finishes, then add these.";
    private const string IngestFailed = "The documents could not be added.";

    /// <summary>Starts adding uploaded files, after checking that each is a text file of up to 1 MB.</summary>
    public async Task<JobView> AddFiles(IReadOnlyList<UploadedFile> files)
    {
        Uploads.Check(files);
        if (await model.Missing() is { } missing) throw new UnavailableException(missing);
        return StartIngest($"Adding {Count(files.Count, "file")}", () => Uploads.Save(database, outputFolder, files));
    }

    /// <summary>Starts adding the text files in a folder on this machine. The folder is only read.</summary>
    public async Task<JobView> AddFolder(string? folder)
    {
        var named = (folder ?? "").Trim();
        if (named.Length == 0) throw new RequestException("Name a folder on this machine, for example /Users/me/notes.");
        if (named == "~" || named.StartsWith("~/", StringComparison.Ordinal))
            named = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), named.TrimStart('~').TrimStart('/'));
        if (!Directory.Exists(named)) throw new RequestException($"No folder was found at {Path.GetFullPath(named)}.");

        List<string> files;
        try
        {
            files = IngestPipeline.FilesIn(named);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new RequestException($"The folder {Path.GetFullPath(named)} could not be read. Check that this program may open it.");
        }
        if (files.Count == 0) throw new RequestException($"The folder {Path.GetFullPath(named)} holds no file ending in .txt.");
        if (await model.Missing() is { } missing) throw new UnavailableException(missing);

        return StartIngest($"Adding the text files in {Path.GetFullPath(named)}", () => (named, files.ToDictionary(f => f, f => Path.GetRelativePath(named, f), StringComparer.Ordinal)));
    }

    /// <summary>
    /// Runs the ingest command's work on one folder as a job, with a row for each file. Only one
    /// runs at a time. The files are saved, when they were uploaded, only once the job is sure to run.
    /// </summary>
    private JobView StartIngest(string title, Func<(string Folder, Dictionary<string, string> Names)> prepare)
    {
        var job = jobs.Start("ingest", title, alone: true, IngestRunning, IngestFailed, async job =>
        {
            job.Stage("Saving the files.");
            var (folder, names) = prepare();
            string Name(string? path) => path is null ? "" : names.GetValueOrDefault(path) ?? Path.GetFileName(path);
            job.Stage("Reading the files.");
            using var log = new Log(Path.Combine(outputFolder, "logs"), $"ingest-{job.Id}");
            try
            {
                var summary = await Cli.IngestFolderAsync(database, folder, log, () => model.Create(database), step => Track(job, step, Name));
                job.Summary(Summary(summary), summary.Warnings
                    // A document whose extraction failed has its own row, in plain words. The
                    // pipeline's warning for it carries the provider's message, so it is left out.
                    .Where(w => !w.StartsWith("Extraction failed for ", StringComparison.Ordinal))
                    .Select(w => Wording.FullSentence(w)));
            }
            catch (Exception e)
            {
                job.Unfinished($"Not read, because the documents could not be added. {ModelAccess.Describe(e)}");
                throw;
            }
        });
        return job.View();
    }

    private static void Track(Job job, IngestProgress step, Func<string?, string> name)
    {
        switch (step.Step)
        {
            case IngestStep.New:
                job.File(step.Path!, step.DocHash, name(step.Path), "New", "none", "Not seen before. The model will read it.");
                break;
            case IngestStep.AlreadyKnown:
                job.File(step.Path!, step.DocHash, name(step.Path), "Already known", "counted", "The same text was added before, so it is not read again.");
                break;
            case IngestStep.NotText:
                job.File(step.Path!, null, name(step.Path), "Failed", "not-met", "Not read, because it is not a text file.");
                break;
            case IngestStep.Extracting:
                job.Stage("The model is reading the new documents.");
                job.Document(step.DocHash!, "Extracting", "may-count", "The model is reading it.");
                break;
            case IngestStep.Extracted:
                job.Document(step.DocHash!, "Done", "met",
                    $"{Count(step.Accepted, "assertion")} kept, {step.Rejected} rejected.{(step.PrintedId is null ? "" : $" Its identifier is {step.PrintedId}.")}");
                break;
            case IngestStep.Failed:
                job.Document(step.DocHash!, "Failed", "not-met", $"Not read. {ModelAccess.Describe(step.Error!)} It will be tried again the next time documents are added.");
                break;
            case IngestStep.Reconciling:
                job.Stage("Rebuilding the abstraction for the patients whose documents changed.");
                break;
        }
    }

    private static IEnumerable<string> Summary(IngestSummary s)
    {
        yield return $"{Count(s.FilesSeen, "file")} read: {s.NewDocuments} new, {s.DuplicateFiles} already known.";
        if (s.DocumentsExtracted > 0)
            yield return $"The model read {Count(s.DocumentsExtracted, "document")}: {Count(s.AssertionsAccepted, "assertion")} kept, {s.AssertionsRejected} rejected.";
        else if (string.IsNullOrEmpty(s.ExtractionModel))
            yield return "No model was called, because no document needed reading.";
        if (s.PatientsReconciled > 0)
            yield return $"The abstraction was rebuilt for {Count(s.PatientsReconciled, "patient")}.";
    }

    private static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

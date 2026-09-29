using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;
using ClinicalAbstraction.Web;
using Microsoft.AspNetCore.Builder;

namespace ClinicalAbstraction.Tests;

/// <summary>
/// The Ask, Documents and Gap log screens' requests, and the safety rules for requests that change
/// the database. No test calls a model: the server is given a stand-in that either says no model is
/// configured or refuses to make one. They share the console collection because jobs run in the
/// background and write to the console.
/// </summary>
[Collection("Console")]
public class StageTwoTests
{
    private static readonly DateOnly Monday = new(2030, 3, 4);

    /// <summary>A stand-in model that is configured but must never be created. It counts attempts.</summary>
    private sealed class NoModel
    {
        public int Created;
        public TaskCompletionSource<ModelClient>? Hold;

        public ModelAccess Access => new(() => Task.FromResult<string?>(null), _ =>
        {
            Interlocked.Increment(ref Created);
            return Hold?.Task ?? throw new InvalidOperationException("No model is called in these tests.");
        });
    }

    /// <summary>A server on a free port with its own output folder, removed afterwards.</summary>
    private sealed class Served : IAsyncDisposable
    {
        public string Output { get; } = Path.Combine(Path.GetTempPath(), $"stage2-test-{Guid.NewGuid():N}");
        public WebApplication App { get; }
        public HttpClient Client { get; }
        public string Address { get; }

        private Served(WebApplication app, string output)
        {
            App = app;
            Output = output;
            Address = app.Urls.Single();
            Client = new HttpClient { BaseAddress = new Uri(Address) };
        }

        public static async Task<Served> Start(Database database, ModelAccess model)
        {
            var output = Path.Combine(Path.GetTempPath(), $"stage2-test-{Guid.NewGuid():N}");
            var app = Server.Build(database, output, 0, model);
            await app.StartAsync();
            return new Served(app, output);
        }

        public Task<HttpResponseMessage> Post(string path, HttpContent content, string? origin = "self")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            if (origin is not null) request.Headers.Add("Origin", origin == "self" ? Address.TrimEnd('/') : origin);
            return Client.SendAsync(request);
        }

        public async Task<JsonNode> Json(string path)
        {
            var response = await Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {body}");
            return JsonNode.Parse(body)!;
        }

        /// <summary>Asks for a job's progress until it ends, as the page does.</summary>
        public async Task<JsonNode> Finished(string id)
        {
            for (var i = 0; i < 200; i++)
            {
                var job = await Json($"/api/jobs?id={id}");
                if (!(bool)job["running"]!) return job;
                await Task.Delay(50);
            }
            throw new TimeoutException("The job did not finish within ten seconds.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (Directory.Exists(Output)) Directory.Delete(Output, recursive: true);
        }
    }

    private static StringContent Question(string question, string? patient = null) =>
        new(JsonSerializer.Serialize(new { question, patient }), Encoding.UTF8, "application/json");

    private static MultipartFormDataContent Files(params (string Name, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(part, "files", name);
        }
        return form;
    }

    private static MultipartFormDataContent Folder(string folder) => new() { { new StringContent(folder), "folder" } };

    private static async Task<string> Error(HttpResponseMessage response) => (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;

    // ---------- requests that change something come only from the page ----------

    [Fact]
    public async Task A_request_that_changes_something_is_refused_unless_it_comes_from_the_page_itself()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var model = new NoModel();
        await using var served = await Served.Start(scratch.Database, model.Access);
        var port = new Uri(served.Address).Port;

        foreach (var origin in new string?[] { null, "http://evil.example", "null", $"http://127.0.0.1:{port + 1}", $"https://127.0.0.1:{port}", $"http://127.0.0.1:{port}, http://evil.example" })
        {
            foreach (var (path, content) in new (string, HttpContent)[] { ("/api/ask", Question("How many sessions?")), ("/api/documents", Folder(Path.GetTempPath())) })
            {
                var refused = await served.Post(path, content, origin);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal(Server.RefusedOrigin, await Error(refused));
                Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
            }
        }

        // From the page itself, by either of the names it is served under, the request is read.
        var own = await served.Post("/api/ask", Question(" "));
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        Assert.Equal("The question could not be sent. Write a question first.", await Error(own));
        var local = await served.Post("/api/ask", Question(" "), $"http://localhost:{port}");
        Assert.Equal(HttpStatusCode.BadRequest, local.StatusCode);

        // A form, which a page elsewhere can send without asking the browser first, is not read as a question.
        var form = await served.Post("/api/ask", new FormUrlEncodedContent([new("question", "How many sessions?")]));
        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
        Assert.Equal(0, model.Created);
    }

    // ---------- uploads ----------

    [Fact]
    public void Uploads_are_text_files_of_up_to_1_MB_saved_under_names_the_server_makes()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var text = Encoding.UTF8.GetBytes("Progress note\nSeen 09:00 to 09:50\n");

        Assert.Contains("larger than 1 MB", Assert.Throws<RequestException>(() => Uploads.Check([new("big.txt", new byte[Uploads.MaxBytes + 1])])).Message);
        Assert.Contains("is not a text file", Assert.Throws<RequestException>(() => Uploads.Check([new("note.txt", [.. text, 0, .. text])])).Message);
        Assert.Contains("does not end in .txt", Assert.Throws<RequestException>(() => Uploads.Check([new("scan.pdf", text)])).Message);
        Assert.Contains("is empty", Assert.Throws<RequestException>(() => Uploads.Check([new("empty.txt", [])])).Message);
        Assert.Throws<RequestException>(() => Uploads.Check([]));
        // One bad file refuses the whole upload.
        Assert.Throws<RequestException>(() => Uploads.Check([new("good.txt", text), new("bad.txt", [0])]));
        Uploads.Check([new("exactly-1-MB.txt", Enumerable.Repeat((byte)'a', Uploads.MaxBytes).ToArray()), new("no-extension", text)]);

        var root = Path.Combine(Path.GetTempPath(), $"stage2-test-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "output");
        try
        {
            var (folder, names) = Uploads.Save(scratch.Database, output, [new("../../x.txt", text), new("/tmp/evil.txt", text), new("..\\..\\y.txt", text)]);

            Assert.StartsWith(Path.Combine(output, "uploads") + Path.DirectorySeparatorChar, folder);
            Assert.Equal(["001.txt", "002.txt", "003.txt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
            Assert.False(File.Exists(Path.Combine(root, "x.txt")) || File.Exists(Path.Combine(output, "x.txt")));
            Assert.Equal(["../../x.txt", "/tmp/evil.txt", "..\\..\\y.txt"], names.Values);
            Assert.Equal(names, Uploads.OriginalNames(scratch.Database));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_over_1_MB_or_not_text_is_refused_by_the_server_before_anything_is_saved()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var model = new NoModel();
        await using var served = await Served.Start(scratch.Database, model.Access);

        var big = await served.Post("/api/documents", Files(("big.txt", new byte[Uploads.MaxBytes + 500_000])));
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        Assert.Equal("The documents could not be added. Nothing was added. \"big.txt\" is larger than 1 MB. Only text files ending in .txt, of up to 1 MB each, can be added.", await Error(big));

        var binary = await served.Post("/api/documents", Files(("scan.txt", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01])));
        Assert.Equal(HttpStatusCode.BadRequest, binary.StatusCode);
        Assert.Contains("\"scan.txt\" is not a text file", await Error(binary));

        var folder = await served.Post("/api/documents", Folder("/no/such/folder/anywhere"));
        Assert.Equal("The documents could not be added. No folder was found at /no/such/folder/anywhere.", await Error(folder));

        Assert.False(Directory.Exists(served.Output));
        Assert.Equal(0, model.Created);
    }

    [Fact]
    public async Task A_document_added_again_is_already_known_and_no_model_is_made()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var text = "Document ID: KNOWN-1\nProgress note\nSeen 09:00 to 09:50\n";
        var file = Path.Combine(Path.GetTempPath(), $"known-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, text);
        try
        {
            var registration = new DocumentRegistry(scratch.Database).Register(file);
            scratch.Database.SaveExtraction(new ExtractionRecord(registration.Document.DocHash, Vocabulary.ExtractorVersion, "none", "complete", null, 0, 0, "P-K"), "{}", [], []);

            var model = new NoModel();
            await using var served = await Served.Start(scratch.Database, model.Access);
            var started = await served.Post("/api/documents", Files(("../../x.txt", Encoding.UTF8.GetBytes(text))));
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var job = await served.Finished((string)JsonNode.Parse(await started.Content.ReadAsStringAsync())!["id"]!);

            Assert.False((bool)job["failed"]!, job.ToJsonString());
            var row = Assert.Single(job["files"]!.AsArray())!;
            Assert.Equal(("../../x.txt", "Already known"), ((string)row["name"]!, (string)row["status"]!));
            Assert.Contains("No model was called, because no document needed reading.", job["summary"]!.AsArray().Select(s => (string)s!));
            Assert.Equal(0, model.Created);

            // The copy was saved under a made-up name inside the output folder, and the document page names it by the name it arrived with.
            var saved = Assert.Single(Directory.GetFiles(served.Output, "*.txt", SearchOption.AllDirectories));
            Assert.Equal("001.txt", Path.GetFileName(saved));
            var page = new LibraryApi(scratch.Database).Document(registration.Document.DocHash);
            Assert.Equal(["../../x.txt (added from this page)"], page.AlsoSeenAs);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---------- jobs ----------

    [Fact]
    public async Task Only_one_ingest_runs_at_a_time_and_a_job_that_fails_says_what_failed_in_plain_words()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var model = new NoModel { Hold = new TaskCompletionSource<ModelClient>(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var served = await Served.Start(scratch.Database, model.Access);

        // The first ingest waits for the model, which the test holds back.
        var first = await served.Post("/api/documents", Files(("new-note.txt", Encoding.UTF8.GetBytes("A note never seen before.\n"))));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var id = (string)JsonNode.Parse(await first.Content.ReadAsStringAsync())!["id"]!;
        for (var i = 0; i < 100 && model.Created == 0; i++) await Task.Delay(20);
        var running = await served.Json($"/api/jobs?id={id}");
        Assert.True((bool)running["running"]!);
        Assert.StartsWith("Running for ", (string)running["elapsed"]!);
        Assert.Equal("New", (string)running["files"]![0]!["status"]!);

        // A second one is refused while it runs, and nothing of it is saved.
        var second = await served.Post("/api/documents", Files(("other.txt", Encoding.UTF8.GetBytes("Another note.\n"))));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(WorkApi.IngestRunning, await Error(second));
        Assert.Single(Directory.GetFiles(served.Output, "*.txt", SearchOption.AllDirectories));

        // The model cannot be reached. The job says so in plain words, without the provider's message.
        model.Hold.SetException(new HttpRequestException("Response status code 503 (ServiceUnavailable) ThrottlingException req-7f3a"));
        var failed = await served.Finished(id);
        Assert.True((bool)failed["failed"]!);
        var error = (string)failed["error"]!;
        Assert.StartsWith("The documents could not be added.", error);
        Assert.Contains("The model could not be reached. Check the network connection, then try again.", error);
        Assert.DoesNotContain("503", error);
        Assert.DoesNotContain("Throttling", error);
        Assert.Equal("Failed", (string)failed["files"]![0]!["status"]!);

        // With the first finished, another can start.
        model.Hold = null;
        var third = await served.Post("/api/documents", Files(("other.txt", Encoding.UTF8.GetBytes("Another note.\n"))));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        await served.Finished((string)JsonNode.Parse(await third.Content.ReadAsStringAsync())!["id"]!);

        var unknown = await served.Client.GetAsync("/api/jobs?id=not-a-job");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_job_store_reports_progress_refuses_a_second_job_of_a_kind_that_runs_alone_and_forgets_only_finished_jobs()
    {
        var jobs = new JobStore();
        var release = new TaskCompletionSource();
        var job = jobs.Start("ingest", "Adding 1 file", alone: true, "Busy.", "It failed.", async j =>
        {
            j.Stage("Reading the files.");
            j.File("a.txt", "hash-a", "a.txt", "New", "none", "Not seen before.");
            await release.Task;
            j.Document("hash-a", "Done", "met", "3 assertions kept, 0 rejected.");
        });
        for (var i = 0; i < 100 && job.View().Stage != "Reading the files."; i++) await Task.Delay(10);

        Assert.Equal(("Running", "Reading the files.", "Adding 1 file"), (job.View().State, job.View().Stage, job.View().Title));
        Assert.Equal("Busy.", Assert.Throws<ConflictException>(() => jobs.Start("ingest", "Again", alone: true, "Busy.", "It failed.", _ => Task.CompletedTask)).Message);
        var other = jobs.Start("ask", "Question", alone: false, "", "It failed.", _ => throw new InvalidOperationException("internal detail"));

        // Many finished jobs later, the running one is still remembered.
        for (var i = 0; i < JobStore.Kept + 5; i++) jobs.Start("ask", "Question", alone: false, "", "It failed.", _ => Task.CompletedTask);
        await Task.Delay(100);
        Assert.Same(job, jobs.Find(job.Id));

        release.SetResult();
        for (var i = 0; i < 100 && job.Running; i++) await Task.Delay(10);
        var done = job.View();
        Assert.Equal(("Finished", "Done"), (done.State, done.Files.Single().Status));
        Assert.StartsWith("Took ", done.Elapsed);

        // A failure that is not this program's own says only that something unexpected went wrong.
        Assert.Equal("It failed. Something unexpected went wrong. The details are printed in the window where the server was started.", other.View().Error);
    }

    // ---------- no model ----------

    [Fact]
    public async Task With_no_model_Ask_and_Documents_say_what_is_missing_and_everything_else_still_answers()
    {
        using var scratch = new ReviewApiTests.Scratch();
        scratch.Add("P-S", "Sam Example", [Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17", episodeStart: "2030-03-04", episodeEnd: "2030-03-17"),
            Build.Encounter("NOTE-E1", "individual_therapy", encounter: "E1", date: Monday), Build.Contact("NOTE-E1", "09:00", "09:50", encounter: "E1", date: Monday)]);
        scratch.Reconcile();
        const string Missing = "No model is configured, because no AWS credentials were found.";
        await using var served = await Served.Start(scratch.Database, ModelAccess.None(Missing));

        var model = await served.Json("/api/model");
        Assert.False((bool)model["ready"]!);
        Assert.Equal(Missing, (string)model["missing"]!);

        var ask = await served.Post("/api/ask", Question("How many sessions?"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ask.StatusCode);
        Assert.Equal(Missing, await Error(ask));
        var add = await served.Post("/api/documents", Files(("note.txt", Encoding.UTF8.GetBytes("A note.\n"))));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, add.StatusCode);
        Assert.Equal(Missing, await Error(add));
        Assert.False(Directory.Exists(served.Output));

        foreach (var path in new[] { "/api/status", "/api/patients", "/api/patient/weekly?key=P-S", "/api/gaps", "/api/documents", "/api/answers", "/api/collection/summary" })
            await served.Json(path);
    }

    [Fact]
    public void Failures_reaching_the_model_are_described_by_what_to_do_and_never_by_the_provider_message()
    {
        Assert.Equal("The model could not be reached. Check the network connection, then try again.",
            ModelAccess.Describe(new AggregateException(new HttpRequestException("Name or service not known (bedrock-runtime.us-west-2.amazonaws.com:443)"))));
        Assert.Equal("Something unexpected went wrong. The details are printed in the window where the server was started.",
            ModelAccess.Describe(new InvalidOperationException("ValidationException: model id us.anthropic.x not found")));
        Assert.Equal("Write a question first.", ModelAccess.Describe(new RequestException("Write a question first.")));
    }

    // ---------- documents ----------

    [Fact]
    public void The_documents_list_and_a_document_page_show_each_document_and_the_assertions_from_each_line()
    {
        using var copy = new ReviewApiTests.Copy("output/experiments/four-patients/collection.db");
        var library = new LibraryApi(copy.Database);

        var list = library.Documents();
        Assert.Equal(copy.Database.ListDocuments().Count, list.Documents.Count);
        var row = list.Documents.Single(d => d.Identifier == "BH-D101");
        Assert.Equal(("BH-D101_group_content_2026-01-19.txt", "HG-M042", "Rowan Mercer (HG-M042)", 9, 0, "Complete"),
            (row.FileName, row.PatientKey, row.Patient, row.Kept, row.Rejected, row.Status));
        Assert.All(list.Documents, d => Assert.Equal(d.Kept, copy.Database.GetAssertions().Count(a => a.DocHash == d.Id)));

        var page = library.Document(row.Id);
        var text = copy.Database.ListDocuments().Single(d => d.DocHash == row.Id).Text.Split('\n');
        Assert.Equal(text, page.Lines.Select(l => l.Text));
        Assert.Equal(Enumerable.Range(1, text.Length), page.Lines.Select(l => l.Number));
        Assert.Equal(9, page.Statements.Count);

        // Each marked line lists the same assertions step 4 of the trace lists for it.
        var review = new ReviewApi(copy.Database, Path.GetTempPath());
        foreach (var line in page.Lines)
        {
            var trace = review.Source(row.Id, line.Number.ToString(), line.Number.ToString(), "0", "HG-M042");
            Assert.Equal(trace.Statements.Select(s => s.Source), line.Statements.Select(i => page.Statements[i].Source));
        }
        Assert.Contains(page.Lines, l => l.Statements.Count > 1);

        Assert.Throws<NotFoundException>(() => library.Document("0000000000"));
        Assert.Throws<RequestException>(() => library.Document(" "));
        Assert.Equal("BH-D101", library.Document("bh-d101").Identifier);
    }

    // ---------- the gap log ----------

    [Fact]
    public void The_gap_log_groups_passages_by_the_kind_suggested_as_the_gaps_command_does()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var library = new LibraryApi(scratch.Database);
        Assert.StartsWith("The gap log is empty.", library.Gaps().Empty);

        scratch.Database.RecordGap("Why was the session cut short?", "P-G", "HASH-1", "DOC-1", 4, "Left early: <b>fire alarm</b>", "left, early", "attendance_reason");
        scratch.Database.RecordGap("Why did the patient leave?", "P-G", "HASH-1", "DOC-1", 9, "Patient left at 10:40", "left", "attendance_reason");
        scratch.Database.RecordGap("Why was the session cut short?", "P-G", "HASH-2", null, 2, "Alarm", "alarm", null);

        var gaps = library.Gaps();
        Assert.Null(gaps.Empty);
        Assert.Equal(3, gaps.Passages);
        Assert.Equal(["Attendance reason", "No kind suggested"], gaps.Groups.Select(g => g.Kind));
        var first = gaps.Groups[0];
        Assert.Equal(["Why was the session cut short?", "Why did the patient leave?"], first.Questions);
        Assert.Equal([("DOC-1 L4", "P-G", "left, early", "Left early: <b>fire alarm</b>"), ("DOC-1 L9", "P-G", "left", "Patient left at 10:40")],
            first.Passages.Select(p => (p.Source, p.PatientKey, p.SearchTerms, p.Passage)));
        Assert.Equal("HASH-2 L2", gaps.Groups[1].Passages.Single().Source);

        // The gaps command groups them the same way.
        var printed = Command.Run("gaps", "--db", scratch.Database.FilePath);
        Assert.Contains("3 passage(s) in the gap log.", printed);
        Assert.Contains("| attendance_reason | 2 | Why was the session cut short? / Why did the patient leave? |", printed);
        Assert.Contains("| not suggested | 1 | Why was the session cut short? |", printed);
    }

    // ---------- answers ----------

    [Fact]
    public void A_default_patient_is_added_to_a_question_exactly_as_before_so_saved_answers_are_still_found()
    {
        Assert.Equal("How many sessions?", QuestionAnswerer.Asked("How many sessions?", null));
        Assert.Equal("How many sessions?\n\n(Default patient set by the person asking: HG-M042. Use it if the question names no patient, and say that you did.)",
            QuestionAnswerer.Asked("How many sessions?", "HG-M042"));
        Assert.Equal(("How many sessions?", "HG-M042"), QuestionAnswerer.ReadAsked(QuestionAnswerer.Asked("How many sessions?", "HG-M042")));
        Assert.Equal(("How many sessions?", (string?)null), QuestionAnswerer.ReadAsked("How many sessions?"));
    }

    [Fact]
    public void Earlier_questions_are_listed_newest_first_and_each_opens_in_its_three_parts_with_the_command_line_tables()
    {
        using var copy = new ReviewApiTests.Copy("output/experiments/four-patients/collection.db");
        var library = new LibraryApi(copy.Database);

        var list = library.Answers();
        var saved = copy.Database.ListAnswers();
        Assert.Equal(saved.Count, list.Total);
        Assert.Equal(list.Answers.Select(a => a.Id).OrderDescending(), list.Answers.Select(a => a.Id));
        Assert.Equal(saved.Count(a => a.FromCache), list.Answers.Count(a => a.FromSaved));
        Assert.All(list.Answers.Where(a => a.FromSaved), a => Assert.Equal("Served from a saved answer", a.How));
        Assert.Contains(list.Answers, a => a.Patient == "WD-40815" && !a.Question.Contains("Default patient"));

        foreach (var row in list.Answers)
        {
            var answer = library.Answer(row.Id.ToString());
            var stored = copy.Database.FindAnswer(row.Id)!.Value;
            var record = Json.Read<AnswerRecord>(stored.Json);
            Assert.Null(answer.Note);
            Assert.Equal(QuestionAnswerer.CheckRows(record.Checks).Select(r => (r[0], r[1])), answer.Checks.Select(c => (c.Check, c.Result)));
            Assert.Equal(record.Checks.Passed, answer.Warning is null);
            Assert.Equal(QuestionAnswerer.NarrativeBy, answer.NarrativeBy);

            // Every table row the command line printed for each calculation is a row of the page's table, cell for cell.
            var tabled = QuestionAnswerer.Tabled(record).ToList();
            Assert.Equal(tabled.Count, answer.Calculations.Count);
            for (var i = 0; i < tabled.Count; i++)
            {
                var printedRows = tabled[i].Rendered.Split('\n').Where(l => l.StartsWith('|') && !l.StartsWith("|---")).ToList();
                var shownRows = answer.Calculations[i].Blocks.Where(b => b.Kind == "table")
                    .SelectMany(b => new[] { b.Headers! }.Concat(b.Rows!))
                    .Select(cells => "| " + string.Join(" | ", cells.Select(c => string.Concat(c.Select(s => s.Text)).Replace("|", "\\|"))) + " |")
                    .ToList();
                Assert.Equal(printedRows.Select(r => r.TrimEnd()), shownRows);
                Assert.True(stored.Markdown.Contains(tabled[i].Rendered), "The answer file holds the calculation's rendering unchanged.");
            }
        }

        // Answer 23's checks found a number the results do not hold. The warning names it.
        var warned = library.Answer("23");
        Assert.Contains("(140)", warned.Warning);
        Assert.Throws<NotFoundException>(() => library.Answer("99999"));
        Assert.Throws<RequestException>(() => library.Answer("abc"));
    }

    [Fact]
    public void Markdown_from_a_model_is_divided_into_parts_and_anything_else_stays_as_written()
    {
        var blocks = MarkdownBlocks.Read(
            "<script>alert(1)</script> and <img src=x onerror=alert(1)>\n" +
            "## Weekly **totals**\n" +
            "Total: **140** minutes from `weekly` and *not met* [BH-D005 L10]. [a link](https://example.com)\n" +
            "\n" +
            "- first\n" +
            "  - nested one\n" +
            "  - nested two\n" +
            "- second\n" +
            "\n" +
            "1. one\n" +
            "2. two\n" +
            "\n" +
            "| Week | Minutes |\n" +
            "|---|---:|\n" +
            "| a \\| b | 140 |\n" +
            "```\n<b>code</b>\n```");

        Assert.Equal(["paragraph", "heading", "paragraph", "list", "list", "table", "code"], blocks.Select(b => b.Kind));
        Assert.Equal("<script>alert(1)</script> and <img src=x onerror=alert(1)>", blocks[0].Spans!.Single().Text);
        Assert.Equal((2, "Weekly totals"), (blocks[1].Level!.Value, string.Concat(blocks[1].Spans!.Select(s => s.Text))));
        var inline = blocks[2].Spans!;
        Assert.Equal(["Total: ", "140", " minutes from ", "weekly", " and ", "not met", " [BH-D005 L10]. [a link](https://example.com)"], inline.Select(s => s.Text));
        Assert.True(inline[1].Bold == true && inline[3].Code == true && inline[5].Italic == true);
        var list = blocks[3];
        Assert.Equal(["first", "second"], list.Items!.Select(i => i.Spans.Single().Text));
        Assert.Equal(["nested one", "nested two"], list.Items![0].Children!.Select(i => i.Spans.Single().Text));
        Assert.True(blocks[4].Ordered);
        Assert.Equal(["a | b", "140"], blocks[5].Rows!.Single().Select(c => c.Single().Text));
        Assert.Equal("<b>code</b>", blocks[6].Text);
    }

    // ---------- which files a folder ingest reads ----------

    [Fact]
    public void A_folder_ingest_reads_nothing_inside_a_hidden_folder()
    {
        var folder = Directory.CreateTempSubdirectory("ca-files-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".aws"));
            Directory.CreateDirectory(Path.Combine(folder, "notes", ".cache"));
            File.WriteAllText(Path.Combine(folder, ".aws", "credentials"), "secret");
            File.WriteAllText(Path.Combine(folder, "notes", ".cache", "old.txt"), "hidden");
            File.WriteAllText(Path.Combine(folder, ".hidden.txt"), "hidden");
            File.WriteAllText(Path.Combine(folder, "notes", "visit.txt"), "kept");
            File.WriteAllText(Path.Combine(folder, "roster"), "kept");

            var read = IngestPipeline.FilesIn(folder).Select(path => Path.GetRelativePath(folder, path)).ToList();

            Assert.Equal([Path.Combine("notes", "visit.txt"), "roster"], read);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---------- reads while an ingest writes ----------

    [Fact]
    public void Nothing_the_new_screens_read_changes_the_database()
    {
        using var copy = new ReviewApiTests.Copy("output/experiments/four-patients/collection.db");
        var before = copy.Hash();
        var library = new LibraryApi(copy.Database);
        library.Documents();
        library.Document(library.Documents().Documents[0].Id);
        library.Gaps();
        library.Answers();
        library.Answer("58");
        copy.Database.Checkpoint();
        Assert.Equal(before, copy.Hash());
    }
}

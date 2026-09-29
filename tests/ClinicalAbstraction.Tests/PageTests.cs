using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Web;

namespace ClinicalAbstraction.Tests;

/// <summary>A test that runs the page's own script in Node. It is skipped, and says why, where Node is not installed.</summary>
public sealed class PageFactAttribute : FactAttribute
{
    public PageFactAttribute()
    {
        if (PageTests.Node is null) Skip = "Node.js is not on the PATH, so the page's own script cannot be run here.";
    }
}

/// <summary>
/// Runs the review page's own script against a running server, through page-harness.js, which
/// stands in for the browser's document. It follows every link the page offers, as a reviewer
/// clicking through would, and reports what each screen showed.
/// </summary>
public class PageTests
{
    internal static readonly string? Node = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(d => new[] { Path.Combine(d, "node"), Path.Combine(d, "node.exe") })
        .FirstOrDefault(File.Exists);

    private static readonly DateOnly Monday = new(2030, 3, 4);

    private const string Key = "../P #1?&%\"'é/Ω|1990";
    private const string Name = "<img src=x onerror=alert(1)>Åsa \"Nordström\"";
    private static readonly string[] Encounters = ["..", ".", "E 1/2#?&%\"'", "Ж-101", "%2F", "<b>x</b>"];

    [PageFact]
    public async Task Every_link_on_the_page_opens_whatever_characters_the_identifiers_hold()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var assertions = new List<Assertion> { Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17", episodeStart: "2030-03-04", episodeEnd: "2030-03-17") };
        for (var i = 0; i < Encounters.Length; i++)
        {
            var date = Monday.AddDays(i % 5);
            var category = i == 1 ? "medication_management" : "individual_therapy";
            assertions.Add(Build.Encounter($"NOTE-{i}", category, encounter: Encounters[i], date: date));
            assertions.Add(Build.Contact($"NOTE-{i}", "09:00", "09:50", encounter: Encounters[i], date: date));
        }
        scratch.Add(Key, Name, [.. assertions]);
        foreach (var hash in assertions.Select(a => a.DocHash).Distinct())
            scratch.AddDocument(hash, hash, $"notes/<script>alert(1)</script> {hash}.txt", string.Join('\n', Enumerable.Range(1, 5).Select(n => $"<i>line {n}</i> of {hash}")));
        scratch.Reconcile();

        await using var app = Server.Build(scratch.Database, Path.GetTempPath(), 0);
        await app.StartAsync();
        JsonNode result;
        try
        {
            result = Run(app.Urls.Single(), "crawl", "#/patients", "400");
        }
        finally
        {
            await app.StopAsync();
        }

        Assert.Null(result["failure"]);
        var pages = result["pages"]!.AsArray().Select(p => p!).ToList();
        Assert.True(pages.Count > 20, $"Only {pages.Count} pages were reached.");
        Assert.Empty(result["consoleErrors"]!.AsArray());

        // Every patient, encounter and source opened: no screen showed a failure.
        foreach (var page in pages)
            Assert.True(page["errors"]!.AsArray().Count == 0, $"{page["hash"]}: {page["errors"]}");
        Assert.All(Encounters, id => Assert.Contains(pages, p => ((string)p["text"]!).Contains($"Every assertion linked to this encounter") && ((string)p["hash"]!).Contains("encounter=")));
        foreach (var page in pages.Where(p => ((string)p["hash"]!).Contains("&doc=")))
            Assert.True(page["citedLines"]!.AsArray().Count > 0, $"{page["hash"]} shows no cited line.");

        // A source inside a sentence is a link too: the plan's source is linked in the list of the
        // goal's sources and in the sentence saying the plan excludes medication management.
        var planSource = $"{Key}-PLAN L1";
        Assert.Contains(pages, p => ((string)p["hash"]!).Contains("week=2030-03-04") && p["links"]!.AsArray().Count(l => (string)l!["text"]! == planSource) >= 2);

        // A week with no appointment says so once, and nothing contradicts it.
        var empty = pages.First(p => ((string)p["hash"]!).Contains("week=2030-03-11"));
        Assert.Contains("No appointment or contact is recorded in this week.", (string)empty["text"]!);
        Assert.DoesNotContain("Every appointment in this week was counted.", (string)empty["text"]!);

        // Markup from the data is only ever text.
        Assert.Contains(pages, p => ((string)p["text"]!).Contains(Name));
        var created = result["created"]!.AsArray().Select(t => (string)t!).ToList();
        Assert.DoesNotContain("img", created);
        Assert.DoesNotContain("script", created);
        Assert.DoesNotContain("b", created);
        Assert.DoesNotContain("i", created);
    }

    [PageFact]
    public async Task The_Ask_Documents_and_Gap_log_screens_open_and_show_everything_as_text_with_sources_as_links()
    {
        using var scratch = new ReviewApiTests.Scratch();
        const string Patient = "P-A";
        scratch.Add(Patient, Name,
            Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17", episodeStart: "2030-03-04", episodeEnd: "2030-03-17"),
            Build.Encounter("NOTE-E1", "individual_therapy", encounter: "E1", date: Monday),
            Build.Contact("NOTE-E1", "09:00", "09:50", encounter: "E1", date: Monday));
        foreach (var hash in new[] { "P-A-PLAN", "P-A-NOTE-E1" })
            scratch.AddDocument(hash, hash, $"notes/{hash}.txt", string.Join('\n', Enumerable.Range(1, 5).Select(n => $"<i>line {n}</i> of {hash}")));
        scratch.Reconcile();

        // A saved answer whose written part holds markup, a source the results do not hold, and
        // Markdown; its calculation is the weekly one, rendered as the command line renders it.
        const string Question = "How many minutes in the week of March 4? <img src=x onerror=alert(1)>";
        const string Narrative = "## Minutes\n\nThe week had **50** minutes [P-A-NOTE-E1 L3] <script>alert(1)</script>. See also [P-A-NOTE-E1 L99].\n\n- Goal: `45` minutes\n  - met\n\n| Week | Minutes |\n|---|---|\n| 2030-03-04 | 50 |";
        var weekly = new Calculations(scratch.Database).Weekly(Patient, null, null);
        var result = JsonDocument.Parse(Json.Write(weekly)).RootElement.Clone();
        var record = new AnswerRecord
        {
            Question = Question, Model = "test-model", AbstractionVersion = "test", Narrative = Narrative,
            Checks = NarrativeChecks.Check(Narrative, Question, [result.GetRawText()]),
            Calculations =
            [
                new ToolCallRecord { Name = "find_patient", Input = "{\"query\":\"P-A\"}", Rendered = "", Result = JsonDocument.Parse("{}").RootElement.Clone() },
                new ToolCallRecord { Name = "weekly_minutes_and_goals", Input = "{\"patient_key\":\"P-A\"}", ElapsedMs = 2.5, Rendered = Markdown.Render(weekly), Result = result },
            ],
        };
        Assert.False(record.Checks.Passed);
        var answered = scratch.Database.SaveAnswer(null, QuestionAnswerer.Asked(Question, Patient), "test", "test-model", 1200, false, "", Json.Write(record, indented: true));
        scratch.Database.SaveAnswer(null, QuestionAnswerer.Asked(Question, Patient), "test", "test-model", 3, true, "", Json.Write(record, indented: true));
        scratch.Database.RecordGap("Why did <b>it</b> end?", Patient, "P-A-NOTE-E1", "P-A-NOTE-E1", 2, "<i>line 2</i> of P-A-NOTE-E1", "line", "attendance_reason");

        var model = new ModelAccess(() => Task.FromResult<string?>(null), _ => throw new InvalidOperationException("No model is called in this test."));
        await using var app = Server.Build(scratch.Database, Path.GetTempPath(), 0, model);
        await app.StartAsync();
        JsonNode run;
        try
        {
            run = Run(app.Urls.Single(), "steps", JsonSerializer.Serialize(new object[]
            {
                new { go = "#/ask" }, new { go = $"#/ask?answer={answered}" }, new { go = "#/documents" }, new { click = "P-A-NOTE-E1" },
                new { go = "#/document?id=P-A-NOTE-E1&line=3" }, new { go = "#/gaps" },
            }));
        }
        finally
        {
            await app.StopAsync();
        }

        Assert.Null(run["failure"]);
        Assert.Empty(run["consoleErrors"]!.AsArray());
        var pages = run["pages"]!.AsArray().Select(p => p!).ToList();
        foreach (var page in pages) Assert.True(page["errors"]!.AsArray().Count == 0, $"{page["hash"]}: {page["errors"]}");
        var created = run["created"]!.AsArray().Select(t => (string)t!).ToList();
        foreach (var tag in new[] { "img", "script", "b", "i" }) Assert.DoesNotContain(tag, created);
        static string Text(JsonNode page) => (string)page["text"]!;
        static List<JsonNode> Links(JsonNode page, string text) => page["links"]!.AsArray().Where(l => (string)l!["text"]! == text).Select(l => l!).ToList();

        // Ask: the earlier questions, the second marked as served from a saved answer.
        var ask = pages[0];
        Assert.Contains("Earlier questions", Text(ask));
        Assert.Contains("Served from a saved answer", Text(ask));
        Assert.Contains("Default patient, used when the question names none", Text(ask));

        // The answer: the warning above the written answer, then the three parts, each saying who wrote it.
        var answer = pages[1];
        var headings = answer["headings"]!.AsArray().Select(h => (string)h!).ToList();
        var order = new[] { "Check before relying on this answer", "Written answer", "Checks on the written answer", "Calculation tables" }.Select(h => headings.IndexOf(h)).ToList();
        Assert.True(order.All(i => i >= 0) && order.SequenceEqual(order.Order()), string.Join(" / ", headings));
        Assert.Contains("1. Weekly minutes and goals", headings);
        Assert.Contains(QuestionAnswerer.NarrativeBy, Text(answer));
        Assert.Contains(QuestionAnswerer.CalculationsBy, Text(answer));
        Assert.Contains("<script>alert(1)</script>", Text(answer));
        Assert.Contains("<img src=x onerror=alert(1)>", Text(answer));
        // The source in the written answer and the same source in the calculation table open step 4 for the patient.
        var cited = Links(answer, "P-A-NOTE-E1 L3");
        Assert.True(cited.Count >= 2, $"{cited.Count} links to P-A-NOTE-E1 L3.");
        Assert.All(cited, l => Assert.Equal(("4", "#/trace?patient=P-A&doc=P-A-NOTE-E1&lines=3"), ((string)l["step"]!, (string)l["href"]!)));

        // Documents, then one document with the assertions from line 3.
        Assert.Contains("Documents in this database", Text(pages[2]));
        Assert.Contains("#/document?id=P-A-NOTE-E1", (string)pages[3]["hash"]!);
        var line = pages[4];
        Assert.Contains("Assertions extracted from line 3: 1", Text(line));
        Assert.Contains("<i>line 3</i> of P-A-NOTE-E1", Text(line));
        Assert.Equal("4", (string)Assert.Single(Links(line, "P-A-NOTE-E1 L3"))["step"]!);

        // The gap log: the kind in words, the question, and the passage with its source as a link.
        var gaps = pages[5];
        Assert.Contains("Attendance reason: 1 passage", Text(gaps));
        Assert.Contains("Why did <b>it</b> end?", Text(gaps));
        Assert.Contains("<i>line 2</i> of P-A-NOTE-E1", Text(gaps));
        Assert.Equal("#/trace?patient=P-A&doc=P-A-NOTE-E1&lines=2", (string)Assert.Single(Links(gaps, "P-A-NOTE-E1 L2"))["href"]!);
    }

    private static JsonNode Run(string address, params string[] args)
    {
        var start = new ProcessStartInfo(Node!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(SuppliedRecordTests.Root, "tests", "ClinicalAbstraction.Tests", "page-harness.js"));
        start.ArgumentList.Add(address);
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The page did not finish within two minutes.");
        return JsonNode.Parse(output.Result) ?? throw new InvalidOperationException(error.Result);
    }
}

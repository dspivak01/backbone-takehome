using System.Net;
using System.Text.Json.Nodes;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;
using ClinicalAbstraction.Web;

namespace ClinicalAbstraction.Tests;

/// <summary>Runs a command as the command line would, and returns what it printed.</summary>
internal static class Command
{
    public static string Run(params string[] args)
    {
        var output = new StringWriter();
        var (standard, error) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            Cli.RunAsync(args).GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(standard);
            Console.SetError(error);
        }
        return output.ToString();
    }
}

/// <summary>Tests that capture what a command prints run one at a time, because the console is shared.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

/// <summary>
/// One test for each finding of the stage 1 review that was fixed, and for each of the four agreed
/// changes. Each runs against made-up patients or a copy of a saved database, with no model.
/// </summary>
[Collection("Console")]
public class ReviewFindingsTests
{
    private static readonly DateOnly Monday = new(2030, 3, 4);

    private static Assertion[] Session(string id, DateOnly date, string category, string start, string end) =>
    [
        Build.Encounter($"NOTE-{id}", category, encounter: id, date: date),
        Build.Contact($"NOTE-{id}", start, end, encounter: id, date: date),
    ];

    private static Assertion Plan() =>
        Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17", episodeStart: "2030-03-04", episodeEnd: "2030-03-17");

    // ---------- finding 1: identifiers with any characters ----------

    private const string AwkwardKey = "../P #1?&%\"'é/Ω|1990";
    private static readonly string[] AwkwardEncounters = ["..", ".", "E 1/2#?&%\"'", "Ж-101", "%2F", "a+b"];

    [Fact]
    public async Task Every_patient_and_encounter_opens_whatever_characters_its_identifiers_hold()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var assertions = new List<Assertion> { Plan() };
        for (var i = 0; i < AwkwardEncounters.Length; i++)
            assertions.AddRange(Session(AwkwardEncounters[i], Monday.AddDays(i % 5), "individual_therapy", "09:00", "09:50"));
        scratch.Add(AwkwardKey, "Åsa Nordström", [.. assertions]);
        scratch.Reconcile();

        await using var app = Server.Build(scratch.Database, Path.GetTempPath(), 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // Each value is encoded the way the page's URLSearchParams encodes it.
            async Task<JsonNode> Get(string path, params (string Name, string Value)[] query)
            {
                var response = await client.GetAsync(path + "?" + string.Join('&', query.Select(q => $"{q.Name}={WebUtility.UrlEncode(q.Value)}")));
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} {string.Join(", ", query)}: {body}");
                return JsonNode.Parse(body)!;
            }

            var patient = await Get("/api/patient", ("key", AwkwardKey));
            Assert.Equal(AwkwardKey, (string)patient["key"]!);
            var refs = patient["encounters"]!.AsArray().Select(e => (string)e!["ref"]!).ToList();
            Assert.Equal(AwkwardEncounters.Length, refs.Count);
            foreach (var id in refs)
                Assert.Equal(id, (string)(await Get("/api/patient/encounter", ("key", AwkwardKey), ("id", id)))["ref"]!);
            Assert.Equal(2, (await Get("/api/patient/weekly", ("key", AwkwardKey)))["weeks"]!.AsArray().Count);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>Saves a patient's assertions document by document, each document carrying the printed identifier given for it.</summary>
    private static void AddWithIds(ReviewApiTests.Scratch scratch, string patientKey, Func<string, string> printedId, params Assertion[] assertions)
    {
        scratch.Database.UpsertPatient(patientKey, patientKey, "Test Patient", "1990-01-01");
        foreach (var document in Build.ForPatient(patientKey, assertions).GroupBy(a => a.DocHash))
        {
            foreach (var a in document) a.PrintedDocId = printedId(document.Key);
            scratch.Database.SaveExtraction(
                new ExtractionRecord(document.Key, Vocabulary.ExtractorVersion, "none", "complete", null, document.Count(), 0, patientKey), "{}", [.. document], []);
        }
    }

    // ---------- finding 3: what would settle each field ----------

    [Fact]
    public void Each_unresolved_field_shows_only_what_would_settle_that_field()
    {
        using var scratch = new ReviewApiTests.Scratch();
        // Two signed notes on one session disagree on when it ended and on whether it took place.
        scratch.Add("P-N", "Nell Needs",
            Plan(),
            Build.Encounter("NOTE-A", "individual_therapy", encounter: "E1"),
            Build.Assertion("NOTE-A", 2, "presence", f => { f.Arrival = "09:00"; f.Departure = "09:50"; f.Disposition = "attended"; }, encounter: "E1"),
            Build.Assertion("NOTE-B", 2, "presence", f => { f.Arrival = "09:00"; f.Departure = "09:40"; f.Disposition = "no_show"; }, encounter: "E1"));
        scratch.Reconcile();

        var e = scratch.Api.Encounter("P-N", "E1");
        var end = e.Fields.Single(f => f.Field == "Presence end");
        var disposition = e.Fields.Single(f => f.Field == "Disposition");
        Assert.True(end.Unresolved && disposition.Unresolved);
        // "attended" contains "ended", which is how the attendance item used to land under the end time.
        Assert.Equal(["A signed correction or addendum for E1 stating when the patient's contact ended, or an independent record of departure."], end.WouldSettle);
        Assert.Equal(["A signed attendance record or correction for E1 stating whether the patient attended."], disposition.WouldSettle);
        Assert.Empty(e.WouldSettle);
    }

    // ---------- finding 4: identifiers and document text are never reworded ----------

    [Fact]
    public void Identifiers_and_document_text_are_never_reworded_and_values_are_given_words()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var wednesday = Monday.AddDays(2);
        AddWithIds(scratch, "P-U", document => document.EndsWith("-PLAN") ? "treatment_plan_note" : document,
        [
            Plan(),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            .. Session("E2", Monday.AddDays(1), "medication_management", "10:00", "10:20"),
            Build.Encounter("NOTE-E3", "group_therapy", encounter: "E3", date: wednesday),
            Build.Assertion("NOTE-E3", 2, "attendance", f => f.Disposition = "no_show", encounter: "E3", date: wednesday),
            Build.Assertion("CHARGE-E3", 1, "charge", f => { f.Description = "group_therapy session_code"; f.Quantity = 1; }, "billing_charge", "no", encounter: "E3", date: wednesday),
        ]);
        scratch.Reconcile();

        // A document identifier with underscores stays as printed, in a list of sources and inside a sentence.
        var week = scratch.Api.Weekly("P-U", null, null).Weeks[0];
        Assert.Equal(["treatment_plan_note L1"], week.GoalSources);
        Assert.Equal("The treatment plan says medication management does not count toward the goal (treatment_plan_note L1).",
            week.NotCounted.Single(c => c.Encounter == "E2").Reason);

        // A charge's own description is quoted as the document gives it.
        var noShow = scratch.Api.Encounter("P-U", "E3");
        Assert.Contains(noShow.Disagreements, d => d.Description.Contains("(\"group_therapy session_code\", quantity 1)"));

        // Values are words in the sentences the reconciler writes.
        Assert.Equal("Tier 1 (signed record) states \"no show\".", noShow.Fields.Single(f => f.Field == "Disposition").Explanation);
        Assert.Equal("Tier 1 (signed record) states individual therapy.", scratch.Api.Encounter("P-U", "E1").Fields.Single(f => f.Field == "Service category").Explanation);
    }

    // ---------- findings 6 and 7 ----------

    [Fact]
    public void The_collection_section_shows_no_more_patients_than_the_list()
    {
        using var scratch = new ReviewApiTests.Scratch();
        // With a goal and no sessions, every patient has two weeks below the goal.
        for (var i = 1; i <= ReviewApi.ListLimit + 1; i++) scratch.Add($"P-{i:00}", $"Patient {i:00}", Plan());
        scratch.Reconcile();

        var consecutive = scratch.Api.Consecutive(null, null);
        Assert.Equal(ReviewApi.ListLimit + 1, consecutive.Groups[0].Patients.Count);
        Assert.Equal(ReviewApi.ListLimit, consecutive.Details.Count);
        Assert.Equal("The pairs of weeks are shown for the first 50 of the 51 patients named above. Each patient's own page shows their weekly results.", consecutive.Note);

        var summary = scratch.Api.CollectionSummary(null, null);
        Assert.Equal(ReviewApi.ListLimit, summary.Rows.Count);
        Assert.Equal("Showing the first 50 of 51 patients, as the patient list does. Each patient's own page shows their figures.", summary.Note);
    }

    [Fact]
    public void A_week_with_no_appointments_says_so_once()
    {
        using var scratch = new ReviewApiTests.Scratch();
        scratch.Add("P-Q", "Quinn Quiet", [Plan(), .. Session("E1", Monday, "individual_therapy", "09:00", "09:50")]);
        scratch.Reconcile();

        var weeks = scratch.Api.Weekly("P-Q", null, null).Weeks;
        Assert.Null(weeks[0].ContactsNote);
        Assert.Equal("No appointment or contact is recorded in this week.", weeks[1].ContactsNote);
    }

    // ---------- findings 12, 14 and 17 ----------

    [Fact]
    public void A_document_is_named_by_its_identifier_or_at_least_eight_characters_of_its_hash()
    {
        using var copy = new ReviewApiTests.Copy("abstraction/abstraction.db");
        var hash = copy.Database.ListDocuments().Single(d => d.PrintedId == "BH-D005").DocHash;

        Assert.Equal("BH-D005, line 10", copy.Api.Source(hash[..8], "10", null, null).Heading);
        Assert.Throws<NotFoundException>(() => copy.Api.Source(hash[..1], "1", null, null));
        Assert.Throws<NotFoundException>(() => copy.Api.Source(hash[..7], "1", null, null));
    }

    [Fact]
    public void Only_messages_this_program_writes_reach_the_page()
    {
        static (int? Status, string Error) Run(Func<object> work)
        {
            var result = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(Server.Answer("The encounter could not be shown.", work));
            return (result.StatusCode, (string)JsonNode.Parse(result.ResponseContent!)!["error"]!);
        }

        // Messages .NET writes, such as these, are replaced by a plain sentence.
        foreach (var thrown in new Exception[] { new ArgumentOutOfRangeException("count"), new InvalidOperationException("Sequence contains no elements"), new FormatException("String 'x' was not recognized as a valid DateOnly.") })
            Assert.Equal((500, "The encounter could not be shown. Something unexpected went wrong. The details are printed in the window where the server was started."), Run(() => throw thrown));

        Assert.Equal((400, "The encounter could not be shown. Give a date as yyyy-MM-dd."), Run(() => throw new RequestException("Give a date as yyyy-MM-dd.")));
        Assert.Equal((404, "No such encounter."), Run(() => throw new NotFoundException("No such encounter.")));
    }

    [Fact]
    public void The_empty_database_message_names_the_database_to_ingest_into()
    {
        using var scratch = new ReviewApiTests.Scratch();
        Assert.EndsWith($"ingest documents --db \"{Path.GetFullPath(scratch.Database.FilePath)}\"", scratch.Api.Patients(null).EmptyCollection);
    }

    // ---------- Part C ----------

    [Fact]
    public void A_plan_document_whose_assertions_give_different_thresholds_is_flagged()
    {
        var abstraction = Build.Reconcile(
            Build.Plan("PLAN", 3, 150, "2030-03-04"),
            Build.Assertion("PLAN", 5, "plan_requirement", f => { f.MinMinutesPerWeek = 120; f.EffectiveFrom = "2030-03-04"; },
                "treatment_plan", encounter: null, noDate: true, signedAt: "2030-03-04T09:00"),
            Build.Encounter("NOTE", "individual_therapy"), Build.Contact("NOTE", "09:00", "09:50"));

        var plan = Assert.Single(abstraction.Plans);
        Assert.Equal(150, plan.MinMinutesPerWeek);
        Assert.Contains("PLAN gives different minimum minutes per week for one version of the treatment plan: 150 (PLAN L1), 120 (PLAN L5). " +
            "The plan version uses 150, the value stated first in the document, and the weekly goal results depend on it. Check the document.", abstraction.Warnings);
        Assert.DoesNotContain(abstraction.Warnings, w => w.Contains("minimum therapy days"));
    }

    [Fact]
    public void Events_the_reference_does_not_list_are_named_beside_the_score_without_changing_it()
    {
        using var scratch = new ReviewApiTests.Scratch();
        scratch.Add("P-E", "Eli Extra",
        [
            Plan(),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            .. Session("CALL-7", Monday.AddDays(1), "administrative", "11:00", "11:10"),
        ]);
        scratch.Reconcile();
        var reference = new Reporting.Evaluation.Reference
        {
            PatientKey = "P-E", From = "2030-03-04", To = "2030-03-17",
            Encounters = [new() { Id = "E1", Date = "2030-03-04", Category = "individual_therapy", Status = "counted", MinutesMin = 50, MinutesMax = 50 }],
        };

        var checks = Reporting.Evaluation.Score(scratch.Database, reference);
        var text = Reporting.Evaluation.Render(checks, "Scored", Reporting.Evaluation.NotListed(scratch.Database, reference));
        Assert.DoesNotContain(checks, c => c.Item == "CALL-7");
        Assert.Contains("Events the reference does not list, none of which can change a count, so they are not scored: 1 (CALL-7 on 2030-03-05, because it is not a session).", text);
    }

    [Fact]
    public void Each_week_of_a_consecutive_pair_carries_the_sources_of_its_goal_and_its_contacts_by_either_path()
    {
        using var copy = new ReviewApiTests.Copy("output/experiments/four-patients/collection.db");

        // For every patient over the whole episode the stored weekly results are read; for one
        // patient the weeks are worked out again. Both give the same sources.
        var stored = copy.Calculations.ConsecutiveWeeks(null, null, null).DependsOnUnresolvedDocumentation.Single(p => p.PatientKey == "RV-20931").PossiblePairs.Single();
        var computed = copy.Calculations.ConsecutiveWeeks("RV-20931", null, null).DependsOnUnresolvedDocumentation.Single().PossiblePairs.Single();
        Assert.NotEmpty(stored.FirstGoalSources);
        Assert.NotEmpty(stored.SecondContactSources);
        Assert.Equal(stored.FirstGoalSources, computed.FirstGoalSources);
        Assert.Equal(stored.FirstContactSources, computed.FirstContactSources);
        Assert.Equal(stored.SecondGoalSources, computed.SecondGoalSources);
        Assert.Equal(stored.SecondContactSources, computed.SecondContactSources);

        // The second week is unresolved: the sources behind both start times are among its sources.
        var unresolved = copy.Calculations.Load("RV-20931").Encounters.Single(e => e.Start.Status == "unresolved");
        Assert.All(unresolved.Start.Candidates.SelectMany(c => c.Citations), c => Assert.Contains(c, stored.SecondContactSources));

        // The command line table and the page show them.
        Assert.Contains($"Goal from {string.Join(", ", stored.FirstGoalSources)}.", Reporting.Markdown.Render(copy.Calculations.ConsecutiveWeeks(null, null, null)));
        var pair = copy.Api.Consecutive(null, null).Details.Single(d => d.Key == "RV-20931").Pairs.Single();
        Assert.Equal(stored.SecondContactSources, pair.SecondContactSources);
    }

    [Fact]
    public void The_scoring_report_describes_the_ingest_that_added_the_patient_scored()
    {
        using var copy = new ReviewApiTests.Copy("output/experiments/four-patients/collection.db");
        var printed = Command.Run("evaluate", "--reference", Path.Combine(SuppliedRecordTests.Root, "tests", "reference", "synthetic-patient.json"), "--db", copy.Database.FilePath);

        Assert.Contains("23 of 23 checks passed.", printed);
        Assert.Contains("| Ingest | test-data/synthetic-patient, the ingest that added this patient |", printed);
        Assert.Contains("| Documents | 5 |", printed);
        Assert.Contains("| Assertions kept | 52 |", printed);
        // The supplied record's figures, from the first ingest in this collection, are not reported.
        Assert.DoesNotContain("| Documents | 31 |", printed);
        Assert.DoesNotContain("129.1 s", printed);
    }

    // ---------- finding 2: two documents with the same printed identifier ----------

    [Fact]
    public void A_source_whose_identifier_two_documents_share_shows_the_right_document_or_says_it_cannot_tell()
    {
        using var scratch = new ReviewApiTests.Scratch();
        var tuesday = Monday.AddDays(1);
        var original = Build.ForPatient("P-S", Build.Encounter("A", "individual_therapy", encounter: "E1", date: Monday), Build.Contact("A", "09:00", "09:50", encounter: "E1", date: Monday));
        var revised = Build.ForPatient("P-S", Build.Encounter("B", "individual_therapy", encounter: "E2", date: tuesday),
            Build.Contact("B", "10:00", "10:50", encounter: "E2", date: tuesday), Build.Break("B", "10:20", "10:30", encounter: "E2", date: tuesday));
        scratch.Database.UpsertPatient("P-S", "P-S", "Sam Shared", "1990-01-01");
        foreach (var (hash, path, list) in new[] { ("P-S-A", "notes/a-original.txt", original), ("P-S-B", "notes/b-revised.txt", revised) })
        {
            foreach (var a in list) a.PrintedDocId = "SHARED-1";
            scratch.AddDocument(hash, "SHARED-1", path, string.Join('\n', Enumerable.Range(1, 5).Select(n => $"{path} line {n}")));
            scratch.Database.SaveExtraction(new ExtractionRecord(hash, Vocabulary.ExtractorVersion, "none", "complete", null, list.Length, 0, "P-S"), "{}", [.. list], []);
        }
        scratch.Reconcile();

        // Line 4 is cited only from the revised document, so that document's line is shown, although
        // the original comes first by file name.
        var four = scratch.Api.Source("SHARED-1", "4", null, null);
        Assert.Equal("notes/b-revised.txt", four.File);
        Assert.Equal("notes/b-revised.txt line 4", four.Lines.Single(l => l.Cited).Text);
        Assert.StartsWith("2 documents in this database carry the identifier \"SHARED-1\".", four.Note);

        // Line 3 is cited from both, so both are shown with their file names and neither as the one.
        var three = scratch.Api.Source("SHARED-1", "3", null, null);
        Assert.Empty(three.Lines);
        Assert.Equal(["notes/a-original.txt", "notes/b-revised.txt"], three.Candidates.Select(c => c.File));
        Assert.Equal(["notes/a-original.txt line 3", "notes/b-revised.txt line 3"], three.Candidates.Select(c => c.Lines.Single(l => l.Cited).Text));
        Assert.Contains("cannot be tied to one of them", three.Note);

        // The source command gives the same answers.
        var printed = Command.Run("source", "SHARED-1", "4", "--db", scratch.Database.FilePath);
        Assert.Contains("File: notes/b-revised.txt", printed);
        Assert.DoesNotContain("notes/a-original.txt line 4", printed);
        var both = Command.Run("source", "SHARED-1", "3", "--db", scratch.Database.FilePath);
        Assert.Contains("File: notes/a-original.txt", both);
        Assert.Contains("File: notes/b-revised.txt", both);
        Assert.Contains("cannot be tied to one of them", both);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Reconciliation;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;
using ClinicalAbstraction.Web;

namespace ClinicalAbstraction.Tests;

/// <summary>
/// The logic behind the review page's requests. Each test runs against a temporary SQLite file,
/// either a copy of a saved database or made-up patients, with no model. Only the last test starts
/// a server, to check what the web layer adds: the security headers and plain error messages.
/// </summary>
public class ReviewApiTests
{
    private static readonly DateOnly Monday = new(2030, 3, 4);

    /// <summary>A copy of a saved database in a temporary file, so nothing a test does can touch the original.</summary>
    internal sealed class Copy : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), $"abstraction-test-{Guid.NewGuid():N}.db");

        public Database Database { get; }
        public ReviewApi Api { get; }
        public Calculations Calculations { get; }

        public Copy(string relativePath)
        {
            File.Copy(Path.Combine(SuppliedRecordTests.Root, relativePath), _file);
            Database = new Database(_file);
            Api = new ReviewApi(Database, Path.GetTempPath());
            Calculations = new Calculations(Database);
        }

        public string Hash() => Hashing.Sha256(File.ReadAllBytes(_file));

        public void Dispose() => Remove(Database, _file);
    }

    /// <summary>Made-up patients and documents in their own temporary SQLite file.</summary>
    internal sealed class Scratch : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), $"abstraction-test-{Guid.NewGuid():N}.db");

        public Database Database { get; }
        public ReviewApi Api { get; }

        public Scratch()
        {
            Database = new Database(_file);
            Api = new ReviewApi(Database, Path.GetTempPath());
        }

        /// <summary>Saves a patient's statements the way ingest does, one document at a time.</summary>
        public void Add(string patientKey, string name, params Assertion[] assertions)
        {
            Database.UpsertPatient(patientKey, patientKey, name, "1990-01-01");
            foreach (var document in Build.ForPatient(patientKey, assertions).GroupBy(a => a.DocHash))
                Database.SaveExtraction(
                    new ExtractionRecord(document.Key, Vocabulary.ExtractorVersion, "none", "complete", null, document.Count(), 0, patientKey),
                    "{}", document.ToList(), []);
        }

        public void AddDocument(string docHash, string? printedId, string path, string text) =>
            Database.InsertDocument(new StoredDocument(docHash, docHash, printedId, path, text, text.Split('\n').Length, text.Length));

        public void Reconcile() => new ReconcileService(Database).Run();

        public void Dispose() => Remove(Database, _file);
    }

    internal static void Remove(Database database, string file)
    {
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(leftover);
    }

    private static Assertion[] Session(string id, DateOnly date, string category, string start, string end) =>
    [
        Build.Encounter($"NOTE-{id}", category, encounter: id, date: date),
        Build.Contact($"NOTE-{id}", start, end, encounter: id, date: date),
    ];

    private static Assertion Plan() =>
        Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17", episodeStart: "2030-03-04", episodeEnd: "2030-03-17");

    private const string Main = "abstraction/abstraction.db";
    private const string ThreePatients = "output/experiments/three-patients/collection.db";

    // ---------- the trace, on the supplied record ----------

    [Fact]
    public void The_trace_goes_from_the_week_of_January_5_to_BH_D005_line_10()
    {
        using var copy = new Copy(Main);

        // Step 1: the week.
        var week = Assert.Single(copy.Api.Weekly("HG-M042", null, null).Weeks, w => w.WeekStart == "2026-01-05");
        Assert.Equal(("3", "140", "Not met"), (week.Days, week.Minutes, week.Result));

        // Step 2: the sessions behind its total, with the arithmetic.
        Assert.Equal("50 + 45 + 45 = 140", week.Arithmetic);
        var session = Assert.Single(week.Counted, c => c.Encounter == "HG-E102");
        Assert.Equal("45", session.Minutes);
        Assert.Equal("Present 10:15-11:15, less break 10:45-11:00 = 45 minutes", session.Detail);
        Assert.Contains("BH-D005 L10", session.Sources);

        // Step 3: how each field of the session was decided.
        var encounter = copy.Api.Encounter("HG-M042", session.Ref);
        var start = Assert.Single(encounter.Fields, f => f.Field == "Presence start");
        Assert.Equal(("10:15", "Settled"), (start.Value, start.Decision));
        Assert.Contains("BH-D005 L10", start.Sources);

        // Step 4: the cited line with one line either side, its file, and what was extracted from it.
        var source = copy.Api.Source("BH-D005", "10", "10", null);
        Assert.Equal("BH-D005, line 10", source.Heading);
        Assert.Equal("documents/early_group_attendance_roster.txt", source.File);
        Assert.Equal([(9, false), (10, true), (11, false)], source.Lines.Select(l => (l.Number, l.Cited)));
        Assert.Contains("HG-E102", source.Lines[1].Text);
        Assert.Equal("Assertions extracted from this line: 5", source.StatementsHeading);
        Assert.Contains(source.Statements, s => s.Source == "BH-D005 L10" && s.Kind == "Presence" && s.Recorded.Any(r => r.Label == "Arrival" && r.Value == "10:15"));
    }

    [Fact]
    public void The_week_of_January_26_shows_both_start_times_and_what_would_settle_them()
    {
        using var copy = new Copy(Main);

        var week = Assert.Single(copy.Api.Weekly("HG-M042", null, null).Weeks, w => w.WeekStart == "2026-01-26");
        Assert.Equal(("145 to 155", "Cannot be determined", "undetermined"), (week.Minutes, week.Result, week.Tone));
        Assert.Contains(week.WouldSettle, n => n.Contains("HG-E115") && n.Contains("began"));

        const string Settle = "A signed correction or addendum for HG-E115 stating when the patient's contact began, or an independent record of arrival such as a check-in, room or platform log.";
        var encounter = copy.Api.Encounter("HG-M042", "HG-E115");
        var start = Assert.Single(encounter.Fields, f => f.Field == "Presence start");
        Assert.True(start.Unresolved);
        Assert.Equal("Not settled", start.Decision);
        Assert.Equal("09:00 or 09:10", start.Value);
        Assert.Equal([("09:00", "BH-D110 L7"), ("09:10", "BH-D111 L10")], start.Candidates.Select(c => (c.Value, Assert.Single(c.Sources))));

        // The item is shown with the field it settles, in the submitted abstraction and again when
        // it is rebuilt from the same assertions.
        Assert.Equal([Settle], start.WouldSettle);
        Assert.Empty(encounter.WouldSettle);
        new ReconcileService(copy.Database).Run();
        var rebuilt = copy.Api.Encounter("HG-M042", "HG-E115");
        Assert.Equal([Settle], rebuilt.Fields.Single(f => f.Field == "Presence start").WouldSettle);
        Assert.Empty(rebuilt.WouldSettle);
    }

    [Fact]
    public void Appointments_that_were_not_counted_appear_in_their_week_with_the_reason()
    {
        using var copy = new Copy(Main);
        var weeks = copy.Api.Weekly("HG-M042", null, null).Weeks;

        var noShow = Assert.Single(weeks.Single(w => w.WeekStart == "2026-01-05").NotCounted);
        Assert.Equal(("HG-E103", "0", "The appointment was a no show."), (noShow.Encounter, noShow.Minutes, noShow.Reason));
        Assert.Equal("Not counted: not a session", noShow.Status);

        var medication = Assert.Single(weeks.Single(w => w.WeekStart == "2026-01-12").NotCounted, c => c.Encounter == "HG-E106");
        Assert.Equal("Not counted: the plan excludes this service", medication.Status);
        Assert.Equal("The treatment plan says medication management does not count toward the goal (BH-D003 L6, BH-D003 L12).", medication.Reason);
    }

    [Fact]
    public void Statements_set_aside_and_disagreements_are_shown_with_the_rule_and_reason()
    {
        using var copy = new Copy(Main);

        // The January 19 group: a correction replaced the departure time.
        var corrected = copy.Api.Encounter("HG-M042", "HG-E110");
        Assert.Contains(corrected.SetAside, s => s.Rule == "Correction applied (rule R3)" && s.Field == "Presence end" && s.Reason.Contains("corrected to 11:15"));
        Assert.Equal("11:15", corrected.Fields.Single(f => f.Field == "Presence end").Value);

        // The January 27 no show: a draft and a charge disagree with the signed register.
        var noShow = copy.Api.Encounter("HG-M042", "HG-E116");
        Assert.Equal(2, noShow.Disagreements.Count(d => d.Rule == "A less authoritative record disagrees and changes nothing (rule R5)"));
        Assert.Contains(noShow.Disagreements, d => d.Description.Contains("A draft is not evidence of attendance") && d.Sources.Contains("BH-D112 L9-11"));
        Assert.Equal("The appointment was a no show.", noShow.Conclusion);
    }

    // ---------- the same figures as the command line ----------

    [Fact]
    public void Every_weekly_figure_is_the_one_the_command_line_prints()
    {
        using var copy = new Copy(Main);
        var view = copy.Api.Weekly("HG-M042", null, null);
        var printed = Markdown.Render(copy.Calculations.Weekly("HG-M042", null, null));

        // Each row of the command line's table, rebuilt from what the page receives.
        foreach (var w in view.Weeks)
            Assert.Contains($"| {w.Label}{(w.Partial ? " (partial)" : "")} | {w.Days} | {w.Minutes} | {w.Hours} | {w.Arithmetic} | {w.Goal} | {w.Result.ToLowerInvariant()} |", printed);
        Assert.Contains($"**Total**: {view.TotalMinutes} minutes, {view.TotalHours} hours. {view.TotalArithmetic}.", printed);
        Assert.Equal(("585 to 595", "9.75 to 9.92"), (view.TotalMinutes, view.TotalHours));
    }

    [Fact]
    public void Session_counts_and_the_collection_are_the_figures_the_command_line_prints()
    {
        using var copy = new Copy(ThreePatients);

        var sessions = copy.Api.Sessions("NS-77120", null, null);
        var printed = Markdown.Render(copy.Calculations.SessionCounts("NS-77120", null, null));
        foreach (var s in sessions.ByService) Assert.Contains($"| {s.Service} | {s.Sessions} |", printed);
        Assert.Contains($"| Total | {sessions.Total} |", printed);
        Assert.Contains($"| Distinct days | {sessions.DistinctDays} |", printed);
        Assert.Equal("8 to 9", sessions.Total);

        var summary = copy.Api.CollectionSummary(null, null);
        var printedSummary = Markdown.Render(copy.Calculations.CollectionSummary(null, null));
        Assert.Equal(3, summary.Rows.Count);
        foreach (var r in summary.Rows)
        {
            Assert.Contains($"| {r.Label} | {r.Period} | {string.Join(" | ", r.Sessions)} | {r.Total} | {r.DistinctDays} | {r.Minutes} |", printedSummary);
            Assert.Contains($"| {r.Label} | {r.WeeksExamined} | {r.WeeksMet} | {r.WeeksNotMet} | {r.WeeksUndetermined} | {r.WeeksWithNoGoal} | {r.PartialWeeks} |", printedSummary);
        }

        var consecutive = copy.Api.Consecutive(null, null);
        Assert.Equal(3, consecutive.PatientsExamined);
        Assert.Equal(["HG-M042", "NS-77120"], consecutive.Groups[0].Patients.Select(p => p.Key));
        Assert.Equal(["Imani Castell (WD-40815)"], consecutive.Groups[3].Named);
        Assert.Equal("Definite", consecutive.Details.Single(d => d.Key == "NS-77120").Pairs.Single().Kind);
    }

    // ---------- wording ----------

    [Fact]
    public void No_internal_name_reaches_the_page()
    {
        foreach (var path in new[] { Main, ThreePatients })
        {
            using var copy = new Copy(path);
            var views = new List<object> { copy.Api.Status(), copy.Api.Patients(null), copy.Api.Consecutive(null, null), copy.Api.CollectionSummary(null, null) };
            foreach (var patient in copy.Api.Patients(null).Patients)
            {
                var one = copy.Api.Patient(patient.Key);
                views.AddRange([one, copy.Api.Weekly(patient.Key, null, null), copy.Api.Sessions(patient.Key, null, null), copy.Api.Measures(patient.Key, null)]);
                views.AddRange(one.Encounters.Select(e => (object)copy.Api.Encounter(patient.Key, e.Ref)));
                views.AddRange(one.Encounters.Select(e => e.Date).Distinct().Select(d => (object)copy.Api.Day(patient.Key, d)));
            }

            foreach (var view in views)
                foreach (var (property, value) in Strings(JsonNode.Parse(Json.Write(view))!, ""))
                {
                    // Keys the page only puts in the address, and text quoted from the documents, are never reworded.
                    if (property is "ref" or "tone" or "first_tone" or "second_tone" or "quote" or "text" or "file" or "database" or "output_folder" or "summary" or "description") continue;
                    Assert.False(InternalName.IsMatch(value), $"An internal name in {property}: \"{value}\"");
                    Assert.DoesNotContain("NOID|", value);
                }
        }
    }

    private static readonly Regex InternalName = new(@"(?<![\w./-])[a-z]+(?:_[a-z]+)+(?![\w/-]|\.\w)");

    [Fact]
    public void Rules_are_named_in_words_sentences_start_with_a_capital_and_times_are_plain()
    {
        var bareRule = new Regex(@"(?<![A-Za-z])(?<!rule )R\d\b");
        var lowerAfterStop = new Regex(@"\. [a-z]");
        var isoTime = new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}");
        foreach (var path in new[] { Main, ThreePatients, "output/experiments/four-patients/collection.db" })
        {
            using var copy = new Copy(path);
            var views = new List<object> { copy.Api.Consecutive(null, null), copy.Api.CollectionSummary(null, null) };
            foreach (var patient in copy.Api.Patients(null).Patients)
            {
                var one = copy.Api.Patient(patient.Key);
                views.AddRange([one, copy.Api.Weekly(patient.Key, null, null), copy.Api.Sessions(patient.Key, null, null), copy.Api.Measures(patient.Key, null)]);
                views.AddRange(one.Encounters.Select(e => (object)copy.Api.Encounter(patient.Key, e.Ref)));
            }

            foreach (var view in views)
                foreach (var (property, value) in Strings(JsonNode.Parse(Json.Write(view))!, ""))
                {
                    // Text quoted from the documents is shown exactly as it is.
                    if (property is "quote" or "text" or "summary" or "file" or "service_labels") continue;
                    Assert.False(bareRule.IsMatch(value), $"A rule code without words in {property}: \"{value}\"");
                    Assert.False(lowerAfterStop.IsMatch(value), $"A sentence starting in lower case in {property}: \"{value}\"");
                    Assert.False(isoTime.IsMatch(value), $"A time written with a T in {property}: \"{value}\"");
                }
        }
    }

    private static IEnumerable<(string Property, string Value)> Strings(JsonNode node, string property) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Value is null ? [] : Strings(p.Value, p.Key)),
        JsonArray a => a.SelectMany(item => item is null ? [] : Strings(item, property)),
        JsonValue v when v.TryGetValue<string>(out var s) => [(property, s)],
        _ => [],
    };

    [Fact]
    public void Sentences_saved_with_codes_get_words_but_identifiers_file_names_and_quotes_do_not()
    {
        // A sentence the code writes is never reworded, only started with a capital and ended.
        Assert.Equal("Extraction failed for early_group_attendance_roster.txt.", Wording.FullSentence("Extraction failed for early_group_attendance_roster.txt"));
        Assert.Equal("Goal from treatment_plan_note L6.", Wording.FullSentence("Goal from treatment_plan_note L6"));

        // A sentence saved by the earlier reconciler with a value's code gets the value's words,
        // but a source, a file name and quoted document text keep theirs.
        Assert.Equal("Tier 1 (signed record) states \"took place\".", Wording.Saved("tier 1 (signed record) states took_place.", []));
        Assert.Equal("Tier 2 (administrative record) states service category group therapy, which differs from the higher-tier record.",
            Wording.Saved("tier 2 (administrative record) states service category group_therapy, which differs from the higher-tier record.", []));
        Assert.Equal("Corrected to \"no show\" by no_show L3", Wording.Saved("corrected to no_show by no_show L3", ["no_show L3"]));
        Assert.Equal("A charge was posted (\"group_therapy\", quantity 1) for group_therapy.txt.", Wording.Saved("A charge was posted (\"group_therapy\", quantity 1) for group_therapy.txt.", []));
        Assert.Equal("No identifier: family therapy on 2026-02-26", Wording.EncounterLabel("NOID|2026-02-26|family_therapy", null));
        Assert.Equal(["Met", "Not met", "Cannot be determined", "No goal in effect"],
            new[] { WeeklyCalculator.Met, WeeklyCalculator.NotMet, WeeklyCalculator.Undetermined, WeeklyCalculator.NoGoal }.Select(Wording.Result));
    }

    // ---------- plain messages ----------

    [Fact]
    public void A_patient_or_encounter_that_does_not_exist_is_a_plain_message()
    {
        using var copy = new Copy(Main);

        Assert.Equal("No patient with the key \"NOPE-1\" is in this database.", Assert.Throws<NotFoundException>(() => copy.Api.Patient("NOPE-1")).Message);
        Assert.Throws<NotFoundException>(() => copy.Api.Weekly("NOPE-1", null, null));
        Assert.Equal("Rowan Mercer (HG-M042) has no encounter \"HG-E999\".", Assert.Throws<NotFoundException>(() => copy.Api.Encounter("HG-M042", "HG-E999")).Message);
    }

    [Fact]
    public void A_source_outside_the_document_is_refused_with_a_plain_message()
    {
        using var copy = new Copy(Main);

        Assert.Equal("BH-D005 has 17 lines. Line 99 is outside it.", Assert.Throws<RequestException>(() => copy.Api.Source("BH-D005", "99", null, null)).Message);
        Assert.Equal("BH-D005 has 17 lines. Lines 16 to 18 are outside it.", Assert.Throws<RequestException>(() => copy.Api.Source("BH-D005", "16", "18", null)).Message);
        Assert.Equal("BH-D005 has 17 lines. Line 0 is outside it.", Assert.Throws<RequestException>(() => copy.Api.Source("BH-D005", "0", null, null)).Message);
        Assert.Equal("The last line asked for, 9, comes before the first, 10.", Assert.Throws<RequestException>(() => copy.Api.Source("BH-D005", "10", "9", null)).Message);
        Assert.Equal("Give the first line as a number.", Assert.Throws<RequestException>(() => copy.Api.Source("BH-D005", "ten", null, null)).Message);
        Assert.Equal("No document \"BH-D999\" is in this database.", Assert.Throws<NotFoundException>(() => copy.Api.Source("BH-D999", "1", null, null)).Message);

        // The last line of the document is inside it, and its context stops at the end.
        Assert.Equal([16, 17], copy.Api.Source("BH-D005", "17", null, null).Lines.Select(l => l.Number));
    }

    [Fact]
    public void A_date_that_cannot_be_read_is_refused_rather_than_silently_replaced_by_the_episode()
    {
        using var copy = new Copy(Main);

        Assert.Equal("\"2026-02-30\" is not a date. Use yyyy-MM-dd.", Assert.Throws<RequestException>(() => copy.Api.Weekly("HG-M042", "2026-02-30", null)).Message);
        Assert.Throws<RequestException>(() => copy.Api.Sessions("HG-M042", "2026-01-30", "2026-01-05"));
        Assert.Throws<RequestException>(() => copy.Api.Weekly("HG-M042", "1990-01-01", "2026-01-30"));
        Assert.Throws<RequestException>(() => copy.Api.Day("HG-M042", null));

        // A period that is given is used as given.
        var one = Assert.Single(copy.Api.Weekly("HG-M042", "2026-01-12", "2026-01-18").Weeks);
        Assert.Equal(("2026-01-12", "120"), (one.WeekStart, one.Minutes));
    }

    // ---------- states ----------

    [Fact]
    public void An_empty_database_says_that_no_documents_have_been_processed()
    {
        using var scratch = new Scratch();

        var list = scratch.Api.Patients(null);
        Assert.Empty(list.Patients);
        Assert.StartsWith("No documents have been processed into this database", list.EmptyCollection);
        Assert.Equal(0, scratch.Api.Status().Patients);
        Assert.Empty(scratch.Api.CollectionSummary(null, null).Rows);
        Assert.Equal(0, scratch.Api.Consecutive(null, null).PatientsExamined);
    }

    [Fact]
    public void The_list_offers_a_search_above_ten_patients_and_shows_the_first_fifty_above_fifty()
    {
        using var scratch = new Scratch();
        for (var i = 1; i <= 51; i++)
            scratch.Add($"P-{i:00}", $"Patient {i:00}", [Plan(), .. Session("E1", Monday, "individual_therapy", "09:00", "09:50")]);
        scratch.Reconcile();

        var list = scratch.Api.Patients(null);
        Assert.True(list.SearchOffered);
        Assert.Equal(51, list.PatientsInCollection);
        Assert.Equal(ReviewApi.ListLimit, list.Patients.Count);
        Assert.Equal("Showing the first 50 of 51 patients. Search by name, key or medical record number to find the others.", list.Note);

        var found = Assert.Single(scratch.Api.Patients("patient 51").Patients);
        Assert.Equal(("P-51", "2030-03-04 to 2030-03-17", 1, 1), (found.Key, found.Episode, found.PlanVersions, found.Encounters));
        Assert.Equal("No patient matches \"nobody\". The collection holds 51 patients.", scratch.Api.Patients("nobody").Note);
    }

    [Fact]
    public void Ten_patients_are_listed_without_a_search_box()
    {
        using var scratch = new Scratch();
        for (var i = 1; i <= ReviewApi.SearchThreshold; i++)
            scratch.Add($"P-{i:00}", $"Patient {i:00}", [Plan(), .. Session("E1", Monday, "individual_therapy", "09:00", "09:50")]);
        scratch.Reconcile();

        var list = scratch.Api.Patients(null);
        Assert.False(list.SearchOffered);
        Assert.Equal(ReviewApi.SearchThreshold, list.Patients.Count);
        Assert.Null(list.Note);
    }

    [Fact]
    public void A_failed_extraction_and_an_unlinked_statement_are_warnings_on_the_patient()
    {
        using var scratch = new Scratch();
        scratch.Add("P-W", "Wren Example",
        [
            Plan(),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            Build.Assertion("LOOSE", 5, "presence", f => f.Arrival = "10:00", encounter: null, noDate: true),
        ]);
        scratch.AddDocument("P-W-FAILED-DOCUMENT", null, "notes/failed_note.txt", "A note that could not be read.");
        scratch.Database.SaveExtraction(new ExtractionRecord("P-W-FAILED-DOCUMENT", Vocabulary.ExtractorVersion, "none", "failed", null, 0, 0, "P-W"), "{}", [], []);
        scratch.Reconcile();

        var patient = scratch.Api.Patient("P-W");
        Assert.Contains(patient.Warnings, w => w.StartsWith("Extraction failed for failed_note.txt."));
        Assert.Contains(patient.Warnings, w => w.StartsWith("1 assertion(s) about service contacts could not be linked"));
        var loose = Assert.Single(patient.Unlinked);
        Assert.Equal(("P-W-LOOSE L5", "Presence", "No encounter identifier and no service date."), (loose.Source, loose.Kind, loose.Reason));
        Assert.Equal(2, Assert.Single(scratch.Api.Patients(null).Patients).Warnings);
        Assert.Equal(2, scratch.Api.Weekly("P-W", null, null).Warnings.Count);
    }

    // ---------- safety ----------

    private const string Script = "<script>alert(1)</script>";
    private const string Image = "<img src=x onerror=alert(1)>";

    [Fact]
    public void Markup_in_a_document_or_a_name_comes_back_as_plain_text_and_the_page_never_inserts_markup()
    {
        using var scratch = new Scratch();
        scratch.Add("P-X", $"{Image} Example", [Plan(), .. Session("E1", Monday, "individual_therapy", "09:00", "09:50")]);
        scratch.AddDocument("P-X-NOTE-E1", "P-X-NOTE-E1", "notes/markup.txt", $"Progress note\nPatient {Image}\n{Script} Seen 09:00 to 09:50\nSigned");
        scratch.Reconcile();

        // The data is returned exactly as stored. It is the page's job to show it as text.
        Assert.Equal($"{Image} Example", scratch.Api.Patient("P-X").Name);
        var source = scratch.Api.Source("P-X-NOTE-E1", "3", null, null);
        Assert.Equal($"{Script} Seen 09:00 to 09:50", source.Lines.Single(l => l.Cited).Text);
        Assert.Contains("\\u003Cscript\\u003E", Json.Write(source));

        // The page builds every element with createElement and text nodes. None of the ways a
        // string can become markup appears anywhere in it.
        var page = Server.Page();
        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "createContextualFragment", "DOMParser", "srcdoc", "eval(", "new Function", "setTimeout('", "javascript:" })
            Assert.DoesNotContain(sink, page);
        Assert.DoesNotMatch(new Regex(@"<[a-z]+[^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase), page);
        Assert.DoesNotContain("://", Regex.Replace(page, @"<script>.*</script>", "", RegexOptions.Singleline));
        Assert.Single(Regex.Matches(page, "<script"));

        var policy = Server.ContentSecurityPolicy(page);
        Assert.StartsWith("default-src 'none'; script-src 'sha256-", policy);
        Assert.DoesNotContain("unsafe", policy);
    }

    [Fact]
    public void No_request_changes_the_database()
    {
        using var copy = new Copy(Main);
        var before = copy.Hash();

        copy.Api.Status();
        copy.Api.Patients(null);
        copy.Api.Patient("HG-M042");
        copy.Api.Weekly("HG-M042", null, null);
        copy.Api.Sessions("HG-M042", "2026-01-05", "2026-01-30");
        copy.Api.Encounter("HG-M042", "HG-E115");
        copy.Api.Day("HG-M042", "2026-01-19");
        copy.Api.Measures("HG-M042", null);
        copy.Api.Observations("HG-M042", null, null, null);
        copy.Api.Consecutive(null, null);
        copy.Api.CollectionSummary(null, null);
        copy.Api.Source("BH-D103", "5", "7", "2");
        copy.Database.Checkpoint();

        Assert.Equal(before, copy.Hash());
    }

    [Fact]
    public async Task The_server_listens_on_127_0_0_1_sends_the_security_policy_and_answers_failures_plainly()
    {
        using var scratch = new Scratch();
        scratch.Add("P-S", "Sam Example", [Plan(), .. Session("E1", Monday, "individual_therapy", "09:00", "09:50")]);
        scratch.Reconcile();

        await using var app = Server.Build(scratch.Database, Path.GetTempPath(), 0);
        await app.StartAsync();
        try
        {
            var address = Assert.Single(app.Urls);
            Assert.StartsWith("http://127.0.0.1:", address);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            var page = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            var policy = string.Join(" ", page.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("default-src 'none'", policy);
            Assert.Contains("script-src 'sha256-", policy);
            Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

            var weekly = await client.GetAsync("/api/patients/P-S/weekly");
            Assert.Equal(HttpStatusCode.OK, weekly.StatusCode);
            Assert.Contains("\"minutes\":\"50\"", await weekly.Content.ReadAsStringAsync());

            var missing = await client.GetAsync("/api/patients/NOPE");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("{\"error\":\"No patient with the key \\u0022NOPE\\u0022 is in this database.\"}", await missing.Content.ReadAsStringAsync());

            var badDate = await client.GetAsync("/api/patients/P-S/weekly?from=someday");
            Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
            var message = (string)JsonNode.Parse(await badDate.Content.ReadAsStringAsync())!["error"]!;
            Assert.Equal("The weekly results could not be worked out. \"someday\" is not a date. Use yyyy-MM-dd.", message);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nothing-here")).StatusCode);

            // A request addressed to another name, as a rebinding attack would send, is refused.
            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            foreign.Headers.Host = "attacker.example";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(foreign)).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

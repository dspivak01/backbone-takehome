using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Reconciliation;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Tests;

/// <summary>
/// Checks on the supplied record. They read the assertions saved by the last extraction, so they
/// run without a model, and they compare the results with a reference worked by hand.
/// </summary>
public class SuppliedRecordTests
{
    internal static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinicalAbstraction.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static List<Assertion> SavedAssertions(string patientKey)
    {
        var path = Path.Combine(Root, "abstraction", "export", "assertions.json");
        Assert.True(File.Exists(path), $"Run 'export' first. Expected {path}.");
        return Json.Read<List<Assertion>>(File.ReadAllText(path)).Where(a => a.PatientKey == patientKey).ToList();
    }

    private static Evaluation.Reference Reference() =>
        Json.Read<Evaluation.Reference>(File.ReadAllText(Path.Combine(Root, "tests", "reference", "supplied-record.json")));

    private static PatientAbstraction Rebuild(IEnumerable<Assertion> assertions) =>
        new Reconciler().Reconcile("HG-M042", "HG-M042", "Rowan Mercer", "1991-04-12", assertions, 31, []);

    [Fact]
    public void Weekly_days_minutes_and_results_match_the_reference()
    {
        var reference = Reference();
        var patient = Rebuild(SavedAssertions(reference.PatientKey));
        var weeks = WeeklyCalculator.Weeks(patient, DateOnly.Parse(reference.From), DateOnly.Parse(reference.To));

        Assert.Equal(reference.Weeks.Count, weeks.Count);
        foreach (var expected in reference.Weeks)
        {
            var actual = Assert.Single(weeks, w => w.WeekStart == expected.WeekStart);
            Assert.Equal((expected.DaysMin, expected.DaysMax), (actual.DaysMin, actual.DaysMax));
            Assert.Equal((expected.MinutesMin, expected.MinutesMax), (actual.MinutesMin, actual.MinutesMax));
            Assert.Equal(expected.Result, actual.Result);
        }
    }

    [Fact]
    public void Every_encounter_matches_the_reference()
    {
        var reference = Reference();
        var patient = Rebuild(SavedAssertions(reference.PatientKey));
        var contacts = WeeklyCalculator.ContactsInRange(patient, DateOnly.Parse(reference.From), DateOnly.Parse(reference.To));

        // Nothing outside the reference may be able to change a count.
        Assert.DoesNotContain(contacts, c => (c.Counted || c.MayCount) && reference.Encounters.All(e => e.Id != c.EncounterId));
        foreach (var expected in reference.Encounters)
        {
            var actual = Assert.Single(contacts, c => c.EncounterId == expected.Id);
            Assert.Equal(expected.Date, actual.Date);
            Assert.Equal(expected.Category, actual.Category);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal((expected.MinutesMin, expected.MinutesMax), (actual.MinutesMin, actual.MinutesMax));
        }
    }

    [Fact]
    public void Rebuilding_from_the_same_assertions_in_any_order_gives_the_same_abstraction()
    {
        var assertions = SavedAssertions("HG-M042");
        var forward = Rebuild(assertions);
        var reversed = Rebuild(assertions.AsEnumerable().Reverse());
        var shuffled = Rebuild(assertions.OrderBy(a => a.Quote.Length).ThenBy(a => a.Id % 7));

        Assert.Equal(forward.Version, reversed.Version);
        Assert.Equal(forward.Version, shuffled.Version);
    }

    [Fact]
    public void Seeing_every_assertion_twice_changes_nothing()
    {
        var assertions = SavedAssertions("HG-M042");
        var once = Rebuild(assertions);
        var twice = Rebuild(assertions.Concat(assertions));

        var from = new DateOnly(2026, 1, 5);
        var to = new DateOnly(2026, 1, 30);
        Assert.Equal(
            WeeklyCalculator.Weeks(once, from, to).Select(w => (w.DaysMin, w.DaysMax, w.MinutesMin, w.MinutesMax, w.Result)),
            WeeklyCalculator.Weeks(twice, from, to).Select(w => (w.DaysMin, w.DaysMax, w.MinutesMin, w.MinutesMax, w.Result)));
    }

    [Fact]
    public void Ingesting_every_document_twice_registers_each_one_once()
    {
        var folder = Path.Combine(Root, "documents");
        var file = Path.Combine(Path.GetTempPath(), $"abstraction-test-{Guid.NewGuid():N}.db");
        try
        {
            using var database = new Database(file);
            var registry = new DocumentRegistry(database);
            var files = Directory.GetFiles(folder, "*.txt");

            var first = files.Select(registry.Register).ToList();
            var second = files.Select(registry.Register).ToList();

            Assert.All(first, r => Assert.Equal(RegistrationOutcome.New, r.Outcome));
            Assert.All(second, r => Assert.Equal(RegistrationOutcome.Duplicate, r.Outcome));
            Assert.Equal(files.Length, database.ListDocuments().Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(leftover);
        }
    }

    [Fact]
    public void Removing_the_encounter_identifier_from_one_documents_statements_changes_nothing()
    {
        var reference = Reference();
        var assertions = SavedAssertions(reference.PatientKey);

        // Strip the identifier from every statement made by the documents that describe one encounter's attendance.
        var stripped = assertions.Select(a => Json.Read<Assertion>(Json.Write(a))).ToList();
        var target = stripped.Where(a => a.EncounterId == "HG-E113" && a.RecordType == "attendance_record").ToList();
        Assert.NotEmpty(target);
        foreach (var a in target) a.EncounterId = null;

        var patient = Rebuild(stripped);
        var weeks = WeeklyCalculator.Weeks(patient, DateOnly.Parse(reference.From), DateOnly.Parse(reference.To));
        foreach (var expected in reference.Weeks)
        {
            var actual = Assert.Single(weeks, w => w.WeekStart == expected.WeekStart);
            Assert.Equal((expected.MinutesMin, expected.MinutesMax), (actual.MinutesMin, actual.MinutesMax));
            Assert.Equal(expected.Result, actual.Result);
        }
    }
}

using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Reconciliation;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Tests;

/// <summary>
/// A question that names no patient applies to every patient who fits what it does specify.
/// These tests check that code, reading the saved events, decides who that is. They use made-up
/// patients saved to a temporary database, and no model.
/// </summary>
public class NoPatientNamedTests
{
    private static readonly DateOnly Monday = new(2030, 3, 4);

    /// <summary>A collection in its own temporary SQLite file, removed when the test ends.</summary>
    private sealed class Collection : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), $"abstraction-test-{Guid.NewGuid():N}.db");
        private readonly Database _database;

        public Calculations Calculations { get; }

        public Collection()
        {
            _database = new Database(_file);
            Calculations = new Calculations(_database);
        }

        /// <summary>Adds a made-up patient from assertions built by hand.</summary>
        public void Add(string patientKey, string name, params Assertion[] assertions) =>
            AddSaved(patientKey, name, Build.ForPatient(patientKey, assertions));

        /// <summary>Saves assertions that already name their patient, the way ingest does, one document at a time.</summary>
        public void AddSaved(string patientKey, string name, IEnumerable<Assertion> assertions)
        {
            _database.UpsertPatient(patientKey, patientKey, name, "1990-01-01");
            foreach (var document in assertions.GroupBy(a => a.DocHash))
                _database.SaveExtraction(
                    new ExtractionRecord(document.Key, Vocabulary.ExtractorVersion, "none", "complete", null, document.Count(), 0, patientKey),
                    "{}", document.ToList(), []);
        }

        /// <summary>Builds every patient's events from the saved assertions, as the reconcile command does.</summary>
        public void Reconcile() => new ReconcileService(_database).Run();

        public void Dispose()
        {
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*")) File.Delete(leftover);
        }
    }

    private static Assertion[] Session(string id, DateOnly date, string category, string start, string end) =>
    [
        Build.Encounter($"NOTE-{id}", category, encounter: id, date: date),
        Build.Contact($"NOTE-{id}", start, end, encounter: id, date: date),
    ];

    private static Assertion Plan(int days, int minutes, string from, string to) =>
        Build.Plan("PLAN", days, minutes, from, to, episodeStart: from, episodeEnd: to);

    /// <summary>Two patients whose totals are worked out by hand in the tests that use them.</summary>
    private static Collection TwoPatients()
    {
        var collection = new Collection();

        // Week one: 50 and 60 minutes on two days, goal met. Week two: 45 minutes on one day, goal not met.
        // The medication visit is excluded by the plan.
        collection.Add("P-A", "Ada Example",
        [
            Plan(2, 90, "2030-03-04", "2030-03-17"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            .. Session("E2", Monday.AddDays(2), "group_therapy", "10:00", "11:00"),
            .. Session("E3", Monday.AddDays(7), "individual_therapy", "09:00", "09:45"),
            .. Session("E4", Monday.AddDays(8), "medication_management", "14:00", "14:25"),
        ]);

        // Week one: two signed notes give 50 and 40 minutes against a goal of 45, so it cannot be determined.
        // Week two: 45 minutes, goal met.
        collection.Add("P-B", "Ben Example",
        [
            Plan(1, 45, "2030-03-04", "2030-03-17"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            Build.Contact("NOTE-E1-SECOND", "09:10", "09:50", encounter: "E1", date: Monday),
            .. Session("E2", Monday.AddDays(7), "family_therapy", "13:00", "13:45"),
        ]);

        collection.Reconcile();
        return collection;
    }

    // ---------- one day across patients ----------

    [Fact]
    public void Two_patients_with_contacts_on_the_same_date_both_appear_in_the_day_result()
    {
        using var collection = TwoPatients();

        var day = collection.Calculations.DayAcrossPatients("2030-03-04");

        Assert.Equal(2, day.PatientsWhoFit);
        Assert.True(day.FullDetailGiven);
        Assert.Equal(["P-A", "P-B"], day.Summary.Select(p => p.PatientKey));
        Assert.Equal([(1, 50, 50), (1, 40, 50)], day.Summary.Select(p => (p.TherapyContacts, p.MinutesMin, p.MinutesMax)));
        Assert.Equal(["P-A", "P-B"], day.Patients.Select(p => p.Parameters.PatientKey));
        Assert.Contains("All 2 patients in the collection have an appointment or contact recorded on 2030-03-04.", day.Notes);
    }

    [Fact]
    public void Each_patients_part_of_the_day_is_what_a_question_naming_that_patient_gets()
    {
        using var collection = TwoPatients();

        var day = collection.Calculations.DayAcrossPatients("2030-03-04");

        foreach (var patient in day.Patients)
            Assert.Equal(Json.Write(collection.Calculations.Day(patient.Parameters.PatientKey, "2030-03-04")), Json.Write(patient));
    }

    [Fact]
    public void A_patient_with_no_contact_on_that_date_is_left_out()
    {
        using var collection = TwoPatients();

        // Only Ada has a contact on the Wednesday of week one.
        var day = collection.Calculations.DayAcrossPatients("2030-03-06");

        Assert.Equal(2, day.PatientsInCollection);
        Assert.Equal(["P-A"], day.Summary.Select(p => p.PatientKey));
        Assert.Equal(["P-A"], day.Patients.Select(p => p.Parameters.PatientKey));
        Assert.Contains(
            "Ada Example (P-A) is the only patient who has an appointment or contact recorded on 2030-03-06. The other patient in the collection has none and is left out.",
            day.Notes);
    }

    [Fact]
    public void A_date_on_which_nobody_has_a_contact_gives_an_empty_result_that_says_so()
    {
        using var collection = TwoPatients();

        var day = collection.Calculations.DayAcrossPatients("2030-03-08");

        Assert.Empty(day.Summary);
        Assert.Empty(day.Patients);
        Assert.Contains("No patient in the collection has an appointment or contact recorded on 2030-03-08. The collection holds 2 patients.", day.Notes);
    }

    [Fact]
    public void With_more_patients_fitting_than_the_limit_the_day_is_a_summary_with_no_detail()
    {
        using var collection = new Collection();
        var count = Calculations.FullDetailLimit + 1;
        for (var i = 1; i <= count; i++)
            collection.Add($"P-{i:00}", $"Patient {i:00}",
            [
                Plan(1, 45, "2030-03-04", "2030-03-17"),
                .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            ]);
        collection.Reconcile();

        var day = collection.Calculations.DayAcrossPatients("2030-03-04");

        Assert.False(day.FullDetailGiven);
        Assert.Equal(count, day.PatientsWhoFit);
        Assert.Equal(count, day.Summary.Count);
        Assert.All(day.Summary, p => Assert.Equal((1, 50, 50), (p.TherapyContacts, p.MinutesMin, p.MinutesMax)));
        Assert.Empty(day.Patients);
        Assert.Contains(day.Notes, n => n.Contains($"more than the limit of {Calculations.FullDetailLimit} for full detail") && n.Contains("name one patient"));

        // The rendered result is one table. No patient has a section of their own.
        var text = Markdown.Render(day);
        Assert.Contains("| Patient 11 (P-11) | 1 | 1 | 50 |", text);
        Assert.DoesNotContain("#", text);

        var who = collection.Calculations.PatientsInPeriod("2030-03-04", "2030-03-10");
        Assert.False(who.FullDetailAllowed);
        Assert.Equal(count, who.Patients.Count);

        var summary = collection.Calculations.CollectionSummary(null, null);
        Assert.False(summary.FullDetailAllowed);
        Assert.Equal(count, summary.Patients.Count);
    }

    [Fact]
    public void With_exactly_the_limit_fitting_every_patient_is_shown_in_detail()
    {
        using var collection = new Collection();
        for (var i = 1; i <= Calculations.FullDetailLimit; i++)
            collection.Add($"P-{i:00}", $"Patient {i:00}",
            [
                Plan(1, 45, "2030-03-04", "2030-03-17"),
                .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            ]);
        collection.Reconcile();

        var day = collection.Calculations.DayAcrossPatients("2030-03-04");

        Assert.True(day.FullDetailGiven);
        Assert.Equal(Calculations.FullDetailLimit, day.Patients.Count);
        Assert.Contains("\n#### Patient 10 (P-10)\n", Markdown.Render(day));
    }

    // ---------- who fits ----------

    [Fact]
    public void Patients_are_listed_for_a_period_only_when_they_have_a_contact_in_it()
    {
        using var collection = TwoPatients();

        // Ada has contacts on the Monday and Tuesday of week two, and Ben on the Monday.
        var weekTwo = collection.Calculations.PatientsInPeriod("2030-03-11", "2030-03-17");
        Assert.Equal([("P-A", 2, 2), ("P-B", 1, 1)], weekTwo.Patients.Select(p => (p.PatientKey, p.ContactsRecorded, p.DaysWithContacts)));

        var tuesday = collection.Calculations.PatientsInPeriod("2030-03-12", "2030-03-12");
        Assert.Equal(["P-A"], tuesday.Patients.Select(p => p.PatientKey));
        Assert.Equal(2, tuesday.PatientsInCollection);
    }

    [Fact]
    public void A_date_that_cannot_be_read_is_an_error_and_not_a_silent_default()
    {
        using var collection = TwoPatients();

        Assert.Throws<InvalidOperationException>(() => collection.Calculations.PatientsInPeriod("2030-02-30", null));
        Assert.Throws<InvalidOperationException>(() => collection.Calculations.CollectionSummary("2030-03-17", "2030-03-04"));
        Assert.Throws<InvalidOperationException>(() => collection.Calculations.DayAcrossPatients("the fourth"));
    }

    // ---------- the collection summary ----------

    [Fact]
    public void The_collection_summary_has_one_row_per_patient_with_the_right_totals()
    {
        using var collection = TwoPatients();

        var summary = collection.Calculations.CollectionSummary("2030-03-04", "2030-03-17");

        Assert.Equal(["P-A", "P-B"], summary.Patients.Select(p => p.PatientKey));
        var (ada, ben) = (summary.Patients[0], summary.Patients[1]);

        Assert.Equal(new Dictionary<string, int> { ["Group therapy"] = 1, ["Individual therapy"] = 2 }, ada.SessionsByCategory);
        Assert.Equal((3, 3), (ada.TotalSessions, ada.TotalSessionsMax));
        Assert.Equal((3, 3), (ada.DistinctDays, ada.DistinctDaysMax));
        Assert.Equal((155, 155), (ada.MinutesMin, ada.MinutesMax));
        Assert.Equal((2, 1, 1, 0), (ada.WeeksExamined, ada.WeeksMet, ada.WeeksNotMet, ada.WeeksUndetermined));

        Assert.Equal(new Dictionary<string, int> { ["Family therapy"] = 1, ["Individual therapy"] = 1 }, ben.SessionsByCategory);
        Assert.Equal((2, 2), (ben.TotalSessions, ben.TotalSessionsMax));
        Assert.Equal((2, 2), (ben.DistinctDays, ben.DistinctDaysMax));
        Assert.Equal((85, 95), (ben.MinutesMin, ben.MinutesMax));
        Assert.Equal((2, 1, 0, 1), (ben.WeeksExamined, ben.WeeksMet, ben.WeeksNotMet, ben.WeeksUndetermined));

        var text = Markdown.Render(summary);
        Assert.Contains("| Patient | Period examined | Family therapy | Group therapy | Individual therapy | Total sessions | Distinct therapy days | Minutes |", text);
        Assert.Contains("| Ada Example (P-A) | 2030-03-04 to 2030-03-17 | 0 | 1 | 2 | 3 | 3 | 155 |", text);
        Assert.Contains("| Ben Example (P-B) | 2030-03-04 to 2030-03-17 | 1 | 0 | 1 | 2 | 2 | 85 to 95 |", text);
    }

    [Fact]
    public void Each_row_of_the_summary_matches_that_patients_own_calculations()
    {
        using var collection = TwoPatients();

        foreach (var row in collection.Calculations.CollectionSummary("2030-03-04", "2030-03-17").Patients)
        {
            var sessions = collection.Calculations.SessionCounts(row.PatientKey, "2030-03-04", "2030-03-17");
            var weekly = collection.Calculations.Weekly(row.PatientKey, "2030-03-04", "2030-03-17");

            Assert.Equal(sessions.SessionsByCategory, row.SessionsByCategory);
            Assert.Equal((sessions.TotalSessions, sessions.TotalSessionsMax), (row.TotalSessions, row.TotalSessionsMax));
            Assert.Equal((sessions.DistinctDays, sessions.DistinctDaysMax), (row.DistinctDays, row.DistinctDaysMax));
            Assert.Equal((weekly.TotalMinutesMin, weekly.TotalMinutesMax), (row.MinutesMin, row.MinutesMax));
            Assert.Equal(weekly.Weeks.Count(w => w.Result == WeeklyCalculator.Met), row.WeeksMet);
            Assert.Equal(weekly.Weeks.Count(w => w.Result == WeeklyCalculator.NotMet), row.WeeksNotMet);
            Assert.Equal(weekly.Weeks.Count(w => w.Result == WeeklyCalculator.Undetermined), row.WeeksUndetermined);
        }
    }

    [Fact]
    public void With_no_dates_the_summary_examines_each_patient_over_their_own_episode()
    {
        using var collection = new Collection();
        collection.Add("P-A", "Ada Example",
        [
            Plan(1, 45, "2030-03-04", "2030-03-17"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
        ]);

        // Ben's episode begins a week later. His contact on 5 March is before it and is not examined.
        collection.Add("P-B", "Ben Example",
        [
            Plan(1, 45, "2030-03-11", "2030-03-24"),
            .. Session("E0", Monday.AddDays(1), "individual_therapy", "09:00", "09:50"),
            .. Session("E1", Monday.AddDays(7), "individual_therapy", "09:00", "09:50"),
            .. Session("E2", Monday.AddDays(14), "group_therapy", "10:00", "11:00"),
        ]);
        collection.Reconcile();

        var summary = collection.Calculations.CollectionSummary(null, null);

        Assert.Equal(2, summary.PatientsWhoFit);
        Assert.Equal([("2030-03-04", "2030-03-17"), ("2030-03-11", "2030-03-24")], summary.Patients.Select(p => (p.From, p.To)));
        Assert.Equal([(1, 1, 50), (2, 2, 110)], summary.Patients.Select(p => (p.TotalSessions, p.TotalSessionsMax, p.MinutesMin)));
        Assert.Contains("No dates were given, so every patient in the collection is covered, each over their own episode. The collection holds 2 patients.", summary.Notes);
    }

    [Fact]
    public void With_dates_the_summary_leaves_out_a_patient_with_no_contact_in_the_period()
    {
        using var collection = TwoPatients();

        // Only Ada has a contact on the Wednesday of week one.
        var summary = collection.Calculations.CollectionSummary("2030-03-05", "2030-03-08");

        Assert.Equal(2, summary.PatientsInCollection);
        var ada = Assert.Single(summary.Patients);
        Assert.Equal("P-A", ada.PatientKey);
        Assert.Equal((1, 60), (ada.TotalSessions, ada.MinutesMin));
        Assert.Contains(summary.Notes, n => n.EndsWith("The other patient in the collection has none and is left out."));
    }

    [Fact]
    public void The_summary_of_the_supplied_record_matches_the_reference()
    {
        var reference = Json.Read<Evaluation.Reference>(File.ReadAllText(Path.Combine(SuppliedRecordTests.Root, "tests", "reference", "supplied-record.json")));
        var saved = Json.Read<List<Assertion>>(File.ReadAllText(Path.Combine(SuppliedRecordTests.Root, "abstraction", "export", "assertions.json")))
            .Where(a => a.PatientKey == reference.PatientKey)
            .ToList();

        using var collection = new Collection();
        collection.AddSaved(reference.PatientKey, "Rowan Mercer", saved);
        collection.Reconcile();

        var row = Assert.Single(collection.Calculations.CollectionSummary(reference.From, reference.To).Patients);

        Assert.Equal(reference.Sessions.OrderBy(s => s.Key, StringComparer.Ordinal), row.SessionsByCategory.OrderBy(s => s.Key, StringComparer.Ordinal));
        Assert.Equal(reference.TotalSessions, Markdown.Range(row.TotalSessions, row.TotalSessionsMax));
        Assert.Equal(reference.DistinctDays, Markdown.Range(row.DistinctDays, row.DistinctDaysMax));
        Assert.Equal((reference.Weeks.Sum(w => w.MinutesMin), reference.Weeks.Sum(w => w.MinutesMax)), (row.MinutesMin, row.MinutesMax));
        Assert.Equal(reference.Weeks.Count(w => w.Result == WeeklyCalculator.Met), row.WeeksMet);
        Assert.Equal(reference.Weeks.Count(w => w.Result == WeeklyCalculator.NotMet), row.WeeksNotMet);
        Assert.Equal(reference.Weeks.Count(w => w.Result == WeeklyCalculator.Undetermined), row.WeeksUndetermined);
    }
}

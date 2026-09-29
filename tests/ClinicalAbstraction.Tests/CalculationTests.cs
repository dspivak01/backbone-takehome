using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Ingest;
using ClinicalAbstraction.Questions;
using ClinicalAbstraction.Reconciliation;

namespace ClinicalAbstraction.Tests;

public class IntervalTests
{
    [Fact]
    public void Subtracting_a_break_inside_the_presence_removes_exactly_the_break()
    {
        var minutes = Intervals.TotalMinutes(Intervals.Subtract([new Span(600, 690)], [new Span(645, 660)]));
        Assert.Equal(75, minutes);
    }

    [Fact]
    public void A_break_outside_the_presence_removes_nothing()
    {
        var minutes = Intervals.TotalMinutes(Intervals.Subtract([new Span(660, 690)], [new Span(645, 660)]));
        Assert.Equal(30, minutes);
    }

    [Fact]
    public void A_break_that_overlaps_the_start_removes_only_the_overlap()
    {
        var minutes = Intervals.TotalMinutes(Intervals.Subtract([new Span(650, 690)], [new Span(645, 660)]));
        Assert.Equal(30, minutes);
    }

    [Fact]
    public void Overlapping_spans_are_not_counted_twice()
    {
        Assert.Equal(60, Intervals.TotalMinutes([new Span(600, 640), new Span(620, 660)]));
    }
}

public class GoalTests
{
    [Theory]
    [InlineData(150, 150, 150, "met")]
    [InlineData(155, 160, 150, "met")]
    [InlineData(140, 149, 150, "not_met")]
    [InlineData(145, 155, 150, "cannot_be_determined")]
    [InlineData(149, 150, 150, "cannot_be_determined")]
    public void A_goal_is_judged_from_the_lowest_and_highest_possible_values(int min, int max, int goal, string expected)
    {
        Assert.Equal(expected, WeeklyCalculator.Compare(min, max, goal));
    }

    [Theory]
    [InlineData("met", "met", "met")]
    [InlineData("met", "not_met", "not_met")]
    [InlineData("not_met", "cannot_be_determined", "not_met")]
    [InlineData("met", "cannot_be_determined", "cannot_be_determined")]
    public void A_week_fails_if_either_criterion_fails(string days, string minutes, string expected)
    {
        Assert.Equal(expected, WeeklyCalculator.Combine(days, minutes));
    }

    [Fact]
    public void Weeks_run_monday_to_sunday()
    {
        Assert.Equal(new DateOnly(2030, 3, 4), WeeklyCalculator.WeekStart(new DateOnly(2030, 3, 10), "monday_sunday"));
        Assert.Equal(new DateOnly(2030, 3, 4), WeeklyCalculator.WeekStart(new DateOnly(2030, 3, 4), "monday_sunday"));
        Assert.Equal(new DateOnly(2030, 3, 11), WeeklyCalculator.WeekStart(new DateOnly(2030, 3, 11), "monday_sunday"));
    }
}

public class WeeklyTests
{
    private static Assertion[] Session(string id, DateOnly date, string category, string start, string end) =>
    [
        Build.Encounter($"NOTE-{id}", category, encounter: id, date: date),
        Build.Contact($"NOTE-{id}", start, end, encounter: id, date: date),
    ];

    private static readonly DateOnly Monday = new(2030, 3, 4);

    [Fact]
    public void Medication_visits_and_collateral_contacts_add_no_minutes()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 3, 150, "2030-03-04", "2030-03-31"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            .. Session("E2", Monday.AddDays(1), "medication_management", "09:00", "09:25"),
            .. Session("E3", Monday.AddDays(2), "collateral_contact", "14:00", "14:40"),
        ]);

        var week = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(6)).Single();
        Assert.Equal(50, week.MinutesMin);
        Assert.Equal(50, week.MinutesMax);
        Assert.Equal(1, week.DaysMin);
        Assert.Equal("not_met", week.Result);
    }

    [Fact]
    public void Two_sessions_on_one_day_are_one_therapy_day()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 3, 150, "2030-03-04", "2030-03-31"),
            .. Session("E1", Monday, "group_therapy", "10:00", "11:00"),
            .. Session("E2", Monday, "individual_therapy", "11:15", "11:45"),
        ]);

        var week = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(6)).Single();
        Assert.Equal(90, week.MinutesMin);
        Assert.Equal(1, week.DaysMin);
    }

    [Fact]
    public void A_threshold_inside_the_range_cannot_be_determined()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 3, 150, "2030-03-04", "2030-03-31"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:50"),
            Build.Contact("NOTE-E1-SECOND", "09:10", "09:50", encounter: "E1", date: Monday),
            .. Session("E2", Monday.AddDays(2), "group_therapy", "10:00", "11:15"),
            .. Session("E3", Monday.AddDays(4), "family_therapy", "13:15", "13:45"),
        ]);

        var week = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(6)).Single();
        Assert.Equal(145, week.MinutesMin);
        Assert.Equal(155, week.MinutesMax);
        Assert.Equal(3, week.DaysMin);
        Assert.Equal("cannot_be_determined", week.Result);
        Assert.NotEmpty(week.Needs);
    }

    [Fact]
    public void A_service_the_plan_does_not_mention_is_shown_both_ways()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 1, 60, "2030-03-04", "2030-03-31"),
            .. Session("E1", Monday, "other", "09:00", "10:00"),
        ]);

        var week = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(6)).Single();
        Assert.Equal(0, week.MinutesMin);
        Assert.Equal(60, week.MinutesMax);
        Assert.Equal("cannot_be_determined", week.Result);
    }

    [Fact]
    public void The_end_date_of_a_range_is_included()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 1, 30, "2030-03-04", "2030-03-31"),
            .. Session("E1", Monday.AddDays(4), "individual_therapy", "09:00", "09:45"),
        ]);

        var week = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(4)).Single();
        Assert.Equal(45, week.MinutesMin);
        Assert.True(week.PartialWeek);
    }

    [Fact]
    public void Each_contact_is_judged_by_the_plan_in_effect_on_its_own_date()
    {
        // The first plan counts group therapy. The second, from the following Monday, does not.
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN-1", 1, 60, "2030-03-04"),
            Build.Plan("PLAN-2", 2, 120, "2030-03-11", "2030-03-31", counted: ["individual_therapy"], excluded: ["group_therapy"]),
            .. Session("E1", Monday, "group_therapy", "10:00", "11:00"),
            .. Session("E2", Monday.AddDays(7), "group_therapy", "10:00", "11:00"),
            .. Session("E3", Monday.AddDays(8), "individual_therapy", "09:00", "09:45"),
        ]);

        Assert.Equal(2, patient.Plans.Count);
        Assert.Equal(new DateOnly(2030, 3, 10), patient.Plans[0].EffectiveTo);

        var weeks = WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(13));
        Assert.Equal(60, weeks[0].MinutesMin);
        Assert.Equal("met", weeks[0].Result);
        Assert.Equal(45, weeks[1].MinutesMin);
        Assert.Equal(120, weeks[1].RequiredMinutes);
        Assert.Equal("not_met", weeks[1].Result);
    }

    [Fact]
    public void Two_weeks_below_the_goal_in_a_row_include_the_patient()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 1, 60, "2030-03-04", "2030-03-24"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:30"),
            .. Session("E2", Monday.AddDays(7), "individual_therapy", "09:00", "09:30"),
            .. Session("E3", Monday.AddDays(14), "individual_therapy", "09:00", "10:00"),
        ]);

        var summary = Calculations.Consecutive(patient, WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(20)));
        Assert.Equal("included", summary.Status);
        Assert.Single(summary.DefinitePairs);
    }

    [Fact]
    public void A_patient_whose_second_week_is_unsettled_depends_on_documentation()
    {
        var patient = Build.Reconcile(
        [
            Build.Plan("PLAN", 1, 45, "2030-03-04", "2030-03-17"),
            .. Session("E1", Monday, "individual_therapy", "09:00", "09:30"),
            .. Session("E2", Monday.AddDays(7), "individual_therapy", "09:00", "09:50"),
            Build.Contact("NOTE-E2-SECOND", "09:10", "09:50", encounter: "E2", date: Monday.AddDays(7)),
        ]);

        var summary = Calculations.Consecutive(patient, WeeklyCalculator.Weeks(patient, Monday, Monday.AddDays(13)));
        Assert.Equal("depends_on_unresolved_documentation", summary.Status);
        Assert.NotEmpty(summary.PossiblePairs.Single().WouldBeSettledBy);
    }
}

public class SupportingTests
{
    [Fact]
    public void A_quote_is_found_even_when_the_model_changed_the_dash_style()
    {
        string[] lines = ["Scheduled group: 10:00–11:30. Nontherapeutic break: 10:45–11:00."];
        var check = QuoteVerifier.Check(lines, 1, 1, "Scheduled group: 10:00-11:30");
        Assert.True(check.Found);
        Assert.Equal("verified", check.Status);
    }

    [Fact]
    public void A_quote_at_the_wrong_line_is_moved_to_the_right_one()
    {
        string[] lines = ["Header", "", "Patient arrival: 10:00 | Patient departure: 11:30"];
        var check = QuoteVerifier.Check(lines, 1, 1, "Patient departure: 11:30");
        Assert.True(check.Found);
        Assert.Equal(3, check.LineStart);
        Assert.Equal("relocated", check.Status);
    }

    [Fact]
    public void A_quote_that_is_not_in_the_document_is_rejected()
    {
        string[] lines = ["Patient arrival: 10:00"];
        Assert.False(QuoteVerifier.Check(lines, 1, 1, "Patient departure: 11:30").Found);
    }

    private static readonly Storage.StoredDocument Roster = new(
        "hash", "raw", "DOC-1", "roster.txt",
        "Visit V-1 on 2030-03-04\nIn 10:00 | Out 11:30 | Attended\nVisit V-2 on 2030-03-05\nIn 09:00 | Out 09:45 | Attended", 4, 100);

    [Fact]
    public void A_list_returned_as_text_is_still_read()
    {
        var output = """
            {"patient":{"name":"Test","date_of_birth":"1990-01-01","mrn":"P-1"},
             "assertions":"[{\"kind\":\"presence\",\"line_start\":2,\"line_end\":2,\"quote\":\"In 10:00 | Out 11:30\",\"record_type\":\"attendance_record\",\"signed\":\"yes\",\"arrival\":\"10:00\",\"departure\":\"11:30\"}]"}
            """;
        var (accepted, rejected) = Extractor.Read(Roster, output);
        Assert.Single(accepted);
        Assert.Empty(rejected);
    }

    [Fact]
    public void One_malformed_object_costs_that_object_and_not_the_document()
    {
        // The second object is broken in the way a real run broke: stray text in the middle of a value.
        var output = """
            {"assertions":"[{\"kind\":\"presence\",\"line_start\":2,\"line_end\":2,\"quote\":\"In 10:00 | Out 11:30\",\"record_type\":\"attendance_record\",\"signed\":\"yes\",\"arrival\":\"10:00\"},\n{\"kind\":\"presence\",\"line_start\":4,\"line_end\":4,\"quote\":\"In 09:00\",\"encounter_id\":\"V-9\".replace? \"V-2\",\"arrival\":\"09:00\"},\n{\"kind\":\"attendance\",\"line_start\":4,\"line_end\":4,\"quote\":\"Attended\",\"record_type\":\"attendance_record\",\"signed\":\"yes\",\"disposition\":\"attended\"}]"}
            """;
        var (accepted, rejected) = Extractor.Read(Roster, output);
        Assert.Equal(2, accepted.Count);
        Assert.Single(rejected);
        Assert.StartsWith("malformed output", rejected[0]);
    }

    [Fact]
    public void Line_endings_and_trailing_spaces_do_not_make_a_new_document()
    {
        var unix = DocumentRegistry.Normalise("Line one\nLine two  \n");
        var windows = DocumentRegistry.Normalise("Line one\r\nLine two\r\n\r\n");
        Assert.Equal(unix, windows);
    }

    [Fact]
    public void A_number_the_calculations_did_not_produce_is_reported()
    {
        var check = NarrativeChecks.Check(
            "The week had 140 minutes [DOC-1 L8], about 93 percent of the goal [DOC-9 L2].",
            "How many minutes?",
            ["""{"minutes_min":140,"required_minutes":150,"citations":["DOC-1 L8"]}"""]);

        Assert.Equal(["93"], check.NumbersNotInResults);
        Assert.Equal(["DOC-9 L2"], check.CitationsNotInResults);
        Assert.Equal(1, check.CitationsVerified);
    }
}

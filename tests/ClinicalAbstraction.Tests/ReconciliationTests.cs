using ClinicalAbstraction.Domain;

namespace ClinicalAbstraction.Tests;

/// <summary>One test per reconciliation rule, each on a small made-up record.</summary>
public class ReconciliationTests
{
    [Fact]
    public void Several_documents_about_one_encounter_make_one_event()
    {
        var abstraction = Build.Reconcile(
            Build.Encounter("NOTE-A", "family_therapy"),
            Build.Contact("NOTE-A", "14:00", "14:45"),
            Build.Encounter("NOTE-B", "family_therapy"),
            Build.Contact("NOTE-B", "14:00", "14:45"));

        var e = Assert.Single(abstraction.Encounters);
        Assert.Equal("session", e.Occurrence);
        Assert.Equal(45, e.MinutesMin);
        Assert.Equal(45, e.MinutesMax);
        Assert.Equal(2, e.SourceDocuments.Count);
    }

    [Fact]
    public void Minutes_come_from_presence_less_the_break_not_from_the_scheduled_slot()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Break("NOTE", "10:45", "11:00"),
            Build.Roster("ROSTER", "10:15", "11:15", "attended_in_part"));

        Assert.Equal(45, e.MinutesMin);
        Assert.Equal(45, e.MinutesMax);
    }

    [Fact]
    public void Roster_times_outside_the_session_are_limited_to_the_session()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Roster("ROSTER", "09:40", "11:45"));

        Assert.Equal(90, e.MinutesMax);
    }

    [Fact]
    public void A_clinicians_stated_contact_interval_is_never_limited_to_the_booked_slot()
    {
        var e = Build.Single(
            Build.Encounter("EXPORT", "individual_therapy", "09:00", "09:45", recordType: "appointment_export", signed: "no"),
            Build.Contact("NOTE", "09:00", "09:55"));

        Assert.Equal(55, e.MinutesMin);
    }

    [Fact]
    public void A_correction_replaces_the_field_it_names()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Roster("ROSTER", "10:00", "11:30", signedAt: "2030-03-04T12:00"),
            Build.Assertion("FIX", 7, "correction", f =>
            {
                f.CorrectedField = "departure";
                f.OldValue = "11:30";
                f.NewValue = "11:15";
            }, "correction", signedAt: "2030-03-05T08:00"));

        Assert.Equal("resolved", e.End.Status);
        Assert.Equal("11:15", e.End.Value);
        Assert.Equal("10:00", e.Start.Value);
        Assert.Equal(75, e.MinutesMin);
        Assert.Contains(e.End.Overruled, o => o.Rule == "R3" && o.Value == "11:30");
    }

    [Fact]
    public void A_stale_copy_received_after_the_correction_does_not_bring_the_old_value_back()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Roster("ROSTER", "10:00", "11:30", signedAt: "2030-03-04T12:00"),
            Build.Assertion("FIX", 7, "correction", f =>
            {
                f.CorrectedField = "departure";
                f.OldValue = "11:30";
                f.NewValue = "11:15";
            }, "correction", signedAt: "2030-03-05T08:00"),
            Build.Roster("RESENT", "10:00", "11:30", isCopy: true, originalSignedAt: "2030-03-04T12:00"));

        Assert.Equal("resolved", e.End.Status);
        Assert.Equal("11:15", e.End.Value);
        Assert.Equal(2, e.End.Overruled.Count(o => o.Rule == "R3"));
    }

    [Fact]
    public void The_copy_and_the_correction_are_enough_when_the_original_was_never_supplied()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Assertion("FIX", 7, "correction", f =>
            {
                f.CorrectedField = "departure";
                f.OldValue = "11:30";
                f.NewValue = "11:15";
            }, "correction", signedAt: "2030-03-05T08:00"),
            Build.Roster("RESENT", "10:00", "11:30", isCopy: true, originalSignedAt: "2030-03-04T12:00"));

        Assert.Equal("11:15", e.End.Value);
        Assert.Equal(75, e.MinutesMin);
    }

    [Fact]
    public void A_correction_with_no_original_stands_and_is_flagged()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "individual_therapy"),
            Build.Assertion("NOTE", 3, "presence", f => f.Arrival = "09:00"),
            Build.Assertion("FIX", 7, "correction", f =>
            {
                f.CorrectedField = "end";
                f.OldValue = "09:50";
                f.NewValue = "09:40";
            }, "correction", signedAt: "2030-03-05T08:00"));

        Assert.Equal("09:40", e.End.Value);
        Assert.Contains(e.Discrepancies, d => d.Rule == "R3" && d.Description.Contains("not supplied"));
    }

    [Fact]
    public void Two_signed_records_that_disagree_stay_unresolved_and_minutes_become_a_range()
    {
        var e = Build.Single(
            Build.Encounter("NOTE-A", "individual_therapy"),
            Build.Contact("NOTE-A", "09:00", "09:50"),
            Build.Contact("NOTE-B", "09:10", "09:50", signedAt: "2030-03-04T17:00"));

        Assert.Equal("unresolved", e.Start.Status);
        Assert.Equal("resolved", e.End.Status);
        Assert.Equal("session", e.Occurrence);
        Assert.Equal(40, e.MinutesMin);
        Assert.Equal(50, e.MinutesMax);
        Assert.NotEmpty(e.Needs);
    }

    [Fact]
    public void A_later_signed_record_does_not_override_an_earlier_one()
    {
        // The second note is signed a day later. With no correction, neither wins.
        var e = Build.Single(
            Build.Encounter("NOTE-A", "individual_therapy"),
            Build.Contact("NOTE-A", "09:00", "09:50", signedAt: "2030-03-04T11:00"),
            Build.Contact("NOTE-B", "09:10", "09:50", signedAt: "2030-03-05T11:00"));

        Assert.Equal("unresolved", e.Start.Status);
    }

    [Fact]
    public void An_independent_lower_tier_record_settles_a_tie_between_signed_records()
    {
        var e = Build.Single(
            Build.Encounter("NOTE-A", "individual_therapy"),
            Build.Contact("NOTE-A", "09:00", "09:50"),
            Build.Contact("NOTE-B", "09:10", "09:50"),
            Build.Assertion("CHECKIN", 5, "presence", f => f.Arrival = "09:10", "desk_log", "no", signedAt: null, recordedAt: "2030-03-04T09:10"));

        Assert.Equal("resolved_by_corroboration", e.Start.Status);
        Assert.Equal("09:10", e.Start.Value);
        Assert.Equal(40, e.MinutesMin);
        Assert.Equal(40, e.MinutesMax);
    }

    [Fact]
    public void A_signed_no_show_beats_a_draft_and_a_charge_and_both_are_reported()
    {
        var e = Build.Single(
            Build.Encounter("REGISTER", "group_therapy", "10:00", "11:30", recordType: "attendance_record"),
            Build.Assertion("REGISTER", 10, "attendance", f => { f.Disposition = "no_show"; f.PatientPresent = "no"; }, "attendance_record"),
            Build.Assertion("EXTRACT", 9, "attendance", f => { f.Disposition = "attended_in_full"; f.PatientPresent = "yes"; },
                "draft_note", "no", signedAt: null, recordedAt: "2030-03-04T09:45"),
            Build.Assertion("EXTRACT", 19, "charge", f => { f.Description = "Group psychotherapy"; f.Quantity = 1; },
                "billing_charge", "no", signedAt: null, recordedAt: "2030-03-04T18:00"));

        Assert.Equal("not_session", e.Occurrence);
        Assert.Equal(0, e.MinutesMax);
        Assert.Contains(e.Disposition.Overruled, o => o.Rule == "R6");
        Assert.Contains(e.Discrepancies, d => d.Description.Contains("charge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(e.Discrepancies, d => d.Description.Contains("draft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_cancellation_recorded_before_the_appointment_is_kept()
    {
        var e = Build.Single(
            Build.Encounter("NOTICE", "group_therapy", "10:00", "11:30", recordType: "administrative_notice", signed: "no"),
            Build.Assertion("NOTICE", 9, "attendance", f => f.Disposition = "clinic_cancelled",
                "administrative_notice", "no", signedAt: null, recordedAt: "2030-03-04T08:12"));

        Assert.Equal("not_session", e.Occurrence);
        Assert.Equal("clinic_cancelled", e.Disposition.Value);
    }

    [Fact]
    public void Completed_with_the_patient_absent_is_not_a_session()
    {
        var e = Build.Single(
            Build.Encounter("EXPORT", "family_therapy", "14:00", "14:40", recordType: "appointment_export", signed: "no"),
            Build.Assertion("EXPORT", 18, "attendance", f => { f.Disposition = "completed"; f.PatientPresent = "not_stated"; }, "appointment_export", "no"),
            Build.Encounter("NOTE", "family_therapy"),
            Build.Assertion("NOTE", 8, "attendance", f => f.PatientPresent = "no"));

        Assert.Equal("not_session", e.Occurrence);
        Assert.Equal(0, e.MinutesMax);
        Assert.Equal("The patient was not present.", e.NotSessionReason);
    }

    [Fact]
    public void An_appointment_known_only_from_an_export_is_uncertain_not_counted_in_full()
    {
        var e = Build.Single(
            Build.Encounter("EXPORT", "individual_therapy", "09:00", "09:50", recordType: "appointment_export", signed: "no"),
            Build.Assertion("EXPORT", 10, "attendance", f => f.Disposition = "completed", "appointment_export", "no"));

        Assert.Equal("uncertain", e.Occurrence);
        Assert.Equal(0, e.MinutesMin);
        Assert.Equal(50, e.MinutesMax);
    }

    [Fact]
    public void Only_the_patients_own_part_of_a_shared_session_counts()
    {
        var e = Build.Single(
            Build.Assertion("NOTE", 3, "encounter", f => { f.ServiceCategory = "family_therapy"; f.SessionStart = "13:00"; f.SessionEnd = "13:45"; }),
            Build.Assertion("NOTE", 9, "attendance", f => f.PatientPresent = "no"),
            Build.Assertion("NOTE", 11, "attendance", f => { f.PatientPresent = "yes"; f.Disposition = "attended_in_part"; }),
            Build.Contact("NOTE", "13:15", "13:45"));

        Assert.Equal("session", e.Occurrence);
        Assert.Equal(30, e.MinutesMin);
        Assert.Equal(30, e.MinutesMax);
    }

    [Fact]
    public void A_document_that_contradicts_itself_and_gives_no_times_is_left_unresolved()
    {
        // A header filled in by a template says attended. The body says the patient did not attend.
        var e = Build.Single(
            Build.Encounter("NOTE", "individual_therapy", "09:00", "09:50"),
            Build.Assertion("NOTE", 5, "attendance", f => { f.PatientPresent = "yes"; f.Disposition = "attended"; }),
            Build.Assertion("NOTE", 12, "attendance", f => f.PatientPresent = "no"));

        Assert.Equal("unresolved", e.PatientPresent.Status);
        Assert.Equal("uncertain", e.Occurrence);
        Assert.Equal(0, e.MinutesMin);
        Assert.Equal(50, e.MinutesMax);
        Assert.NotEmpty(e.Needs);
    }

    [Fact]
    public void Two_call_records_for_one_visit_are_one_contact_with_a_gap()
    {
        var e = Build.Single(
            Build.Encounter("NOTE", "individual_therapy"),
            Build.Assertion("NOTE", 7, "presence", f => f.Intervals =
            [
                new IntervalText { Start = "13:00", End = "13:20" },
                new IntervalText { Start = "13:30", End = "13:55" },
            ]),
            Build.Assertion("NOTE", 17, "presence", f => f.Intervals = [new IntervalText { Start = "13:00", End = "13:20" }], "platform_log", "no", signedAt: null, recordedAt: "2030-03-04T14:06"),
            Build.Assertion("NOTE", 18, "presence", f => f.Intervals = [new IntervalText { Start = "13:30", End = "13:55" }], "platform_log", "no", signedAt: null, recordedAt: "2030-03-04T14:06"));

        Assert.Equal(45, e.MinutesMin);
        Assert.Equal(45, e.MinutesMax);
        Assert.DoesNotContain(e.Discrepancies, d => d.Rule == "R5");
    }

    [Fact]
    public void A_record_with_no_encounter_identifier_joins_the_one_encounter_it_matches()
    {
        var abstraction = Build.Reconcile(
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Break("NOTE", "10:45", "11:00"),
            Build.Roster("ROSTER", "10:00", "11:30", encounter: null),
            Build.Encounter("ROSTER", "group_therapy", encounter: null, recordType: "attendance_record"));

        var e = Assert.Single(abstraction.Encounters);
        Assert.Equal(75, e.MinutesMin);
        Assert.Empty(abstraction.Unlinked);
    }

    [Fact]
    public void A_record_with_no_identifier_that_fits_two_encounters_is_left_out_and_reported()
    {
        var abstraction = Build.Reconcile(
            Build.Encounter("NOTE-1", "individual_therapy", encounter: "ENC-1"),
            Build.Contact("NOTE-1", "09:00", "09:50", encounter: "ENC-1"),
            Build.Encounter("NOTE-2", "individual_therapy", encounter: "ENC-2"),
            Build.Contact("NOTE-2", "15:00", "15:30", encounter: "ENC-2"),
            Build.Assertion("LOG", 4, "presence", f => { f.Arrival = "15:05"; f.ServiceCategory = "individual_therapy"; }, "desk_log", "no", encounter: null));

        Assert.Equal(2, abstraction.Encounters.Count);
        Assert.Single(abstraction.Unlinked);
        Assert.Equal(50, abstraction.Encounters[0].MinutesMin);
        Assert.Equal(30, abstraction.Encounters[1].MinutesMin);
    }

    [Fact]
    public void Records_without_any_identifier_still_form_an_encounter()
    {
        var abstraction = Build.Reconcile(
            Build.Encounter("NOTE", "individual_therapy", encounter: null),
            Build.Contact("NOTE", "09:00", "09:45", encounter: null));

        var e = Assert.Single(abstraction.Encounters);
        Assert.Null(e.EncounterId);
        Assert.Equal(45, e.MinutesMin);
    }

    [Fact]
    public void The_order_assertions_arrive_in_does_not_change_the_result()
    {
        Assertion[] assertions =
        [
            Build.Encounter("NOTE", "group_therapy", "10:00", "11:30"),
            Build.Break("NOTE", "10:45", "11:00"),
            Build.Roster("ROSTER", "10:00", "11:30", signedAt: "2030-03-04T12:00"),
            Build.Assertion("FIX", 7, "correction", f => { f.CorrectedField = "departure"; f.OldValue = "11:30"; f.NewValue = "11:15"; }, "correction", signedAt: "2030-03-05T08:00"),
            Build.Roster("RESENT", "10:00", "11:30", isCopy: true, originalSignedAt: "2030-03-04T12:00"),
            Build.Encounter("NOTE-2", "individual_therapy", encounter: "ENC-2"),
            Build.Contact("NOTE-2", "11:15", "11:45", encounter: "ENC-2"),
            Build.Plan("PLAN", 3, 150, "2030-03-04", "2030-03-31"),
        ];

        var forward = Build.Reconcile(assertions);
        var reversed = Build.Reconcile([.. assertions.Reverse()]);

        Assert.Equal(forward.Version, reversed.Version);
        Assert.Equal(Json.Write(forward), Json.Write(reversed));
    }

    [Fact]
    public void A_copy_of_a_measure_is_not_a_second_assessment()
    {
        var abstraction = Build.Reconcile(
            Build.Assertion("REVIEW", 8, "measure", f => { f.Instrument = "PHQ-9"; f.TotalScore = 14; f.CompletedAt = "2030-03-10"; f.FormId = "Q-1"; },
                "measure_record", encounter: null, noDate: true),
            Build.Assertion("IMPORT", 13, "measure", f => { f.Instrument = "PHQ-9"; f.TotalScore = 14; f.CompletedAt = "2030-03-10"; f.FormId = "Q-1"; },
                "import_receipt", "no", isCopy: true, encounter: null, noDate: true),
            Build.Assertion("LATER", 11, "measure", f => { f.Instrument = "PHQ-9"; f.TotalScore = 14; f.CompletedAt = "2030-03-10"; f.IsRestatement = true; },
                "measure_record", encounter: null, noDate: true));

        var m = Assert.Single(abstraction.Measurements);
        Assert.Equal(14, m.TotalScore);
        Assert.Single(m.OriginalCitations);
        Assert.Equal(2, m.RepeatCitations.Count);
    }
}

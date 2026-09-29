using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Reconciliation;

namespace ClinicalAbstraction.Tests;

/// <summary>
/// Builds assertions by hand for tests. The scenarios are made up to exercise one rule each.
/// None of them is taken from the supplied patient record.
/// </summary>
internal static class Build
{
    private static long _nextId = 1;

    public static readonly DateOnly Day = new(2030, 3, 4); // a Monday

    private const string SameDay = "same day";

    public static Assertion Assertion(
        string document, int line, string kind, Action<AssertionFields> fields,
        string recordType = "clinical_note", string signed = "yes", string? signedAt = SameDay,
        string? recordedAt = null, bool isCopy = false, string? originalSignedAt = null,
        string? encounter = "ENC-1", DateOnly? date = null, bool noDate = false)
    {
        var f = new AssertionFields();
        fields(f);

        // By default a record is signed at 16:00 on the day of the service it describes.
        if (signedAt == SameDay) signedAt = $"{date ?? Day:yyyy-MM-dd}T16:00";
        return new Assertion
        {
            Id = _nextId++,
            DocHash = document,
            PrintedDocId = document,
            PatientKey = "P-1",
            EncounterId = encounter,
            ServiceDate = noDate ? null : date ?? Day,
            Kind = kind,
            Fields = f,
            LineStart = line,
            LineEnd = line,
            Quote = $"{document} line {line}",
            RecordType = recordType,
            Signed = signed,
            SignedAt = isCopy ? null : Parse.DateTimeLocal(signedAt),
            RecordedAt = Parse.DateTimeLocal(recordedAt),
            IsCopy = isCopy,
            OriginalSignedAt = Parse.DateTimeLocal(originalSignedAt),
        };
    }

    public static Assertion Encounter(string document, string category, string? scheduledStart = null, string? scheduledEnd = null,
        string recordType = "clinical_note", string signed = "yes", string? encounter = "ENC-1", DateOnly? date = null) =>
        Assertion(document, 1, "encounter", f =>
        {
            f.ServiceCategory = category;
            f.ScheduledStart = scheduledStart;
            f.ScheduledEnd = scheduledEnd;
        }, recordType, signed, encounter: encounter, date: date);

    public static Assertion Roster(string document, string arrival, string departure, string disposition = "attended",
        string? signedAt = "2030-03-04T12:00", bool isCopy = false, string? originalSignedAt = null, string? encounter = "ENC-1", DateOnly? date = null) =>
        Assertion(document, 2, "presence", f =>
        {
            f.Arrival = arrival;
            f.Departure = departure;
            f.Disposition = disposition;
            f.PatientPresent = "yes";
        }, "attendance_record", isCopy ? "extract_of_signed" : "yes", signedAt, isCopy: isCopy, originalSignedAt: originalSignedAt, encounter: encounter, date: date);

    public static Assertion Contact(string document, string start, string end, string? signedAt = SameDay, string? encounter = "ENC-1", DateOnly? date = null) =>
        Assertion(document, 3, "presence", f =>
        {
            f.Intervals = [new IntervalText { Start = start, End = end }];
            f.PatientPresent = "yes";
            f.Disposition = "completed";
        }, signedAt: signedAt, encounter: encounter, date: date);

    public static Assertion Break(string document, string start, string end, string? encounter = "ENC-1", DateOnly? date = null) =>
        Assertion(document, 4, "excluded_interval", f =>
        {
            f.Intervals = [new IntervalText { Start = start, End = end }];
            f.IntervalReason = "break";
        }, encounter: encounter, date: date);

    public static Assertion Plan(string document, int days, int minutes, string effectiveFrom, string? effectiveTo = null,
        string[]? counted = null, string[]? excluded = null, string? episodeStart = null, string? episodeEnd = null) =>
        Assertion(document, 1, "plan_requirement", f =>
        {
            f.MinTherapyDaysPerWeek = days;
            f.MinMinutesPerWeek = minutes;
            f.WeekDefinition = "monday_sunday";
            f.EffectiveFrom = effectiveFrom;
            f.EffectiveTo = effectiveTo;
            f.EpisodeStart = episodeStart;
            f.EpisodeEnd = episodeEnd;
            f.CountedCategories = [.. counted ?? ["individual_therapy", "group_therapy", "family_therapy"]];
            f.ExcludedCategories = [.. excluded ?? ["medication_management", "collateral_contact", "care_coordination"]];
        }, "treatment_plan", encounter: null, noDate: true, signedAt: effectiveFrom + "T09:00");

    /// <summary>
    /// Hands assertions to another patient. Every helper above builds for patient P-1, so a test
    /// with several patients builds each patient's assertions as usual and then passes them here.
    /// Document names gain the patient key, because two patients never share a document.
    /// </summary>
    public static Assertion[] ForPatient(string patientKey, params Assertion[] assertions)
    {
        foreach (var a in assertions)
        {
            a.PatientKey = patientKey;
            a.DocHash = $"{patientKey}-{a.DocHash}";
            a.PrintedDocId = a.DocHash;
        }
        return assertions;
    }

    public static PatientAbstraction Reconcile(params Assertion[] assertions) =>
        new Reconciler().Reconcile("P-1", "P-1", "Test Patient", "1990-01-01", assertions, assertions.Select(a => a.DocHash).Distinct().Count(), []);

    public static EncounterEvent Single(params Assertion[] assertions)
    {
        var abstraction = Reconcile(assertions);
        return Assert.Single(abstraction.Encounters);
    }
}

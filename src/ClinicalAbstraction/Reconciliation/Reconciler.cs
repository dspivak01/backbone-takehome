using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Reconciliation;

/// <summary>
/// Turns one patient's assertions into events. It uses no model and keeps no state, and it
/// rebuilds everything each time, so the result depends only on the assertions it is given,
/// never on the order documents arrived in.
/// </summary>
public sealed class Reconciler
{
    private sealed record Member(Assertion Assertion, string LinkMethod);

    public PatientAbstraction Reconcile(
        string patientKey, string? mrn, string? name, string? dateOfBirth,
        IEnumerable<Assertion> patientAssertions, int documentCount, IEnumerable<string> extractionWarnings)
    {
        // A fixed order, based on content, so the output never depends on arrival order.
        var assertions = patientAssertions
            .OrderBy(a => a.PrintedDocId ?? a.DocHash, StringComparer.Ordinal)
            .ThenBy(a => a.LineStart).ThenBy(a => a.LineEnd)
            .ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Quote, StringComparer.Ordinal)
            .ToList();

        var abstraction = new PatientAbstraction
        {
            PatientKey = patientKey, Mrn = mrn, Name = name, DateOfBirth = dateOfBirth,
            DocumentCount = documentCount, AssertionCount = assertions.Count,
        };
        abstraction.Warnings.AddRange(extractionWarnings);

        var groups = Link(assertions, abstraction.Unlinked);
        foreach (var (key, members) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            abstraction.Encounters.Add(BuildEncounter(patientKey, key, members));
        abstraction.Encounters = abstraction.Encounters
            .OrderBy(e => e.ServiceDate ?? DateOnly.MaxValue)
            .ThenBy(e => StartOf(e))
            .ThenBy(e => e.EventKey, StringComparer.Ordinal)
            .ToList();

        abstraction.Measurements = BuildMeasurements(patientKey, assertions);
        abstraction.Plans = BuildPlans(patientKey, assertions, abstraction.Warnings);
        abstraction.Episode = BuildEpisode(assertions, abstraction);
        abstraction.NonEncounterRecords = assertions
            .Where(a => a.Kind == "no_service_contact")
            .Select(a => new NonEncounterRecord
            {
                Citation = a.Citation, Kind = a.Kind, Date = a.ServiceDate,
                Description = a.Fields.WhatDidNotOccur ?? a.Fields.Summary ?? "", Quote = a.Quote,
            })
            .ToList();

        if (abstraction.Unlinked.Count > 0)
            abstraction.Warnings.Add($"{abstraction.Unlinked.Count} assertion(s) about service contacts could not be linked to an encounter. They are left out of all counts.");

        abstraction.Version = Hashing.Sha256(Json.Write(abstraction))[..16];
        return abstraction;
    }

    private static int StartOf(EncounterEvent e) =>
        Parse.ClockMinutes(e.Start.Value ?? e.Start.Candidates.FirstOrDefault()?.Value) ?? e.SessionInterval?.Start ?? 0;

    // ---------- R1: link ----------

    /// <summary>
    /// Groups assertions about service contacts into encounters. The encounter identifier is used when
    /// present. Without one, an assertion joins the single encounter that matches its date and service
    /// category, or starts an encounter of its own. An assertion that fits several is left unlinked.
    /// A break that names no session and no date is a statement about every group session its
    /// document describes, and joins each of them.
    /// </summary>
    private static Dictionary<string, List<Member>> Link(List<Assertion> assertions, List<UnlinkedAssertion> unlinked)
    {
        var groups = new Dictionary<string, List<Member>>(StringComparer.Ordinal);
        var related = assertions.Where(a => Vocabulary.EncounterKinds.Contains(a.Kind)).ToList();

        foreach (var a in related.Where(a => a.EncounterId is not null))
        {
            var key = a.EncounterId!.Trim().ToUpperInvariant();
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
            members.Add(new Member(a, "encounter_id"));
        }

        var profile = groups.ToDictionary(
            g => g.Key,
            g => (Date: MostCommon(g.Value.Select(m => m.Assertion.ServiceDate)), Category: MostCommon(g.Value.Select(m => m.Assertion.Fields.ServiceCategory))));

        var undatedBreaks = new List<Assertion>();
        foreach (var a in related.Where(a => a.EncounterId is null))
        {
            // When the statement carries no category, take it from the same document's description of that date.
            var category = a.Fields.ServiceCategory ?? MostCommon(related
                .Where(o => o.DocHash == a.DocHash && o.ServiceDate == a.ServiceDate)
                .Select(o => o.Fields.ServiceCategory));

            if (a.ServiceDate is null)
            {
                // A break is looked at again below, once every encounter has been formed.
                if (IsBreakForEverySession(a)) undatedBreaks.Add(a);
                else unlinked.Add(Unlinked(a, "no encounter identifier and no service date"));
                continue;
            }

            var matches = profile
                .Where(p => !p.Key.StartsWith("NOID|") && p.Value.Date == a.ServiceDate && (category is null || p.Value.Category == category))
                .Select(p => p.Key)
                .ToList();

            if (matches.Count == 1)
            {
                groups[matches[0]].Add(new Member(a, "date_and_category"));
            }
            else if (matches.Count > 1)
            {
                unlinked.Add(Unlinked(a, $"no encounter identifier, and {matches.Count} encounters on {a.ServiceDate:yyyy-MM-dd} match ({string.Join(", ", matches)})"));
            }
            else if (category is null)
            {
                unlinked.Add(Unlinked(a, "no encounter identifier and no service category"));
            }
            else
            {
                var key = $"NOID|{a.ServiceDate:yyyy-MM-dd}|{category}";
                if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
                members.Add(new Member(a, "date_and_category"));
            }
        }

        // "Every meeting has a break from 13:40 to 13:55" names no session. It applies to each group
        // session that the same document describes and that the break falls inside.
        foreach (var a in undatedBreaks)
        {
            var sessions = groups.Where(g => DescribedBy(g.Value, a) && Contains(g.Value, a)).Select(g => g.Key).ToList();
            if (sessions.Count == 0)
            {
                unlinked.Add(Unlinked(a, "no encounter identifier and no service date, and its document describes no group session that it falls inside"));
                continue;
            }
            foreach (var key in sessions) groups[key].Add(new Member(a, EverySession));
        }

        return groups;
    }

    private const string EverySession = "every_session_in_document";

    private static bool IsBreakForEverySession(Assertion a) =>
        a.Kind == "excluded_interval" && Spans(a).Count > 0;

    private static List<Span> Spans(Assertion a) =>
        (a.Fields.Intervals ?? [])
            .Select(i => (Start: Parse.ClockMinutes(i.Start), End: Parse.ClockMinutes(i.End)))
            .Where(i => i.Start is not null && i.End is not null && i.End > i.Start)
            .Select(i => new Span(i.Start!.Value, i.End!.Value))
            .ToList();

    /// <summary>Whether the break's own document describes this encounter as a group session.</summary>
    private static bool DescribedBy(List<Member> members, Assertion a) =>
        members.Any(m => m.Assertion.DocHash == a.DocHash && m.LinkMethod != EverySession)
        && MostCommon(members.Select(m => m.Assertion.Fields.ServiceCategory)) == "group_therapy";

    /// <summary>
    /// Whether every part of the break falls inside the session, going by the times the break's own
    /// document gives for it. A session with no times in that document is taken to contain it.
    /// </summary>
    private static bool Contains(List<Member> members, Assertion a)
    {
        var times = members.Select(m => m.Assertion).Where(o => o.DocHash == a.DocHash)
            .SelectMany(o => new[] { (o.Fields.SessionStart, o.Fields.SessionEnd), (o.Fields.ScheduledStart, o.Fields.ScheduledEnd) })
            .Select(t => (Start: Parse.ClockMinutes(t.Item1), End: Parse.ClockMinutes(t.Item2)))
            .Where(t => t.Start is not null && t.End is not null && t.End > t.Start)
            .ToList();
        if (times.Count == 0) return true;
        var (start, end) = (times.Min(t => t.Start!.Value), times.Max(t => t.End!.Value));
        return Spans(a).All(s => s.Start >= start && s.End <= end);
    }

    private static UnlinkedAssertion Unlinked(Assertion a, string reason) =>
        new() { AssertionId = a.Id, Citation = a.Citation, Kind = a.Kind, Quote = a.Quote, Reason = reason };

    private static T? MostCommon<T>(IEnumerable<T?> values) =>
        values.Where(v => v is not null)
            .GroupBy(v => v!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key!.ToString(), StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

    // ---------- encounters ----------

    private static EncounterEvent BuildEncounter(string patientKey, string key, List<Member> members)
    {
        var list = members.Select(m => m.Assertion).ToList();
        var e = new EncounterEvent
        {
            EventKey = key,
            PatientKey = patientKey,
            EncounterId = key.StartsWith("NOID|") ? null : list.Select(a => a.EncounterId).FirstOrDefault(id => id is not null),
        };

        e.Assertions = members.Select(m => new AssertionLink
        {
            AssertionId = m.Assertion.Id, Citation = m.Assertion.Citation, Kind = m.Assertion.Kind, Quote = m.Assertion.Quote,
            RecordType = m.Assertion.RecordType, Signed = m.Assertion.Signed, Tier = Tiers.Of(m.Assertion), IsCopy = m.Assertion.IsCopy,
            StatementTime = m.Assertion.StatementTime?.ToString("yyyy-MM-dd HH:mm"), LinkMethod = m.LinkMethod,
        }).ToList();
        e.SourceDocuments = list.Select(a => a.PrintedDocId ?? a.DocHash[..12]).Distinct().Order(StringComparer.Ordinal).ToList();
        e.ServiceLabels = list.Select(a => a.Fields.ServiceLabel).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).Distinct().Order(StringComparer.Ordinal).ToList();
        e.Modality = MostCommon(list.Where(a => a.Fields.Modality is not null and not "not_stated").Select(a => a.Fields.Modality));

        if (key.StartsWith("NOID|")) e.Flags.Add("No encounter identifier in any record. The records were grouped by service date and category.");
        if (members.Any(m => m.LinkMethod == "date_and_category") && !key.StartsWith("NOID|"))
            e.Flags.Add("Some records carried no encounter identifier and were linked by service date and category.");
        foreach (var m in members.Where(m => m.LinkMethod == EverySession))
            e.Flags.Add($"A break stated once for every session, without naming this one, was applied here because the same document describes this session ({m.Assertion.Citation}).");

        ResolveServiceDate(e, list);
        ResolveSessionInterval(e, list);

        DateTime? sessionBegins = e.ServiceDate is { } date
            ? date.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(e.SessionInterval?.Start ?? 0)))
            : null;

        // Service category
        e.Category = FieldResolver.Resolve("service category",
            list.Where(a => a.Fields.ServiceCategory is not null).Select(a => Make(a, a.Fields.ServiceCategory!)),
            [], sessionBegins, e.Discrepancies);
        if (e.Category.Status == "missing")
        {
            var fallback = MostCommon(list.Select(a => a.Fields.ServiceCategory));
            if (fallback is not null)
            {
                e.Category.Value = fallback;
                e.Category.Status = "resolved";
                e.Category.Explanation = "Stated only by a draft or billing entry.";
                e.Flags.Add("The service category comes only from a draft or billing entry.");
            }
        }

        var corrections = list.Where(a => a.Kind == "correction" && a.Fields.CorrectedField is not null).ToList();

        // Disposition: what happened to the appointment.
        e.Disposition = FieldResolver.Resolve("disposition",
            list.Where(a => a.Kind != "correction" && DispositionClass(a.Fields.Disposition) is not null)
                .Select(a => Make(a, DispositionClass(a.Fields.Disposition)!, attends: DispositionClass(a.Fields.Disposition) == "took_place")),
            corrections.Where(c => c.Fields.CorrectedField == "status" && DispositionClass(Slug(c.Fields.NewValue)) is not null)
                .Select(c => new CorrectionStatement
                {
                    Source = c, Tier = Tiers.Of(c), Time = c.StatementTime,
                    OldValue = DispositionClass(Slug(c.Fields.OldValue)), NewValue = DispositionClass(Slug(c.Fields.NewValue))!,
                }),
            sessionBegins, e.Discrepancies);

        // Patient presence, kept apart from disposition: "completed" does not mean the patient was there.
        // A document that says both "present" and "not present", and also gives the patient's own
        // times, is describing a patient who was there for part of the contact. Its "not present"
        // statements are about a portion. Without times there is nothing to say which portion, so
        // the two statements are a contradiction and are left to be resolved like any other.
        var presentIn = list.Where(a => a.Fields.PatientPresent is "yes" or "part").Select(a => a.DocHash).ToHashSet();
        var givesTimes = list
            .Where(a => a.Kind is "presence" or "attendance")
            .Where(a => a.Fields.Intervals is { Count: > 0 } || a.Fields.Arrival is not null || a.Fields.Departure is not null)
            .Select(a => a.DocHash)
            .ToHashSet();
        var aboutAPortion = list
            .Where(a => a.Fields.PatientPresent == "no" && presentIn.Contains(a.DocHash) && givesTimes.Contains(a.DocHash))
            .ToList();
        if (aboutAPortion.Count > 0)
            e.Flags.Add($"The patient was present for part of this contact ({string.Join(", ", aboutAPortion.Select(a => a.Citation).Distinct())}). Only the patient's own times are counted.");

        e.PatientPresent = FieldResolver.Resolve("patient present",
            list.Where(a => a.Fields.PatientPresent is "yes" or "no" or "part")
                .Except(aboutAPortion)
                .Select(a => Make(a, a.Fields.PatientPresent == "no" ? "absent" : "present", attends: a.Fields.PatientPresent != "no")),
            [], sessionBegins, e.Discrepancies);

        // Presence start and end.
        var starts = new List<Statement>();
        var ends = new List<Statement>();
        var impliedGaps = new List<TimeSpanMinutes>();

        // Contact intervals are read one source at a time. A source is one record within one document,
        // so a platform log listing two calls is a single statement of 13:00 to 13:55 with a gap, and
        // agrees with a note that says the same thing in one sentence.
        var intervalSources = list
            .Where(a => a.Kind == "presence" && a.Fields.Intervals is { Count: > 0 })
            .GroupBy(a => (a.DocHash, a.RecordType, a.IsCopy));
        foreach (var source in intervalSources)
        {
            var spans = Intervals.Union(source
                .SelectMany(a => a.Fields.Intervals!)
                .Select(i => (Start: Parse.ClockMinutes(i.Start), End: Parse.ClockMinutes(i.End)))
                .Where(i => i.Start is not null && i.End is not null && i.End > i.Start)
                .Select(i => new Span(i.Start!.Value, i.End!.Value)));
            if (spans.Count == 0) continue;

            foreach (var a in source)
            {
                starts.Add(Make(a, Parse.Clock(spans[0].Start), attends: true));
                ends.Add(Make(a, Parse.Clock(spans[^1].End), attends: true));
            }
            if (source.All(a => Tiers.Of(a) > 2)) continue;
            for (var i = 1; i < spans.Count; i++)
                impliedGaps.Add(new TimeSpanMinutes
                {
                    Start = spans[i - 1].End, End = spans[i].Start, Reason = "gap between contact intervals",
                    Citations = source.Select(a => a.Citation).Distinct().ToList(),
                });
        }

        foreach (var a in list.Where(a => a.Kind is "presence" or "attendance"))
        {
            var roster = a.RecordType is "attendance_record" or "desk_log" or "appointment_export" or "scheduling_entry";
            if (Parse.ClockMinutes(a.Fields.Arrival) is { } arrival) starts.Add(Make(a, Parse.Clock(arrival), roster, attends: true));
            if (Parse.ClockMinutes(a.Fields.Departure) is { } departure) ends.Add(Make(a, Parse.Clock(departure), roster, attends: true));
        }

        e.Start = FieldResolver.Resolve("presence start", starts,
            TimeCorrections(corrections, "start", "arrival"), sessionBegins, e.Discrepancies);
        e.End = FieldResolver.Resolve("presence end", ends,
            TimeCorrections(corrections, "end", "departure"), sessionBegins, e.Discrepancies);

        foreach (var c in corrections.Where(c => c.Fields.CorrectedField == "break"))
            e.Discrepancies.Add(new Discrepancy
            {
                Rule = "R3",
                Description = "A correction to a break interval was found. Break corrections are not applied automatically and need review.",
                Citations = [c.Citation],
            });

        // Excluded intervals: breaks and lost connections.
        e.Excluded = list
            .Where(a => a.Kind == "excluded_interval" && Tiers.Of(a) <= 2 && a.Fields.Intervals is not null)
            .SelectMany(a => a.Fields.Intervals!.Select(i => (a, Start: Parse.ClockMinutes(i.Start), End: Parse.ClockMinutes(i.End))))
            .Where(x => x.Start is not null && x.End is not null && x.End > x.Start)
            .Select(x => new TimeSpanMinutes { Start = x.Start!.Value, End = x.End!.Value, Reason = Describe(x.a.Fields.IntervalReason), Citations = [x.a.Citation] })
            .Concat(impliedGaps)
            .GroupBy(x => (x.Start, x.End))
            .Select(g => new TimeSpanMinutes
            {
                Start = g.Key.Start, End = g.Key.End,
                Reason = g.Select(x => x.Reason).OrderBy(r => r == "gap between contact intervals" ? 1 : 0).First(),
                Citations = g.SelectMany(x => x.Citations).Distinct().Order(StringComparer.Ordinal).ToList(),
            })
            .OrderBy(x => x.Start)
            .ToList();

        var rosterIds = starts.Concat(ends).Where(s => s.FromRoster).Select(s => s.Source.Id).ToHashSet();
        DecideOccurrenceAndMinutes(e, list, rosterIds);
        CheckStatedDurations(e, list);
        ReportChargesAndDrafts(e, list);
        AddNeeds(e);
        return e;
    }

    private static Statement Make(Assertion a, string value, bool fromRoster = false, bool attends = false) =>
        new() { Value = value, Source = a, Tier = Tiers.Of(a), Time = a.StatementTime, FromRoster = fromRoster, AssertsAttendance = attends };

    private static IEnumerable<CorrectionStatement> TimeCorrections(List<Assertion> corrections, params string[] fields) =>
        corrections
            .Where(c => fields.Contains(c.Fields.CorrectedField) && Parse.ClockMinutes(c.Fields.NewValue) is not null)
            .Select(c => new CorrectionStatement
            {
                Source = c, Tier = Tiers.Of(c), Time = c.StatementTime,
                OldValue = Parse.ClockMinutes(c.Fields.OldValue) is { } old ? Parse.Clock(old) : null,
                NewValue = Parse.Clock(Parse.ClockMinutes(c.Fields.NewValue)!.Value),
            });

    private static string? Slug(string? text) => text?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>
    /// Groups dispositions that mean the same thing for counting, so "attended" and "attended in
    /// part" from two records are not treated as a conflict.
    /// </summary>
    private static string? DispositionClass(string? disposition) => disposition switch
    {
        null or "not_stated" or "scheduled" => null,
        "no_show" or "clinic_cancelled" or "patient_cancelled" => disposition,
        _ when Vocabulary.TookPlace.Contains(disposition) => "took_place",
        "cancelled_by_clinic" or "clinic_cancellation" => "clinic_cancelled",
        "cancelled_by_patient" or "patient_cancellation" => "patient_cancelled",
        "noshow" or "no_show;_patient_did_not_attend" => "no_show",
        _ => null,
    };

    private static string DescribeOutcome(string? disposition) => disposition switch
    {
        "no_show" => "a no show",
        "clinic_cancelled" => "cancelled by the clinic",
        "patient_cancelled" => "cancelled by the patient",
        _ => "not held",
    };

    private static string Describe(string? reason) => reason switch
    {
        "break" => "break",
        "connection_lost" => "connection lost",
        _ => "no therapy in this interval",
    };

    private static void ResolveServiceDate(EncounterEvent e, List<Assertion> list)
    {
        var dated = list.Where(a => a.ServiceDate is not null).ToList();
        var evidence = dated.Where(a => Tiers.Of(a) <= 2).ToList();
        if (evidence.Count == 0) evidence = dated;
        var byDate = evidence.GroupBy(a => a.ServiceDate!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        if (byDate.Count == 0)
        {
            e.Flags.Add("No record gives a service date for this encounter.");
            return;
        }
        e.ServiceDate = byDate[0].Key;
        if (byDate.Count > 1)
            e.Discrepancies.Add(new Discrepancy
            {
                Rule = "R1",
                Description = $"Records linked to this encounter give different service dates: {string.Join(", ", byDate.Select(g => $"{g.Key:yyyy-MM-dd} ({g.Count()})"))}. The most common date is used.",
                Citations = byDate.Skip(1).SelectMany(g => g.Select(a => a.Citation)).Distinct().ToList(),
            });
    }

    /// <summary>The session's own interval: the actual one when a record states it, otherwise the booked slot.</summary>
    private static void ResolveSessionInterval(EncounterEvent e, List<Assertion> list)
    {
        static List<(Span Span, Assertion Source)> Read(IEnumerable<Assertion> source, Func<AssertionFields, (string?, string?)> pick) =>
            source.Select(a => (a, Times: pick(a.Fields)))
                .Select(x => (x.a, Start: Parse.ClockMinutes(x.Times.Item1), End: Parse.ClockMinutes(x.Times.Item2)))
                .Where(x => x.Start is not null && x.End is not null && x.End > x.Start)
                .Select(x => (new Span(x.Start!.Value, x.End!.Value), x.a))
                .ToList();

        var actual = Read(list.Where(a => Tiers.Of(a) <= 2), f => (f.SessionStart, f.SessionEnd));
        var scheduled = Read(list, f => (f.ScheduledStart, f.ScheduledEnd));
        var (chosen, kind) = actual.Count > 0 ? (actual, "actual session interval") : (scheduled, "scheduled interval");
        if (chosen.Count == 0) return;

        var groups = chosen.GroupBy(c => c.Span).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Start).ToList();
        var best = groups[0];
        e.SessionInterval = new TimeSpanMinutes
        {
            Start = best.Key.Start, End = best.Key.End, Reason = kind,
            Citations = best.Select(c => c.Source.Citation).Distinct().Order(StringComparer.Ordinal).ToList(),
        };
        if (groups.Count > 1)
            e.Discrepancies.Add(new Discrepancy
            {
                Rule = "R4",
                Description = $"Records give different values for the {kind}: {string.Join(", ", groups.Select(g => $"{Parse.Clock(g.Key.Start)}-{Parse.Clock(g.Key.End)}"))}. The most common is used.",
                Citations = groups.Skip(1).SelectMany(g => g.Select(c => c.Source.Citation)).Distinct().ToList(),
            });
    }

    // ---------- R7: occurrence and minutes ----------

    private static void DecideOccurrenceAndMinutes(EncounterEvent e, List<Assertion> list, HashSet<long> rosterIds)
    {
        var startValues = Values(e.Start);
        var endValues = Values(e.End);
        var hasPresence = startValues.Count > 0 && endValues.Count > 0;
        var hasClinicalRecord = list.Any(a => Tiers.Of(a) == 1);
        var excluded = e.Excluded.Select(x => new Span(x.Start, x.End)).ToList();

        // Roster times are clipped to the session. A clinician's stated contact interval never is.
        bool FromRoster(FieldDecision d) => d.Candidates.Count > 0 && d.Candidates.All(c => c.AssertionIds.All(rosterIds.Contains));
        e.PresenceFromRoster = hasPresence && FromRoster(e.Start) && FromRoster(e.End);
        var clip = e.SessionInterval is not null && e.PresenceFromRoster;
        Span? session = e.SessionInterval is { } s ? new Span(s.Start, s.End) : null;

        var (min, max, arithmetic) = hasPresence
            ? MinutesFromPresence(startValues, endValues, clip ? session : null, excluded, e)
            : (0, 0, "");

        // 1. The appointment did not take place.
        if (e.Disposition.IsSettled && Vocabulary.DidNotTakePlace.Contains(e.Disposition.Value!))
        {
            var contradicted = hasPresence && Math.Min(e.Start.Tier ?? 9, e.End.Tier ?? 9) <= (e.Disposition.Tier ?? 9);
            if (!contradicted)
            {
                NotSession(e, $"The appointment was {DescribeOutcome(e.Disposition.Value)}.", e.Disposition.Candidates);
                return;
            }
            Uncertain(e, 0, max, $"A record of equal or higher standing gives attendance times, but the disposition is {DescribeOutcome(e.Disposition.Value)}. If the patient attended: {arithmetic}");
            e.Flags.Add("Attendance times and disposition contradict each other.");
            return;
        }

        // 2. The appointment took place, but the patient was not there.
        if (e.PatientPresent is { IsSettled: true, Value: "absent" })
        {
            NotSession(e, "The patient was not present.", e.PatientPresent.Candidates);
            return;
        }

        // 3. Whether it took place, or whether the patient was there, is itself unresolved.
        if (e.Disposition.Status == "unresolved" || e.PatientPresent.Status == "unresolved")
        {
            var upper = hasPresence ? max : AssumedMinutes(session, excluded);
            Uncertain(e, 0, upper, "The records disagree on whether the patient attended." + (hasPresence ? $" If the patient attended: {arithmetic}" : ""));
            return;
        }

        // 4. It took place and the record gives the patient's times.
        if (hasPresence)
        {
            e.Occurrence = "session";
            e.MinutesMin = min;
            e.MinutesMax = max;
            e.Arithmetic = arithmetic;
            // A signed record of contact times is itself a statement that the patient attended.
            if (e.Disposition.Status == "missing" && e.PatientPresent.Status == "missing" && Math.Min(e.Start.Tier ?? 9, e.End.Tier ?? 9) > 1)
                e.Flags.Add("No signed record states that the patient attended. The session is inferred from contact times in an administrative record.");
            FlagMissingBreak(e);
            return;
        }

        // 5. No times. What the record does say decides how much can be assumed.
        var assumed = AssumedMinutes(session, excluded);
        var stated = list
            .Where(a => a.Kind == "stated_duration" && a.Fields.DurationMeasures == "patient_present" && a.Fields.Minutes is > 0 && Tiers.Of(a) <= 2)
            .Select(a => a.Fields.Minutes!.Value).Distinct().ToList();
        var label = MostSpecificDisposition(list);

        if (e.Disposition.Status == "missing" && e.PatientPresent.Status == "missing")
        {
            Uncertain(e, 0, assumed, "No usable record says whether the appointment took place.");
            e.Flags.Add("No evidence of attendance.");
            return;
        }

        if (!hasClinicalRecord)
        {
            Uncertain(e, 0, assumed, $"Known only from administrative records, which give no attendance times. Upper bound: {DescribeAssumed(session, excluded, assumed)}");
            e.Flags.Add("No clinical documentation for this appointment.");
            return;
        }

        if (stated.Count == 1)
        {
            e.Occurrence = "session";
            e.MinutesMin = e.MinutesMax = stated[0];
            e.Arithmetic = $"The record states {stated[0]} minutes of patient contact and gives no times.";
            e.Flags.Add("Minutes are taken from a stated duration, not computed from times.");
            return;
        }

        if (session is null)
        {
            Uncertain(e, 0, 0, "The patient attended, but no record gives times or a duration.");
            e.Flags.Add("No duration information.");
            return;
        }

        var partial = label == "attended_in_part" || list.Any(a => a.Fields.PatientPresent == "part" && Tiers.Of(a) <= 2);
        if (partial)
        {
            Uncertain(e, 0, assumed, $"The patient attended part of the session and no record gives times. Upper bound: {DescribeAssumed(session, excluded, assumed)}");
            e.Occurrence = "session";
            return;
        }

        e.Occurrence = "session";
        e.MinutesMin = e.MinutesMax = assumed;
        e.Arithmetic = $"No arrival or departure recorded. Full attendance assumed: {DescribeAssumed(session, excluded, assumed)}";
        e.Flags.Add("Attendance times were not recorded. The session interval is assumed.");
        FlagMissingBreak(e);
    }

    private static List<int> Values(FieldDecision d) =>
        d.Candidates.Select(c => Parse.ClockMinutes(c.Value)).Where(v => v is not null).Select(v => v!.Value).Distinct().Order().ToList();

    private static (int Min, int Max, string Arithmetic) MinutesFromPresence(List<int> starts, List<int> ends, Span? clipTo, List<Span> excluded, EncounterEvent e)
    {
        var lines = new List<string>();
        var totals = new List<int>();
        foreach (var start in starts)
        {
            foreach (var end in ends)
            {
                if (end <= start) continue;
                var present = new List<Span> { new(start, end) };
                var text = $"present {Parse.Clock(start)}-{Parse.Clock(end)}";
                if (clipTo is { } window)
                {
                    var clipped = Intervals.Clip(present, window);
                    if (Intervals.TotalMinutes(clipped) != Intervals.TotalMinutes(present))
                        text += $", limited to the session {Parse.Clock(window.Start)}-{Parse.Clock(window.End)}";
                    present = clipped;
                }
                foreach (var cut in e.Excluded.Where(x => Intervals.TotalMinutes(Intervals.Clip(present, new Span(x.Start, x.End))) > 0))
                    text += $", less {cut.Reason} {Parse.Clock(cut.Start)}-{Parse.Clock(cut.End)}";
                var minutes = Intervals.TotalMinutes(Intervals.Subtract(present, excluded));
                totals.Add(minutes);
                lines.Add($"{text} = {minutes} minutes");
            }
        }
        if (totals.Count == 0) return (0, 0, "The recorded times do not form a valid interval.");
        var arithmetic = lines.Count == 1 ? lines[0] : string.Join("; or ", lines);
        return (totals.Min(), totals.Max(), arithmetic);
    }

    private static int AssumedMinutes(Span? session, List<Span> excluded) =>
        session is { } s ? Intervals.TotalMinutes(Intervals.Subtract([s], excluded)) : 0;

    private static string DescribeAssumed(Span? session, List<Span> excluded, int minutes)
    {
        if (session is not { } s) return "no session interval is recorded";
        var cuts = excluded.Where(x => x.End > s.Start && x.Start < s.End).Select(x => $"{Parse.Clock(x.Start)}-{Parse.Clock(x.End)}").ToList();
        var less = cuts.Count > 0 ? $", less {string.Join(" and ", cuts)}" : "";
        return $"session {Parse.Clock(s.Start)}-{Parse.Clock(s.End)}{less} = {minutes} minutes";
    }

    private static string? MostSpecificDisposition(List<Assertion> list)
    {
        string[] order = ["attended_in_part", "attended_in_full", "attended", "completed"];
        var seen = list.Where(a => Tiers.Of(a) <= 2).Select(a => a.Fields.Disposition).Where(d => d is not null).ToHashSet();
        return order.FirstOrDefault(seen.Contains);
    }

    private static void NotSession(EncounterEvent e, string reason, List<Candidate> support)
    {
        e.Occurrence = "not_session";
        e.NotSessionReason = reason;
        e.MinutesMin = e.MinutesMax = 0;
        e.Arithmetic = $"{reason} 0 minutes. ({string.Join(", ", support.SelectMany(c => c.Citations).Distinct())})";
    }

    private static void Uncertain(EncounterEvent e, int min, int max, string arithmetic)
    {
        e.Occurrence = "uncertain";
        e.MinutesMin = min;
        e.MinutesMax = max;
        e.Arithmetic = arithmetic;
    }

    private static void FlagMissingBreak(EncounterEvent e)
    {
        if (e.Category.Value == "group_therapy" && !e.Excluded.Any(x => x.Reason == "break"))
            e.Flags.Add("No break is documented for this group. If the group had a break, the minutes are an upper bound.");
    }

    /// <summary>Compares the minutes computed from times with any duration the record states.</summary>
    private static void CheckStatedDurations(EncounterEvent e, List<Assertion> list)
    {
        if (e.Occurrence != "session") return;
        foreach (var a in list.Where(a => a.Kind == "stated_duration" && a.Fields.DurationMeasures == "patient_present" && a.Fields.Minutes is not null && Tiers.Of(a) <= 2))
        {
            var stated = a.Fields.Minutes!.Value;
            if (stated < e.MinutesMin || stated > e.MinutesMax)
                e.Discrepancies.Add(new Discrepancy
                {
                    Rule = "R7",
                    Description = $"The record states {stated} minutes of patient contact. The recorded times give {Range(e.MinutesMin, e.MinutesMax)}.",
                    Citations = [a.Citation],
                });
        }
    }

    /// <summary>R5 for whole statements: a draft that says "attended" or a posted charge, where the patient did not attend.</summary>
    private static void ReportChargesAndDrafts(EncounterEvent e, List<Assertion> list)
    {
        if (e.Occurrence == "session") return;
        var outcome = e.NotSessionReason ?? "Attendance is not established.";
        foreach (var a in list.Where(a => a.Kind == "charge"))
            e.Discrepancies.Add(new Discrepancy
            {
                Rule = "R5",
                // The charge's description is the document's own text, so it is quoted and never reworded.
                Description = $"A charge was posted for this encounter ({(a.Fields.Description is { } text ? $"\"{text}\"" : "no description")}, quantity {a.Fields.Quantity?.ToString() ?? "not stated"}). {outcome} Billing is not evidence of attendance.",
                Citations = [a.Citation],
            });
        foreach (var a in list.Where(a => Tiers.Of(a) >= 3 && a.Kind != "charge" && (Vocabulary.TookPlace.Contains(a.Fields.Disposition ?? "") || a.Fields.PatientPresent is "yes" or "part")))
            e.Discrepancies.Add(new Discrepancy
            {
                Rule = "R5",
                Description = $"An unsigned draft says the patient attended. {outcome} A draft is not evidence of attendance.",
                Citations = [a.Citation],
            });
    }

    /// <summary>
    /// What documentation would settle each unresolved item. An item that settles a field is
    /// recorded on that field as well as on the event, so nothing has to guess which it belongs to.
    /// </summary>
    private static void AddNeeds(EncounterEvent e)
    {
        var id = e.EncounterId ?? $"the {Vocabulary.DescribeCategory(e.Category.Value).ToLowerInvariant()} contact on {e.ServiceDate:yyyy-MM-dd}";
        void Need(string item, params FieldDecision[] fields)
        {
            e.Needs.Add(item);
            foreach (var field in fields.Where(f => f.Status == "unresolved")) (field.Needs ??= []).Add(item);
        }

        if (e.Start.Status == "unresolved")
            Need($"A signed correction or addendum for {id} stating when the patient's contact began, or an independent record of arrival such as a check-in, room or platform log.", e.Start);
        if (e.End.Status == "unresolved")
            Need($"A signed correction or addendum for {id} stating when the patient's contact ended, or an independent record of departure.", e.End);
        if (e.Disposition.Status == "unresolved" || e.PatientPresent.Status == "unresolved")
            Need($"A signed attendance record or correction for {id} stating whether the patient attended.", e.Disposition, e.PatientPresent);
        if (e.Category.Status == "unresolved")
            Need($"A signed record for {id} stating which service was delivered.", e.Category);
        if (e.Occurrence == "uncertain" && e.Needs.Count == 0)
            e.Needs.Add($"A signed clinical note or attendance record for {id} giving the patient's attendance and contact times.");
        if (e.Occurrence == "session" && !e.MinutesSettled && e.Needs.Count == 0)
            e.Needs.Add($"A signed record for {id} giving the patient's arrival and departure times.");
    }

    public static string Range(int min, int max) => min == max ? $"{min} minutes" : $"{min} to {max} minutes";

    // ---------- measurements ----------

    private static List<MeasurementEvent> BuildMeasurements(string patientKey, List<Assertion> assertions)
    {
        var result = new List<MeasurementEvent>();
        bool IsRepeat(Assertion a) => a.IsCopy || a.Fields.IsRestatement == true || a.RecordType == "import_receipt";
        DateOnly? DateOf(Assertion a) => Parse.Date(a.Fields.CompletedAt) ?? a.ServiceDate;

        var all = assertions.Where(a => a.Kind == "measure" && !string.IsNullOrWhiteSpace(a.Fields.Instrument) && a.Fields.TotalScore is not null).ToList();

        // A copy or later mention that gives no completion date is never an assessment of its own.
        // It is attached to the one dated assessment with the same instrument and score, if there is one.
        var undatedMentions = all.Where(a => IsRepeat(a) && DateOf(a) is null).ToList();
        var measures = all.Except(undatedMentions).ToList();

        foreach (var group in measures.GroupBy(a => (Instrument: a.Fields.Instrument!.Trim().ToUpperInvariant(), Date: DateOf(a))))
        {
            var members = group.ToList();
            members.AddRange(undatedMentions.Where(u =>
                u.Fields.Instrument!.Trim().ToUpperInvariant() == group.Key.Instrument
                && group.Any(m => m.Fields.TotalScore == u.Fields.TotalScore)
                && measures.Count(m => m.Fields.Instrument!.Trim().ToUpperInvariant() == group.Key.Instrument && m.Fields.TotalScore == u.Fields.TotalScore && DateOf(m) != group.Key.Date) == 0));
            var originals = members.Where(a => !IsRepeat(a)).ToList();
            var basis = originals.Count > 0 ? originals : members;
            var totals = basis.Select(a => a.Fields.TotalScore!.Value).Distinct().Order().ToList();

            var m = new MeasurementEvent
            {
                PatientKey = patientKey,
                Instrument = members[0].Fields.Instrument!.Trim(),
                CompletedOn = group.Key.Date,
                CompletedAt = basis.Select(a => a.Fields.CompletedAt).FirstOrDefault(c => c is not null),
                TotalScore = totals.Count == 1 ? totals[0] : null,
                FormId = members.Select(a => a.Fields.FormId).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)),
                ItemScores = basis.Select(a => a.Fields.ItemScores).FirstOrDefault(i => i is { Count: > 0 }) ?? [],
                OriginalCitations = originals.Select(a => a.Citation).Distinct().ToList(),
                RepeatCitations = members.Where(IsRepeat).Select(a => a.Citation).Distinct().ToList(),
                AssertionIds = members.Select(a => a.Id).ToList(),
            };
            if (totals.Count > 1)
                m.Discrepancies.Add(new Discrepancy
                {
                    Rule = "R4",
                    Description = $"Records give different totals for the same assessment: {string.Join(", ", totals)}.",
                    Citations = basis.Select(a => a.Citation).Distinct().ToList(),
                });
            if (originals.Count == 0)
                m.Discrepancies.Add(new Discrepancy
                {
                    Rule = "R2",
                    Description = "This result is known only from a copy, import or later mention. The original record was not supplied.",
                    Citations = m.RepeatCitations,
                });
            if (group.Key.Date is null)
                m.Discrepancies.Add(new Discrepancy { Rule = "R1", Description = "No completion date is recorded for this result.", Citations = members.Select(a => a.Citation).ToList() });
            result.Add(m);
        }

        return result.OrderBy(m => m.Instrument, StringComparer.Ordinal).ThenBy(m => m.CompletedOn ?? DateOnly.MaxValue).ToList();
    }

    // ---------- plans and episode ----------

    private static List<PlanVersion> BuildPlans(string patientKey, List<Assertion> assertions, List<string> warnings)
    {
        // A document states a plan version only if it sets a threshold. A note that mentions which
        // services are planned, without a number to meet, is not a version of the plan.
        var withThresholds = assertions
            .Where(a => a.Kind == "plan_requirement" && Tiers.Of(a) <= 2)
            .Where(a => a.Fields.MinTherapyDaysPerWeek is not null || a.Fields.MinMinutesPerWeek is not null)
            .Select(a => a.DocHash)
            .ToHashSet();
        var requirements = assertions
            .Where(a => a.Kind == "plan_requirement" && Tiers.Of(a) <= 2 && withThresholds.Contains(a.DocHash))
            .ToList();

        // One document states one plan version, however many assertions it was split into. The version
        // takes effect by date: the date the document states, else the start of its episode, else the
        // day it was signed. A document that states two effective dates describes two versions.
        var effectiveFrom = new Dictionary<long, DateOnly?>();
        foreach (var document in requirements.GroupBy(a => a.DocHash))
        {
            var stated = document.Select(a => Parse.Date(a.Fields.EffectiveFrom)).Where(d => d is not null).Distinct().Order().ToList();
            var fallback = stated.FirstOrDefault()
                ?? document.Select(a => Parse.Date(a.Fields.EpisodeStart)).FirstOrDefault(d => d is not null)
                ?? document.Select(a => a.StatementTime is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null).FirstOrDefault(d => d is not null);
            foreach (var a in document)
                effectiveFrom[a.Id] = Parse.Date(a.Fields.EffectiveFrom) ?? fallback;
        }

        var plans = new List<PlanVersion>();
        foreach (var group in requirements.GroupBy(a => (a.DocHash, From: effectiveFrom[a.Id])))
        {
            var members = group.ToList();
            FlagDisagreements(members, warnings);
            plans.Add(new PlanVersion
            {
                PatientKey = patientKey,
                EffectiveFrom = group.Key.From,
                EffectiveTo = members.Select(a => Parse.Date(a.Fields.EffectiveTo) ?? Parse.Date(a.Fields.EpisodeEnd)).FirstOrDefault(d => d is not null),
                MinTherapyDaysPerWeek = members.Select(a => a.Fields.MinTherapyDaysPerWeek).FirstOrDefault(v => v is not null),
                MinMinutesPerWeek = members.Select(a => a.Fields.MinMinutesPerWeek).FirstOrDefault(v => v is not null),
                WeekDefinition = members.Select(a => a.Fields.WeekDefinition).FirstOrDefault(w => w is not null and not "not_stated") ?? "monday_sunday",
                CountedCategories = members.SelectMany(a => a.Fields.CountedCategories ?? []).Distinct().Order(StringComparer.Ordinal).ToList(),
                ExcludedCategories = members.SelectMany(a => a.Fields.ExcludedCategories ?? []).Distinct().Order(StringComparer.Ordinal).ToList(),
                Citations = members.Select(a => a.Citation).Distinct().ToList(),
                AssertionIds = members.Select(a => a.Id).ToList(),
                SignedAt = members.Select(a => a.StatementTime).FirstOrDefault(t => t is not null)?.ToString("yyyy-MM-dd HH:mm"),
            });
        }

        plans = plans.OrderBy(p => p.EffectiveFrom ?? DateOnly.MinValue).ThenBy(p => p.SignedAt, StringComparer.Ordinal).ToList();

        // A version ends the day before the next one begins.
        for (var i = 0; i + 1 < plans.Count; i++)
        {
            if (plans[i + 1].EffectiveFrom is not { } next) continue;
            var lastDay = next.AddDays(-1);
            if (plans[i].EffectiveTo is null || plans[i].EffectiveTo > lastDay) plans[i].EffectiveTo = lastDay;
        }
        return plans;
    }

    /// <summary>
    /// One document states one version of the plan, and the first value it states for each
    /// threshold is the one used. When the document's assertions give different values for one
    /// version, the patient gets a warning that names the document, every value and where each is
    /// stated, and the value used, so the choice is never made silently.
    /// </summary>
    private static void FlagDisagreements(List<Assertion> members, List<string> warnings)
    {
        var document = members[0].PrintedDocId ?? members[0].DocHash[..Math.Min(8, members[0].DocHash.Length)];
        void Check(string what, Func<Assertion, string?> value)
        {
            var values = members.Where(a => value(a) is not null).GroupBy(a => value(a)!).ToList();
            if (values.Count < 2) return;
            warnings.Add($"{document} gives different {what} for one version of the treatment plan: " +
                string.Join(", ", values.Select(g => $"{g.Key} ({string.Join(", ", g.Select(a => a.Citation).Distinct())})")) +
                $". The plan version uses {values[0].Key}, the value stated first in the document, and the weekly goal results depend on it. Check the document.");
        }

        Check("minimum therapy days per week", a => a.Fields.MinTherapyDaysPerWeek?.ToString());
        Check("minimum minutes per week", a => a.Fields.MinMinutesPerWeek?.ToString());
        Check("definitions of the week", a => a.Fields.WeekDefinition switch
        {
            "monday_sunday" => "Monday to Sunday",
            "sunday_saturday" => "Sunday to Saturday",
            _ => null,
        });
    }

    private static Episode BuildEpisode(List<Assertion> assertions, PatientAbstraction abstraction)
    {
        var stated = assertions
            .Where(a => a.Kind is "plan_requirement" && Tiers.Of(a) <= 2 && Parse.Date(a.Fields.EpisodeStart) is not null && Parse.Date(a.Fields.EpisodeEnd) is not null)
            .ToList();

        if (stated.Count > 0)
        {
            return new Episode
            {
                Start = stated.Min(a => Parse.Date(a.Fields.EpisodeStart)),
                End = stated.Max(a => Parse.Date(a.Fields.EpisodeEnd)),
                Citations = stated.Select(a => a.Citation).Distinct().ToList(),
            };
        }

        var dates = abstraction.Encounters.Where(e => e.ServiceDate is not null).Select(e => e.ServiceDate!.Value).ToList();
        if (dates.Count == 0) return new Episode();
        abstraction.Warnings.Add("No record states the episode dates. The range of recorded encounters is used.");
        return new Episode { Start = dates.Min(), End = dates.Max() };
    }
}

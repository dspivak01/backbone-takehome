using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClinicalAbstraction.Domain;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Write<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException($"Could not read {typeof(T).Name}");
}

public sealed class IntervalText
{
    public string? Start { get; set; }
    public string? End { get; set; }
}

public sealed class ItemScore
{
    public string? Item { get; set; }
    public int? Score { get; set; }
}

/// <summary>
/// The kind-specific details of an assertion. Every property is optional: an assertion carries only
/// the fields its statement provides. Reconciliation reads fields, not kinds, so a field is used
/// wherever the extractor placed it.
/// </summary>
public sealed class AssertionFields
{
    // Encounter description
    public string? ServiceCategory { get; set; }
    public string? ServiceLabel { get; set; }
    public string? Modality { get; set; }
    public List<string>? Clinicians { get; set; }
    public string? ScheduledStart { get; set; }
    public string? ScheduledEnd { get; set; }
    public string? SessionStart { get; set; }
    public string? SessionEnd { get; set; }

    // Attendance
    public string? Disposition { get; set; }
    public string? PatientPresent { get; set; }

    // Presence and excluded intervals
    public string? Arrival { get; set; }
    public string? Departure { get; set; }
    public List<IntervalText>? Intervals { get; set; }
    public string? IntervalReason { get; set; }

    // Stated duration
    public int? Minutes { get; set; }
    public string? DurationMeasures { get; set; }

    // Correction
    public string? CorrectedField { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    // No service contact
    public string? WhatDidNotOccur { get; set; }

    // Plan requirement
    public int? MinTherapyDaysPerWeek { get; set; }
    public int? MinMinutesPerWeek { get; set; }
    public string? WeekDefinition { get; set; }
    public List<string>? CountedCategories { get; set; }
    public List<string>? ExcludedCategories { get; set; }
    public string? EffectiveFrom { get; set; }
    public string? EffectiveTo { get; set; }
    public string? EpisodeStart { get; set; }
    public string? EpisodeEnd { get; set; }

    // Measure
    public string? Instrument { get; set; }
    public int? TotalScore { get; set; }
    public string? CompletedAt { get; set; }
    public string? FormId { get; set; }
    public List<ItemScore>? ItemScores { get; set; }
    public bool? IsRestatement { get; set; }

    // Observation
    public string? ObservationCategory { get; set; }
    public string? Summary { get; set; }

    // Authorization and charge
    public string? AuthorizationNumber { get; set; }
    public int? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? PeriodStart { get; set; }
    public string? PeriodEnd { get; set; }
    public string? ChargeId { get; set; }
    public string? Description { get; set; }
    public string? PostedAt { get; set; }

    // Other
    public string? OtherCategory { get; set; }
}

/// <summary>One statement made by one document, with where it was said and who said it.</summary>
public sealed class Assertion
{
    public long Id { get; set; }
    public string DocHash { get; set; } = "";
    public string? PrintedDocId { get; set; }
    public string PatientKey { get; set; } = "";
    public string? EncounterId { get; set; }
    public List<string>? OtherIds { get; set; }
    public DateOnly? ServiceDate { get; set; }
    public string? DateAsWritten { get; set; }
    public string Kind { get; set; } = "";
    public AssertionFields Fields { get; set; } = new();

    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string Quote { get; set; } = "";
    public string QuoteStatus { get; set; } = "verified";

    public string RecordType { get; set; } = "other";
    public string Signed { get; set; } = "no";
    public string? Signer { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? RecordedAt { get; set; }
    public bool IsCopy { get; set; }
    public DateTime? OriginalSignedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }

    public string ExtractorVersion { get; set; } = Vocabulary.ExtractorVersion;
    public string Model { get; set; } = "";

    /// <summary>
    /// When the statement was made. A copy takes the time of the record it copies.
    /// The time a document was received is never used.
    /// </summary>
    [JsonIgnore]
    public DateTime? StatementTime => IsCopy ? OriginalSignedAt ?? SignedAt : SignedAt ?? RecordedAt;

    /// <summary>A short reference a reviewer can follow, such as "BH-D103 L7".</summary>
    [JsonIgnore]
    public string Citation
    {
        get
        {
            var doc = PrintedDocId ?? DocHash[..Math.Min(8, DocHash.Length)];
            return LineStart == LineEnd ? $"{doc} L{LineStart}" : $"{doc} L{LineStart}-{LineEnd}";
        }
    }
}

public static class Parse
{
    /// <summary>Reads a clock time such as "10:45" or "9:05" as minutes after midnight.</summary>
    public static int? ClockMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "hh:mm tt", "h:mmtt"];
        if (TimeOnly.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time.Hour * 60 + time.Minute;
        return null;
    }

    public static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }

    public static DateTime? DateTimeLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        return null;
    }

    public static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}

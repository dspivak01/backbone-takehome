using System.Text.RegularExpressions;

namespace ClinicalAbstraction.Questions;

public sealed class NarrativeCheckResult
{
    public int CitationsFound { get; set; }
    public int CitationsVerified { get; set; }
    public List<string> CitationsNotInResults { get; set; } = [];
    public int NumbersFound { get; set; }
    public List<string> NumbersNotInResults { get; set; } = [];
    public bool Passed => CitationsNotInResults.Count == 0 && NumbersNotInResults.Count == 0;
}

/// <summary>
/// Checks the model's prose against what the calculations returned. A citation must point to a
/// source that a calculation supplied, and a number must be one that a calculation produced or
/// that appears in the question. Anything else is reported beside the answer.
/// </summary>
public static partial class NarrativeChecks
{
    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex Bracket();

    [GeneratedRegex(@"(?<doc>[A-Za-z0-9][A-Za-z0-9_.\-]*)\s+L(?<from>\d+)(?:\s*[-–]\s*L?(?<to>\d+))?")]
    private static partial Regex Reference();

    /// <summary>A number in prose. Digits inside identifiers such as "HG-E110" are not numbers.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])\d+(?:\.\d+)?")]
    private static partial Regex Number();

    /// <summary>Every run of digits, used to collect what the results contain, including the parts of dates and times.</summary>
    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex Digits();

    public static NarrativeCheckResult Check(string narrative, string question, IEnumerable<string> toolResults)
    {
        var results = string.Join("\n", toolResults);
        var result = new NarrativeCheckResult();

        // Sources the calculations supplied, as document and line range.
        var supplied = Reference().Matches(results)
            .Select(m => (Doc: m.Groups["doc"].Value, From: int.Parse(m.Groups["from"].Value), To: m.Groups["to"].Success ? int.Parse(m.Groups["to"].Value) : int.Parse(m.Groups["from"].Value)))
            .ToList();

        foreach (Match bracket in Bracket().Matches(narrative))
        {
            foreach (Match reference in Reference().Matches(bracket.Groups[1].Value))
            {
                result.CitationsFound++;
                var doc = reference.Groups["doc"].Value;
                var from = int.Parse(reference.Groups["from"].Value);
                var to = reference.Groups["to"].Success ? int.Parse(reference.Groups["to"].Value) : from;
                if (supplied.Any(s => s.Doc.Equals(doc, StringComparison.OrdinalIgnoreCase) && from <= s.To && to >= s.From))
                    result.CitationsVerified++;
                else
                    result.CitationsNotInResults.Add(reference.Value);
            }
        }

        // Numbers: every figure in the prose, outside citations, must already exist in the results or the question.
        var allowed = Digits().Matches(results + "\n" + question).Select(m => Canonical(m.Value)).ToHashSet();
        var prose = Bracket().Replace(narrative, " ");
        foreach (Match number in Number().Matches(prose))
        {
            result.NumbersFound++;
            if (!allowed.Contains(Canonical(number.Value))) result.NumbersNotInResults.Add(number.Value);
        }
        result.NumbersNotInResults = result.NumbersNotInResults.Distinct().ToList();
        result.CitationsNotInResults = result.CitationsNotInResults.Distinct().ToList();
        return result;
    }

    private static string Canonical(string number) =>
        decimal.TryParse(number, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : number;
}

using System.Text;

namespace ClinicalAbstraction.Extraction;

public sealed record QuoteCheck(bool Found, int LineStart, int LineEnd, string Status);

/// <summary>
/// Confirms that the text a model quoted really appears at the lines it cited. Nothing the model
/// returns is stored unless its quote is found in the document.
/// </summary>
public static class QuoteVerifier
{
    private const int MaxWindow = 4;

    public static QuoteCheck Check(string[] lines, int lineStart, int lineEnd, string quote)
    {
        var wanted = Normalise(quote);
        if (wanted.Length == 0) return new QuoteCheck(false, lineStart, lineEnd, "empty quote");

        if (lineStart >= 1 && lineEnd >= lineStart && lineEnd <= lines.Length && Contains(lines, lineStart, lineEnd, wanted))
            return new QuoteCheck(true, lineStart, lineEnd, "verified");

        // The quote is real but the line numbers are off. Find the smallest range that holds it.
        for (var window = 1; window <= MaxWindow; window++)
        {
            for (var start = 1; start + window - 1 <= lines.Length; start++)
            {
                var end = start + window - 1;
                if (Contains(lines, start, end, wanted)) return new QuoteCheck(true, start, end, "relocated");
            }
        }

        return new QuoteCheck(false, lineStart, lineEnd, "quote not found in document");
    }

    private static bool Contains(string[] lines, int start, int end, string wanted)
    {
        var text = Normalise(string.Join(' ', lines[(start - 1)..end]));
        return text.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes two pieces of text comparable when they differ only in characters a model may
    /// silently change: dash style, quotation mark style, non-breaking spaces and spacing.
    /// </summary>
    public static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var raw in text)
        {
            var c = raw switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' or '″' => '"',
                ' ' or ' ' or ' ' or '\t' or '\n' or '\r' => ' ',
                _ => raw,
            };
            if (c == ' ')
            {
                if (!previousWasSpace) builder.Append(' ');
                previousWasSpace = true;
            }
            else
            {
                builder.Append(c);
                previousWasSpace = false;
            }
        }
        return builder.ToString().Trim();
    }
}

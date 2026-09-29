namespace ClinicalAbstraction.Reconciliation;

/// <summary>A span of clock time within one day, in minutes after midnight. The end is not included.</summary>
public readonly record struct Span(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
    public bool IsEmpty => End <= Start;
}

/// <summary>Arithmetic on sets of time spans. Every minute total in the system comes from here.</summary>
public static class Intervals
{
    /// <summary>Merges overlapping or touching spans and drops empty ones.</summary>
    public static List<Span> Union(IEnumerable<Span> spans)
    {
        var result = new List<Span>();
        foreach (var span in spans.Where(s => !s.IsEmpty).OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            if (result.Count > 0 && span.Start <= result[^1].End)
                result[^1] = new Span(result[^1].Start, Math.Max(result[^1].End, span.End));
            else
                result.Add(span);
        }
        return result;
    }

    /// <summary>The parts of <paramref name="spans"/> that fall inside <paramref name="window"/>.</summary>
    public static List<Span> Clip(IEnumerable<Span> spans, Span window) =>
        Union(spans.Select(s => new Span(Math.Max(s.Start, window.Start), Math.Min(s.End, window.End))));

    /// <summary>The parts of <paramref name="spans"/> that are not covered by <paramref name="removed"/>.</summary>
    public static List<Span> Subtract(IEnumerable<Span> spans, IEnumerable<Span> removed)
    {
        var cuts = Union(removed);
        var result = new List<Span>();
        foreach (var span in Union(spans))
        {
            var cursor = span.Start;
            foreach (var cut in cuts)
            {
                if (cut.End <= cursor) continue;
                if (cut.Start >= span.End) break;
                if (cut.Start > cursor) result.Add(new Span(cursor, cut.Start));
                cursor = Math.Max(cursor, cut.End);
            }
            if (cursor < span.End) result.Add(new Span(cursor, span.End));
        }
        return result;
    }

    public static int TotalMinutes(IEnumerable<Span> spans) => Union(spans).Sum(s => s.Length);
}

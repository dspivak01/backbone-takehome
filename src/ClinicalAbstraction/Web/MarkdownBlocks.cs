using System.Text;
using System.Text.RegularExpressions;

namespace ClinicalAbstraction.Web;

/// <summary>
/// Divides Markdown into the parts the review page builds as elements: headings, paragraphs,
/// lists, tables and fenced code, with bold, italic and inline code inside them. It reads the
/// model's written answer and the tables the command line prints for each calculation. Nothing it
/// returns is markup: every piece is plain text the page puts in a text node. Anything it does not
/// recognise, such as a link, a picture or an HTML tag, stays as the characters it was written with.
/// </summary>
public static partial class MarkdownBlocks
{
    /// <summary>
    /// Divides Markdown into parts. With <paramref name="literal"/>, the cells of tables and the
    /// items of lists are kept exactly as written, with no bold or italic read into them: the
    /// command line's tables carry document text there, which may hold asterisks of its own.
    /// </summary>
    public static List<Block> Read(string? markdown, bool literal = false)
    {
        List<Span> Cell(string text) => literal ? (text.Length == 0 ? [] : [new Span { Text = text }]) : Inline(text);

        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new Block { Kind = "paragraph", Spans = Inline(string.Join(" ", paragraph.Select(l => l.Trim()))) });
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                i++;
                blocks.Add(new Block { Kind = "code", Text = string.Join("\n", code) });
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                blocks.Add(new Block { Kind = "heading", Level = heading.Groups["marks"].Length, Spans = Inline(heading.Groups["text"].Value) });
                i++;
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && Separator().IsMatch(lines[i + 1].Trim()))
            {
                Flush();
                var headers = Cells(trimmed).Select(Cell).ToList();
                var rows = new List<List<List<Span>>>();
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(Cells(lines[i].Trim()).Select(Cell).ToList());
                blocks.Add(new Block { Kind = "table", Headers = headers, Rows = rows });
                continue;
            }

            if (Item().IsMatch(line))
            {
                Flush();
                blocks.Add(List(lines, ref i, Cell));
                continue;
            }

            paragraph.Add(line);
            i++;
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// A run of list items. An item indented further than the one before it belongs to that item.
    /// A line that is not an item and not blank continues the item before it.
    /// </summary>
    private static Block List(string[] lines, ref int i, Func<string, List<Span>> read)
    {
        var first = Item().Match(lines[i]);
        var top = new List<ListEntry>();
        var stack = new List<(int Indent, List<ListEntry> Items, ListEntry? Owner)> { (Indent(first.Groups["indent"].Value), top, null) };
        ListEntry? last = null;

        for (; i < lines.Length && lines[i].Trim().Length > 0; i++)
        {
            var match = Item().Match(lines[i]);
            if (!match.Success)
            {
                if (last is null) break;
                last.Spans.AddRange(read(" " + lines[i].Trim()));
                continue;
            }

            var indent = Indent(match.Groups["indent"].Value);
            while (stack.Count > 1 && indent < stack[^1].Indent) stack.RemoveAt(stack.Count - 1);
            if (indent > stack[^1].Indent && last is not null)
            {
                last.Children = [];
                last.Ordered = char.IsDigit(match.Groups["mark"].Value[0]);
                stack.Add((indent, last.Children, last));
            }

            last = new ListEntry { Spans = read(match.Groups["text"].Value) };
            stack[^1].Items.Add(last);
        }

        return new Block { Kind = "list", Ordered = char.IsDigit(first.Groups["mark"].Value[0]), Items = top };
    }

    private static int Indent(string whitespace) => whitespace.Sum(c => c == '\t' ? 4 : 1);

    /// <summary>The cells of a table row. A "|" written as "\|", as the command line writes one inside a cell, stays in its cell.</summary>
    public static List<string> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        return UnescapedPipe().Split(text).Select(c => c.Trim().Replace("\\|", "|")).ToList();
    }

    /// <summary>Bold, italic and inline code inside one line. Everything else is kept as written.</summary>
    public static List<Span> Inline(string text)
    {
        var spans = new List<Span>();
        var plain = new StringBuilder();

        void Emit()
        {
            if (plain.Length == 0) return;
            spans.Add(new Span { Text = plain.ToString() });
            plain.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_".Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`' && text.IndexOf('`', i + 1) is var codeEnd and > 0 && codeEnd > i + 1)
            {
                Emit();
                spans.Add(new Span { Text = text[(i + 1)..codeEnd], Code = true });
                i = codeEnd + 1;
                continue;
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var boldEnd and > 0 && boldEnd > i + 2)
            {
                Emit();
                foreach (var inner in Inline(text[(i + 2)..boldEnd]))
                {
                    inner.Bold = true;
                    spans.Add(inner);
                }
                i = boldEnd + 2;
                continue;
            }
            if (c == '*' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '*'
                && text.IndexOf('*', i + 1) is var italicEnd and > 0 && !char.IsWhiteSpace(text[italicEnd - 1]))
            {
                Emit();
                foreach (var inner in Inline(text[(i + 1)..italicEnd]))
                {
                    inner.Italic = true;
                    spans.Add(inner);
                }
                i = italicEnd + 1;
                continue;
            }
            plain.Append(c);
            i++;
        }

        Emit();
        return spans;
    }

    [GeneratedRegex(@"^\s{0,3}(?<marks>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\|?(\s*:?-+:?\s*\|)+\s*(:?-+:?\s*)?$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(?<indent>\s*)(?<mark>[-*+]|\d{1,3}[.)])\s+(?<text>.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();
}

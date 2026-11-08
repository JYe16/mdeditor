using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MDEditor.Core;

/// <summary>Text plus selection. UTF-16 offsets and '\n' line endings only.</summary>
public sealed record EditState(string Text, int SelStart, int SelLength)
{
    public int SelEnd => SelStart + SelLength;
}

/// <summary>
/// Pure port of MarkdownTextView.swift: list/quote continuation, Tab shifting and the Format commands.
/// The UI applies the returned state as one minimal replace, so each command is a single undo step.
/// </summary>
public static partial class MarkdownCommands
{
    private const string IndentUnit = "    ";

    [GeneratedRegex(@"^([ \t]*)((?:[-*+]|(\d{1,9})([.)]))[ \t]+)(\[[ xX]\][ \t]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^([ \t]{0,3}(?:>[ \t]?)+)", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteRegex();

    /// <summary>Full lines touching the selection, including the trailing '\n' (NSString.lineRange).</summary>
    public static (int Start, int Length) LineRange(string text, int start, int length)
    {
        int lineStart = start <= 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int probe = length > 0 ? start + length - 1 : start;
        int newline = probe < text.Length ? text.IndexOf('\n', probe) : -1;
        int end = newline < 0 ? text.Length : newline + 1;
        return (lineStart, end - lineStart);
    }

    private static List<string> SplitKeepingNewlines(string block)
    {
        var lines = new List<string>();
        int i = 0;
        while (i < block.Length)
        {
            int nl = block.IndexOf('\n', i);
            int end = nl < 0 ? block.Length : nl + 1;
            lines.Add(block[i..end]);
            i = end;
        }
        return lines;
    }

    private static string Splice(string text, int start, int length, string replacement) =>
        string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + length));

    private static int LengthWithoutTrailingNewline(string s) => s.Length - (s.EndsWith('\n') ? 1 : 0);

    // ---- Enter (insertNewline) ---------------------------------------------------------------

    /// <summary>Returns the new state, or null for "do the default newline".</summary>
    public static EditState? Enter(EditState s)
    {
        if (s.SelLength != 0) return null;
        var (lineStart, lineLength) = LineRange(s.Text, s.SelStart, 0);
        int lineEnd = lineStart + lineLength;
        while (lineEnd > lineStart && s.Text[lineEnd - 1] == '\n') lineEnd--;
        if (s.SelStart != lineEnd) return null;
        string line = s.Text[lineStart..lineEnd];

        var list = ListRegex().Match(line);
        if (list.Success)
        {
            if (string.IsNullOrWhiteSpace(line[list.Length..]))
                return new EditState(Splice(s.Text, lineStart, lineEnd - lineStart, ""), lineStart, 0);

            string next = list.Groups[2].Value;
            if (list.Groups[3].Success &&
                int.TryParse(list.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                string spacing = new(next.SkipWhile(c => c != ' ' && c != '\t').ToArray());
                next = (n + 1).ToString(CultureInfo.InvariantCulture) + list.Groups[4].Value + spacing;
            }
            string insert = "\n" + list.Groups[1].Value + next + (list.Groups[5].Success ? "[ ] " : "");
            return new EditState(Splice(s.Text, s.SelStart, 0, insert), s.SelStart + insert.Length, 0);
        }

        var quote = QuoteRegex().Match(line);
        if (quote.Success)
        {
            if (string.IsNullOrWhiteSpace(line[quote.Length..]))
                return new EditState(Splice(s.Text, lineStart, lineEnd - lineStart, ""), lineStart, 0);
            string insert = "\n" + quote.Groups[1].Value;
            return new EditState(Splice(s.Text, s.SelStart, 0, insert), s.SelStart + insert.Length, 0);
        }
        return null;
    }

    // ---- Tab / Shift-Tab (insertTab, insertBacktab, shiftLines) ------------------------------

    public static EditState Tab(EditState s)
    {
        var (start, length) = LineRange(s.Text, s.SelStart, 0);
        if (s.SelLength > 0 || ListRegex().IsMatch(s.Text.Substring(start, length)))
            return ShiftLines(s, indent: true);
        return new EditState(Splice(s.Text, s.SelStart, 0, IndentUnit), s.SelStart + IndentUnit.Length, 0);
    }

    public static EditState BackTab(EditState s) => ShiftLines(s, indent: false);

    private static EditState ShiftLines(EditState s, bool indent)
    {
        var (start, length) = LineRange(s.Text, s.SelStart, s.SelLength);
        string original = s.Text.Substring(start, length);
        var output = new StringBuilder(original.Length + 16);
        int firstDelta = 0;
        bool isFirst = true;

        foreach (string line in SplitKeepingNewlines(original))
        {
            string shifted = line;
            int delta = 0;
            if (indent)
            {
                if (line.Trim('\n').Length > 0)
                {
                    shifted = IndentUnit + line;
                    delta = IndentUnit.Length;
                }
            }
            else if (line.StartsWith('\t'))
            {
                shifted = line[1..];
                delta = -1;
            }
            else
            {
                int n = 0;
                while (n < IndentUnit.Length && n < line.Length && line[n] == ' ') n++;
                shifted = line[n..];
                delta = -n;
            }
            if (isFirst) { firstDelta = delta; isFirst = false; }
            output.Append(shifted);
        }

        string result = output.ToString();
        if (result == original) return s;
        string text = Splice(s.Text, start, length, result);
        return s.SelLength == 0
            ? new EditState(text, Math.Max(start, s.SelStart + firstDelta), 0)
            : new EditState(text, start, LengthWithoutTrailingNewline(result));
    }

    // ---- Format commands ---------------------------------------------------------------------

    public static EditState Bold(EditState s) => ToggleInline(s, "**");
    public static EditState Italic(EditState s) => ToggleInline(s, "*");
    public static EditState Strike(EditState s) => ToggleInline(s, "~~");
    public static EditState InlineCode(EditState s) => ToggleInline(s, "`");
    public static EditState Quote(EditState s) => ToggleLinePrefix(s, "> ");
    public static EditState Bullet(EditState s) => ToggleLinePrefix(s, "- ");
    public static EditState Task(EditState s) => ToggleLinePrefix(s, "- [ ] ");

    private static EditState ToggleInline(EditState s, string marker)
    {
        int m = marker.Length;
        bool wrapped = s.SelStart >= m
            && s.SelEnd + m <= s.Text.Length
            && string.CompareOrdinal(s.Text, s.SelStart - m, marker, 0, m) == 0
            && string.CompareOrdinal(s.Text, s.SelEnd, marker, 0, m) == 0;

        string selected = s.Text.Substring(s.SelStart, s.SelLength);
        if (wrapped)
            return new EditState(Splice(s.Text, s.SelStart - m, s.SelLength + 2 * m, selected), s.SelStart - m, s.SelLength);
        return new EditState(Splice(s.Text, s.SelStart, s.SelLength, marker + selected + marker), s.SelStart + m, s.SelLength);
    }

    public static EditState Link(EditState s)
    {
        const string placeholder = "url";
        string selected = s.Text.Substring(s.SelStart, s.SelLength);
        string text = Splice(s.Text, s.SelStart, s.SelLength, $"[{selected}]({placeholder})");
        return new EditState(text, s.SelStart + 1 + selected.Length + 2, placeholder.Length);
    }

    public static EditState CodeBlock(EditState s)
    {
        string selected = s.Text.Substring(s.SelStart, s.SelLength);
        string lead = s.SelStart > 0 && s.Text[s.SelStart - 1] != '\n' ? "\n" : "";
        string inserted = $"{lead}```\n{selected}\n```\n";
        int caret = s.SelLength == 0 ? s.SelStart + lead.Length + 4 : s.SelStart + inserted.Length;
        return new EditState(Splice(s.Text, s.SelStart, s.SelLength, inserted), caret, 0);
    }

    public static EditState Heading(EditState s, int level)
    {
        var (start, length) = LineRange(s.Text, s.SelStart, 0);
        string content = s.Text.Substring(start, length);
        string newline = content.EndsWith('\n') ? "\n" : "";
        if (newline.Length > 0) content = content[..^1];

        int existing = 0;
        while (existing < content.Length && content[existing] == '#') existing++;
        string body = content;
        if (existing is > 0 and <= 6)
        {
            body = content[existing..];
            if (body.StartsWith(' ')) body = body[1..];
        }
        string replacement = existing == level ? body : new string('#', level) + " " + body;
        return new EditState(Splice(s.Text, start, length, replacement + newline), start + replacement.Length, 0);
    }

    private static EditState ToggleLinePrefix(EditState s, string prefix)
    {
        var (start, length) = LineRange(s.Text, s.SelStart, s.SelLength);
        var lines = SplitKeepingNewlines(s.Text.Substring(start, length));
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        bool allHave = nonEmpty.Count > 0 && nonEmpty.All(l => l.StartsWith(prefix, StringComparison.Ordinal));

        string result = string.Concat(lines.Select(line =>
            string.IsNullOrWhiteSpace(line) ? line : allHave ? line[prefix.Length..] : prefix + line));
        return new EditState(Splice(s.Text, start, length, result), start, LengthWithoutTrailingNewline(result));
    }

    /// <summary>True when the line holding the caret is a list item (used by the UI for Tab decisions).</summary>
    public static bool IsListLine(string line) => ListRegex().IsMatch(line);
}

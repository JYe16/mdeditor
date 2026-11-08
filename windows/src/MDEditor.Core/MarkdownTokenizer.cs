using System.Text.RegularExpressions;

namespace MDEditor.Core;

/// <summary>Foreground colour roles, stored in the low four bits of a style byte.</summary>
public enum ColorRole : byte
{
    Default = 0, Secondary, Blue, Orange, Purple, Teal, Indigo, Pink,
}

/// <summary>A style byte is <see cref="ColorRole"/> | flags. Geometry never changes, only colour and weight.</summary>
public static class StyleBits
{
    public const byte RoleMask = 0x0F;
    public const byte Bold = 0x10;
    public const byte Italic = 0x20;
    public const byte Strike = 0x40;
    public const byte CodeBackground = 0x80;

    /// <summary>"Style not known": never produced by the tokenizer.</summary>
    public const byte Unknown = 0xFF;
}

/// <summary>
/// Line-by-line block scan plus inline regexes. Port of MarkdownHighlighter.swift; later passes win
/// attribute by attribute. Produces one style byte per UTF-16 code unit of the '\n'-normalised text.
/// </summary>
public static partial class MarkdownTokenizer
{
    public const int MaxLength = 300_000;

    [GeneratedRegex(@"^[ \t]*((?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();
    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HRule();
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(?:\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TableSep();
    [GeneratedRegex(@"(?<![*\w\\])([*_])(?![*_\s])([^\n*_]*?[^\s*_\\])\1(?![*\w])", RegexOptions.CultureInvariant)]
    private static partial Regex ItalicRe();
    [GeneratedRegex(@"(\*\*|__)(?=\S)([^\n]*?\S)\1", RegexOptions.CultureInvariant)]
    private static partial Regex BoldRe();
    [GeneratedRegex(@"~~(?=\S)[^\n]*?\S~~", RegexOptions.CultureInvariant)]
    private static partial Regex StrikeRe();
    [GeneratedRegex(@"!?\[([^\]\n]*)\]\(([^)\n]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRe();
    [GeneratedRegex(@"<(?:https?://|mailto:)[^>\s]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AutoLink();
    [GeneratedRegex(@"\$\$[^\n]+?\$\$|(?<![\\$])\$(?![\s$])[^$\n]*?(?<![\s\\])\$(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex MathRe();
    [GeneratedRegex(@"(`+)(?!`)[^\n]*?[^`\n](\1)(?!`)|`[^`\n]`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

    /// <summary>Returns one style byte per char, all default when the text is over <see cref="MaxLength"/>.</summary>
    public static byte[] Tokenize(string text, CancellationToken cancel = default)
    {
        var styles = new byte[text.Length];
        if (text.Length == 0 || text.Length > MaxLength) return styles;

        var blocked = ScanBlocks(text, styles);

        void ForEach(Regex regex, Action<Match> body)
        {
            cancel.ThrowIfCancellationRequested();
            for (Match m = regex.Match(text); m.Success; m = m.NextMatch())
                if (!Intersects(blocked, m.Index, m.Length)) body(m);
        }

        ForEach(ListMarker(), m => Color(styles, m.Groups[1], ColorRole.Orange));
        ForEach(HRule(), m => Color(styles, m, ColorRole.Secondary));
        ForEach(TableSep(), m => Color(styles, m, ColorRole.Secondary));

        ForEach(ItalicRe(), m => { Color(styles, m, ColorRole.Purple); Set(styles, m.Index, m.Length, StyleBits.Italic); });
        ForEach(BoldRe(), m => Set(styles, m.Index, m.Length, StyleBits.Bold));
        ForEach(StrikeRe(), m => { Color(styles, m, ColorRole.Secondary); Set(styles, m.Index, m.Length, StyleBits.Strike); });
        ForEach(LinkRe(), m => { Color(styles, m.Groups[1], ColorRole.Blue); Color(styles, m.Groups[2], ColorRole.Secondary); });
        ForEach(AutoLink(), m => Color(styles, m, ColorRole.Blue));
        ForEach(MathRe(), m => Color(styles, m, ColorRole.Indigo));
        ForEach(InlineCode(), m =>
        {
            Color(styles, m, ColorRole.Pink);
            Set(styles, m.Index, m.Length, StyleBits.CodeBackground);
            Clear(styles, m.Index, m.Length, StyleBits.Italic);
        });
        return styles;
    }

    private static void Color(byte[] s, Capture c, ColorRole role) => Color(s, c.Index, c.Length, role);

    private static void Color(byte[] s, int start, int length, ColorRole role)
    {
        for (int i = start; i < start + length; i++)
            s[i] = (byte)((s[i] & ~StyleBits.RoleMask) | (byte)role);
    }

    private static void Set(byte[] s, int start, int length, byte flag)
    {
        for (int i = start; i < start + length; i++) s[i] |= flag;
    }

    private static void Clear(byte[] s, int start, int length, byte flag)
    {
        for (int i = start; i < start + length; i++) s[i] &= (byte)~flag;
    }

    private static bool Intersects(List<(int Start, int End)> ranges, int start, int length)
    {
        if (length == 0 || ranges.Count == 0) return false;
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var r = ranges[mid];
            if (start + length <= r.Start) hi = mid - 1;
            else if (start >= r.End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static bool OnlySpacesOrTabs(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (text[i] is not (' ' or '\t')) return false;
        return true;
    }

    /// <summary>Front matter, fences, headings, quotes. Returns the sorted ranges inline matches must skip.</summary>
    private static List<(int Start, int End)> ScanBlocks(string text, byte[] styles)
    {
        var blocked = new List<(int, int)>();
        bool inFront = false, inFence = false;
        char fenceChar = '\0';
        int fenceLength = 0, blockStart = 0, lineIndex = 0, pos = 0;

        while (pos < text.Length)
        {
            int newline = text.IndexOf('\n', pos);
            int end = newline < 0 ? text.Length : newline;
            int enclosingEnd = newline < 0 ? text.Length : newline + 1;
            int line = pos, length = end - pos, enclosingLength = enclosingEnd - line;

            pos = enclosingEnd;
            int currentIndex = lineIndex++;

            if (currentIndex == 0 && length >= 3 && text.AsSpan(line, length).Trim(" \t").SequenceEqual("---"))
            {
                inFront = true;
                blockStart = line;
                Color(styles, line, enclosingLength, ColorRole.Secondary);
                continue;
            }
            if (inFront)
            {
                Color(styles, line, enclosingLength, ColorRole.Secondary);
                var trimmed = text.AsSpan(line, length).Trim(" \t");
                if (trimmed.SequenceEqual("---") || trimmed.SequenceEqual("..."))
                {
                    inFront = false;
                    blocked.Add((blockStart, enclosingEnd));
                }
                continue;
            }

            int i = line, spaces = 0;
            while (i < end && text[i] == ' ' && spaces < 3) { i++; spaces++; }

            if (!inFence)
            {
                if (i < end && text[i] is '`' or '~' && i < end)
                {
                    char c = text[i];
                    int j = i;
                    while (j < end && text[j] == c) j++;
                    if (j - i >= 3)
                    {
                        inFence = true;
                        fenceChar = c;
                        fenceLength = j - i;
                        blockStart = line;
                        Color(styles, line, enclosingLength, ColorRole.Secondary);
                        continue;
                    }
                }
            }
            else
            {
                int j = i;
                while (j < end && text[j] == fenceChar) j++;
                if (j - i >= fenceLength && OnlySpacesOrTabs(text, j, end))
                {
                    inFence = false;
                    blocked.Add((blockStart, enclosingEnd));
                    Color(styles, line, enclosingLength, ColorRole.Secondary);
                }
                else
                {
                    Color(styles, line, enclosingLength, ColorRole.Teal);
                }
                continue;
            }

            if (length > 0 && text[line] == '#')
            {
                int n = 0;
                while (line + n < end && text[line + n] == '#') n++;
                if (n <= 6 && (line + n == end || text[line + n] == ' '))
                {
                    Color(styles, line, length, ColorRole.Blue);
                    Set(styles, line, length, StyleBits.Bold);
                    Color(styles, line, n, ColorRole.Secondary);
                    continue;
                }
            }

            if (i < end && text[i] == '>') Color(styles, line, length, ColorRole.Secondary);
        }

        if (inFence || inFront) blocked.Add((blockStart, text.Length));
        return blocked;
    }
}

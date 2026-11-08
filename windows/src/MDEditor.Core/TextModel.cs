using System.Text;

namespace MDEditor.Core;

/// <summary>Word/character counts for the status bar. Port of TextStats in ContentView.swift.</summary>
public readonly record struct TextStats(int Words, int Characters, int Minutes)
{
    public static TextStats Of(string text)
    {
        int cjk = 0, latin = 0, chars = 0;
        bool inWord = false;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(r)) chars++;
            if (IsCjk(r.Value))
            {
                cjk++;
                inWord = false;
            }
            else if (Rune.IsLetter(r) || Rune.IsNumber(r))
            {
                if (!inWord) { latin++; inWord = true; }
            }
            else
            {
                inWord = false;
            }
        }
        int words = cjk + latin;
        double minutes = cjk / 400.0 + latin / 230.0;
        return new TextStats(words, chars, words == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(minutes)));
    }

    public static bool IsCjk(int v) =>
        v is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
          or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F);
}

/// <summary>Start offsets of each logical line; binary-searched for scroll sync.</summary>
public sealed class LineIndex
{
    private readonly List<int> _starts = new() { 0 };

    public LineIndex(string text)
    {
        for (int i = text.IndexOf('\n'); i >= 0; i = i + 1 < text.Length ? text.IndexOf('\n', i + 1) : -1)
            _starts.Add(i + 1);
    }

    public int LineCount => _starts.Count;
    public int StartOf(int line) => _starts[Math.Clamp(line, 0, _starts.Count - 1)];

    public int LineOf(int offset)
    {
        int lo = 0, hi = _starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>One replaced range, recorded for undo.</summary>
public sealed record TextEdit(int Offset, string Removed, string Inserted, int CaretBefore, int SelLenBefore, int SelStartAfter, int SelLenAfter);

/// <summary>Smallest single replacement that turns one text into another.</summary>
public static class TextDiff
{
    /// <param name="caretAfter">
    /// Caret in <paramref name="updated"/> after a user edit, or -1. Text after the caret is known to be unchanged,
    /// which resolves the ambiguity when typing inside a run of identical characters.
    /// </param>
    public static (int Offset, int RemovedLength, int InsertedLength) Minimal(string original, string updated, int caretAfter = -1)
    {
        int maxSuffix = Math.Min(original.Length, updated.Length);
        if (caretAfter >= 0) maxSuffix = Math.Min(maxSuffix, updated.Length - caretAfter);
        int suffix = 0;
        while (suffix < maxSuffix && original[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;

        int maxPrefix = Math.Min(original.Length - suffix, updated.Length - suffix);
        int prefix = 0;
        while (prefix < maxPrefix && original[prefix] == updated[prefix]) prefix++;

        // Never split a surrogate pair across the replaced range.
        if (prefix > 0 && char.IsHighSurrogate(original[prefix - 1])) prefix--;
        while (suffix > 0 && char.IsLowSurrogate(original[^suffix])) suffix--;

        return (prefix, original.Length - prefix - suffix, updated.Length - prefix - suffix);
    }
}

/// <summary>Undo history kept outside the text control, so colouring can never pollute it (HANDOFF §7, G2).</summary>
public sealed class UndoStack
{
    private const int Limit = 5000;
    private const long CoalesceWindowMs = 1200;

    private readonly LinkedList<TextEdit> _undo = new();
    private readonly Stack<TextEdit> _redo = new();
    private long _lastPushMs = long.MinValue;
    private bool _lastCoalescable;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastCoalescable = false;
    }

    /// <summary>Stop the next typed edit from merging into the previous one (caret moved, command ran…).</summary>
    public void BreakCoalescing() => _lastCoalescable = false;

    public void Push(TextEdit edit, bool typing, long nowMs)
    {
        _redo.Clear();
        if (typing && _lastCoalescable && _undo.Last is { } last && nowMs - _lastPushMs <= CoalesceWindowMs
            && TryMerge(last.Value, edit) is { } merged)
        {
            last.Value = merged;
        }
        else
        {
            _undo.AddLast(edit);
            if (_undo.Count > Limit) _undo.RemoveFirst();
        }
        _lastPushMs = nowMs;
        _lastCoalescable = typing;
    }

    private static TextEdit? TryMerge(TextEdit a, TextEdit b)
    {
        bool aInsert = a.Removed.Length == 0 && a.Inserted.Length > 0;
        bool bInsert = b.Removed.Length == 0 && b.Inserted.Length is 1 or 2;
        if (aInsert && bInsert && b.Offset == a.Offset + a.Inserted.Length && !b.Inserted.Contains('\n')
            && !(char.IsWhiteSpace(a.Inserted[^1]) && !char.IsWhiteSpace(b.Inserted[0])))
        {
            return a with { Inserted = a.Inserted + b.Inserted, SelStartAfter = b.SelStartAfter, SelLenAfter = b.SelLenAfter };
        }

        bool aDelete = a.Inserted.Length == 0 && a.Removed.Length > 0;
        bool bDelete = b.Inserted.Length == 0 && b.Removed.Length is 1 or 2;
        if (aDelete && bDelete && !b.Removed.Contains('\n'))
        {
            if (b.Offset + b.Removed.Length == a.Offset) // Backspace
                return a with { Offset = b.Offset, Removed = b.Removed + a.Removed, SelStartAfter = b.SelStartAfter, SelLenAfter = b.SelLenAfter };
            if (b.Offset == a.Offset)                    // Delete
                return a with { Removed = a.Removed + b.Removed, SelStartAfter = b.SelStartAfter, SelLenAfter = b.SelLenAfter };
        }
        return null;
    }

    public TextEdit? Undo()
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(last.Value);
        _lastCoalescable = false;
        return last.Value;
    }

    public TextEdit? Redo()
    {
        if (_redo.Count == 0) return null;
        TextEdit edit = _redo.Pop();
        _undo.AddLast(edit);
        _lastCoalescable = false;
        return edit;
    }
}

/// <summary>Plain-text search for the find bar.</summary>
public static class TextSearch
{
    public static List<int> FindAll(string text, string query, bool matchCase, bool wholeWord)
    {
        var hits = new List<int>();
        if (query.Length == 0) return hits;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int i = 0;
        while (i <= text.Length - query.Length)
        {
            int hit = text.IndexOf(query, i, comparison);
            if (hit < 0) break;
            if (!wholeWord || IsWholeWord(text, hit, query.Length))
            {
                hits.Add(hit);
                i = hit + query.Length;
            }
            else
            {
                i = hit + 1;
            }
        }
        return hits;
    }

    private static bool IsWordChar(string text, int index)
    {
        char c = text[index];
        return (char.IsLetterOrDigit(c) || c == '_') && !TextStats.IsCjk(c);
    }

    private static bool IsWholeWord(string text, int start, int length) =>
        (start == 0 || !IsWordChar(text, start - 1)) &&
        (start + length >= text.Length || !IsWordChar(text, start + length));

    /// <summary>Replaces every hit; returns the new text and how many replacements were made.</summary>
    public static (string Text, int Count) ReplaceAll(string text, string query, string replacement, bool matchCase, bool wholeWord)
    {
        var hits = FindAll(text, query, matchCase, wholeWord);
        if (hits.Count == 0) return (text, 0);
        var sb = new StringBuilder(text.Length + hits.Count * Math.Max(replacement.Length - query.Length, 0));
        int position = 0;
        foreach (int hit in hits)
        {
            sb.Append(text, position, hit - position).Append(replacement);
            position = hit + query.Length;
        }
        sb.Append(text, position, text.Length - position);
        return (sb.ToString(), hits.Count);
    }
}

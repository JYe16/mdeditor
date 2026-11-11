namespace MDEditor.Core;

/// <summary>
/// Where a double-click in the preview lands in the editor: the clamped selection plus the line it is on.
/// Port of the arithmetic in jumpEditor(toLine:column:length:) in SplitEditorView.swift. All offsets are UTF-16 code
/// units in model coordinates ('\n' line endings), which is what .NET string indices and the editor's Select() use.
/// </summary>
/// <param name="Line">Line the selection starts on, after clamping.</param>
/// <param name="Start">Offset of the first selected character (the caret position when <paramref name="Length"/> is 0).</param>
/// <param name="Length">Number of UTF-16 units to select; never runs past the end of the line.</param>
/// <param name="LineStart">Offset of the first character of the line.</param>
/// <param name="LineEnd">Offset just past the last character of the line, excluding its '\n'.</param>
public readonly record struct JumpTarget(int Line, int Start, int Length, int LineStart, int LineEnd)
{
    /// <summary>
    /// Clamps everything: the preview can be a few tens of milliseconds behind the editor, so the numbers it reports
    /// may point past the end of a line or the document. Line goes to [0, LineCount-1], column to the line length,
    /// length to what is left of the line.
    /// </summary>
    public static JumpTarget Resolve(LineIndex lines, int textLength, int line, int column, int length)
    {
        int index = Math.Clamp(line, 0, lines.LineCount - 1);
        int start = Math.Min(lines.StartOf(index), textLength);
        int end = index + 1 < lines.LineCount ? lines.StartOf(index + 1) - 1 : textLength;
        end = Math.Max(end, start);
        int col = Math.Clamp(column, 0, end - start);
        int len = Math.Clamp(length, 0, end - start - col);
        return new JumpTarget(index, start + col, len, start, end);
    }
}

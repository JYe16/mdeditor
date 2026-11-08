using Windows.System;

namespace MDEditor;

internal enum Cmd
{
    New, Open, Save, SaveAs, ExportPdf, Print, CloseWindow, Exit, ToggleAutoSave,
    Undo, Redo, Cut, Copy, Paste, SelectAll, Find, Replace, FindNext, FindPrevious,
    Bold, Italic, Strike, InlineCode, Link, Heading1, Heading2, Heading3, Quote, Bullet, Task, CodeBlock,
    ViewEditor, ViewSplit, ViewPreview, ZoomIn, ZoomOut, ZoomReset,
    About,
}

internal readonly record struct Shortcut(VirtualKey Key, bool Ctrl, bool Shift, Cmd Command);

/// <summary>
/// The one shortcut table. Keys are handled at window level (and forwarded from the preview page), so no
/// shortcut can be swallowed by WebView2 or by RichEditBox's built-in rich-text keys. Menus only display them.
/// Ctrl+Alt chords are deliberately absent: Ctrl+Alt is AltGr on many keyboard layouts.
/// </summary>
internal static class Shortcuts
{
    private const VirtualKey OemPlus = (VirtualKey)187;
    private const VirtualKey OemMinus = (VirtualKey)189;

    private static readonly Shortcut[] Table =
    [
        new(VirtualKey.N, true, false, Cmd.New),
        new(VirtualKey.O, true, false, Cmd.Open),
        new(VirtualKey.S, true, false, Cmd.Save),
        new(VirtualKey.S, true, true, Cmd.SaveAs),
        new(VirtualKey.P, true, false, Cmd.Print),
        new(VirtualKey.W, true, false, Cmd.CloseWindow),
        new(VirtualKey.Z, true, false, Cmd.Undo),
        new(VirtualKey.Y, true, false, Cmd.Redo),
        new(VirtualKey.Z, true, true, Cmd.Redo),
        new(VirtualKey.X, true, false, Cmd.Cut),
        new(VirtualKey.C, true, false, Cmd.Copy),
        new(VirtualKey.V, true, false, Cmd.Paste),
        new(VirtualKey.A, true, false, Cmd.SelectAll),
        new(VirtualKey.F, true, false, Cmd.Find),
        new(VirtualKey.H, true, false, Cmd.Replace),
        new(VirtualKey.F3, false, false, Cmd.FindNext),
        new(VirtualKey.F3, false, true, Cmd.FindPrevious),
        new(VirtualKey.B, true, false, Cmd.Bold),
        new(VirtualKey.I, true, false, Cmd.Italic),
        new(VirtualKey.X, true, true, Cmd.Strike),
        new(VirtualKey.E, true, false, Cmd.InlineCode),
        new(VirtualKey.K, true, false, Cmd.Link),
        new(VirtualKey.Number1, true, false, Cmd.Heading1),
        new(VirtualKey.Number2, true, false, Cmd.Heading2),
        new(VirtualKey.Number3, true, false, Cmd.Heading3),
        new(VirtualKey.Q, true, true, Cmd.Quote),
        new(VirtualKey.L, true, true, Cmd.Bullet),
        new(VirtualKey.T, true, true, Cmd.Task),
        new(VirtualKey.K, true, true, Cmd.CodeBlock),
        new(VirtualKey.Number1, true, true, Cmd.ViewEditor),
        new(VirtualKey.Number2, true, true, Cmd.ViewSplit),
        new(VirtualKey.Number3, true, true, Cmd.ViewPreview),
        new(OemPlus, true, false, Cmd.ZoomIn),
        new(VirtualKey.Add, true, false, Cmd.ZoomIn),
        new(OemMinus, true, false, Cmd.ZoomOut),
        new(VirtualKey.Subtract, true, false, Cmd.ZoomOut),
        new(VirtualKey.Number0, true, false, Cmd.ZoomReset),
        new(VirtualKey.NumberPad0, true, false, Cmd.ZoomReset),
    ];

    public static Cmd? Find(VirtualKey key, bool ctrl, bool shift)
    {
        foreach (var s in Table)
            if (s.Key == key && s.Ctrl == ctrl && s.Shift == shift) return s.Command;
        return null;
    }

    /// <summary>Text shown next to a menu item.</summary>
    public static string? Display(Cmd command) => command switch
    {
        Cmd.New => "Ctrl+N", Cmd.Open => "Ctrl+O", Cmd.Save => "Ctrl+S", Cmd.SaveAs => "Ctrl+Shift+S",
        Cmd.Print => "Ctrl+P", Cmd.CloseWindow => "Ctrl+W", Cmd.Exit => "Alt+F4",
        Cmd.Undo => "Ctrl+Z", Cmd.Redo => "Ctrl+Y", Cmd.Cut => "Ctrl+X", Cmd.Copy => "Ctrl+C", Cmd.Paste => "Ctrl+V",
        Cmd.SelectAll => "Ctrl+A", Cmd.Find => "Ctrl+F", Cmd.Replace => "Ctrl+H", Cmd.FindNext => "F3", Cmd.FindPrevious => "Shift+F3",
        Cmd.Bold => "Ctrl+B", Cmd.Italic => "Ctrl+I", Cmd.Strike => "Ctrl+Shift+X", Cmd.InlineCode => "Ctrl+E", Cmd.Link => "Ctrl+K",
        Cmd.Heading1 => "Ctrl+1", Cmd.Heading2 => "Ctrl+2", Cmd.Heading3 => "Ctrl+3",
        Cmd.Quote => "Ctrl+Shift+Q", Cmd.Bullet => "Ctrl+Shift+L", Cmd.Task => "Ctrl+Shift+T", Cmd.CodeBlock => "Ctrl+Shift+K",
        Cmd.ViewEditor => "Ctrl+Shift+1", Cmd.ViewSplit => "Ctrl+Shift+2", Cmd.ViewPreview => "Ctrl+Shift+3",
        Cmd.ZoomIn => "Ctrl+=", Cmd.ZoomOut => "Ctrl+-", Cmd.ZoomReset => "Ctrl+0",
        _ => null,
    };
}

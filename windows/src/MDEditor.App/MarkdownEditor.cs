using System.Diagnostics;
using MDEditor.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MDEditor;

/// <summary>
/// RichEditBox used as a plain-text Markdown editor. See windows/docs/editor-decision.md.
/// The rest of the app only ever sees text with '\n' line endings; RichEdit's '\r' stays in here.
/// History is kept in <see cref="UndoStack"/>, because RichEdit would otherwise record every colour change.
/// </summary>
internal sealed class MarkdownEditor
{
    private const double BaseFontSizeDip = 14;
    private const double LineHeightRatio = 1.62;
    private const int ViewportMargin = 4000;
    private const int PumpBudgetMs = 6;

    public RichEditBox Box { get; }
    public ScrollViewer? Scroller { get; private set; }

    /// <summary>Raised after every change to the text.</summary>
    public event Action? TextEdited;
    public event Action? Scrolled;
    public event Action<double>? WheelZoom;

    public string Text => _text;
    public bool IsComposing => _composing;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    private RichEditTextDocument Doc => Box.Document;

    private readonly UndoStack _undo = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _fontFace;
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private string _text = "";
    private bool _composing;
    private bool _contentDirty;
    private int _expectedCaret = -1;
    private double _zoom = 1.0;
    private bool _darkTheme;

    private byte[] _applied = [];
    private byte[] _desired = [];
    private string? _desiredFor;
    private CancellationTokenSource? _tokenizeCancel;
    private bool _pumpQueued;
    private readonly ITextCharacterFormat?[] _templates = new ITextCharacterFormat?[256];
    private Color[] _palette = [];

    /// <summary>Milliseconds spent in the most recent pump slice (G3 measurement).</summary>
    public double LastPumpMs { get; private set; }

    public MarkdownEditor()
    {
        _fontFace = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "CascadiaMono.ttf"))
            ? "Cascadia Mono" : "Consolas";

        Box = new RichEditBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            DisabledFormattingAccelerators = DisabledFormattingAccelerators.All,
            ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
            SelectionFlyout = null,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(18, 16, 18, 16),
            FontFamily = new FontFamily(_fontFace),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SelectionHighlightColorWhenNotFocused = new SolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xA8, 0xD8)),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Box, L.T("Editor"));
        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
        })
        {
            Box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        }

        Doc.UndoLimit = 0;

        Box.TextChanging += (_, e) => { if (e.IsContentChanging) _contentDirty = true; };
        Box.TextChanged += (_, _) => OnControlTextChanged();
        Box.SelectionChanged += (_, _) => OnSelectionChanged();
        Box.TextCompositionStarted += (_, _) => _composing = true;
        Box.TextCompositionEnded += (_, _) => OnCompositionEnded();
        Box.PreviewKeyDown += OnPreviewKeyDown;
        Box.Paste += (_, e) => { e.Handled = true; _ = PasteAsync(); };
        Box.PointerWheelChanged += OnPointerWheelChanged;
        Box.Loaded += (_, _) => AttachScroller();

        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); StartTokenize(); };
        SetTheme(dark: false);
        ApplyBaseFormatting();
    }

    // ---- Control <-> model boundary ----------------------------------------------------------

    private string ReadControlText()
    {
        Doc.GetText(TextGetOptions.None, out string raw);
        // The control always ends with a paragraph mark that is not part of the document.
        if (raw.EndsWith('\r')) raw = raw[..^1];
        return raw.Replace('\r', '\n');
    }

    private static string ToControl(string text) => text.Replace('\n', '\r');

    public (int Start, int Length) Selection
    {
        get
        {
            var selection = Doc.Selection;
            int a = Math.Min(selection.StartPosition, _text.Length);
            int b = Math.Min(selection.EndPosition, _text.Length);
            return (a, b - a);
        }
    }

    public void Select(int start, int length)
    {
        start = Math.Clamp(start, 0, _text.Length);
        Doc.Selection.SetRange(start, Math.Min(start + length, _text.Length));
        _expectedCaret = -1;
    }

    public void RevealSelection() => Doc.Selection.ScrollIntoView(PointOptions.None);

    /// <summary>Replaces the document (open, reload). Clears history; caret and scroll are the caller's choice.</summary>
    public void SetDocument(string text)
    {
        _text = text;
        Doc.SetText(TextSetOptions.None, ToControl(text));
        _contentDirty = false;
        _undo.Clear();
        _applied = new byte[text.Length];
        Array.Fill(_applied, StyleBits.Unknown);
        ApplyBaseFormatting();
        _lineIndex = null;
        StartTokenize();
        TextEdited?.Invoke();
    }

    /// <summary>Picks up a change made by the control (typing, IME commit, delete, cut, drag…).</summary>
    private void SyncFromControl(bool typing = true)
    {
        string current = ReadControlText();
        if (current == _text) return;

        int caret = Math.Min(Doc.Selection.EndPosition, current.Length);
        var (offset, removed, inserted) = TextDiff.Minimal(_text, current, caret);
        var edit = new TextEdit(offset, _text.Substring(offset, removed), current.Substring(offset, inserted),
            offset + removed, 0, caret, 0);
        _undo.Push(edit, typing, _clock.ElapsedMilliseconds);
        _expectedCaret = caret;
        Commit(current, offset, removed, inserted);
    }

    private void OnControlTextChanged()
    {
        if (!_contentDirty || _composing) return;
        _contentDirty = false;
        SyncFromControl();
    }

    private void OnCompositionEnded()
    {
        _composing = false;
        // The committed text arrives around this event; read it once the control has settled.
        Box.DispatcherQueue.TryEnqueue(() =>
        {
            if (_composing) return;
            _contentDirty = false;
            _undo.BreakCoalescing();
            SyncFromControl(typing: false);
            SchedulePump();
        });
    }

    private void OnSelectionChanged()
    {
        if (_composing) return;
        if (_expectedCaret >= 0 && Doc.Selection.EndPosition != _expectedCaret) _undo.BreakCoalescing();
    }

    private void Commit(string updated, int offset, int removed, int inserted)
    {
        SpliceApplied(offset, removed, inserted);
        _text = updated;
        _lineIndex = null;
        _highlightTimer.Stop();
        _highlightTimer.Start();
        TextEdited?.Invoke();
    }

    private void SpliceApplied(int offset, int removed, int inserted)
    {
        int newLength = _applied.Length - removed + inserted;
        var next = newLength == _applied.Length ? _applied : new byte[newLength];
        if (!ReferenceEquals(next, _applied))
        {
            Array.Copy(_applied, 0, next, 0, offset);
            Array.Copy(_applied, offset + removed, next, offset + inserted, _applied.Length - offset - removed);
            _applied = next;
        }
        Array.Fill(_applied, StyleBits.Unknown, offset, inserted);
    }

    /// <summary>Writes one replaced range into the control and the model; does not touch history.</summary>
    private void WriteRange(int offset, int removed, string inserted, int selStart, int selLength)
    {
        string updated = string.Concat(_text.AsSpan(0, offset), inserted, _text.AsSpan(offset + removed));
        Doc.BatchDisplayUpdates();
        Doc.GetRange(offset, offset + removed).SetText(TextSetOptions.None, ToControl(inserted));
        Doc.Selection.SetRange(selStart, selStart + selLength);
        Doc.ApplyDisplayUpdates();
        _contentDirty = false;
        _expectedCaret = selStart + selLength;
        Commit(updated, offset, removed, inserted.Length);
    }

    /// <summary>Runs a Markdown command as one undo step. Returns false when the command declined.</summary>
    public bool Run(Func<EditState, EditState?> command)
    {
        if (_composing) return false;
        SyncFromControl();
        var (start, length) = Selection;
        var before = new EditState(_text, start, length);
        var after = command(before);
        if (after is null) return false;
        Apply(before, after);
        return true;
    }

    private void Apply(EditState before, EditState after)
    {
        if (after.Text == before.Text)
        {
            Select(after.SelStart, after.SelLength);
            return;
        }
        var (offset, removed, inserted) = TextDiff.Minimal(before.Text, after.Text);
        string insertedText = after.Text.Substring(offset, inserted);
        _undo.Push(new TextEdit(offset, before.Text.Substring(offset, removed), insertedText,
            before.SelStart, before.SelLength, after.SelStart, after.SelLength), typing: false, _clock.ElapsedMilliseconds);
        WriteRange(offset, removed, insertedText, after.SelStart, after.SelLength);
    }

    public void ReplaceRange(int start, int length, string replacement)
    {
        if (_composing) return;
        SyncFromControl();
        var after = new EditState(string.Concat(_text.AsSpan(0, start), replacement, _text.AsSpan(start + length)), start + replacement.Length, 0);
        Apply(new EditState(_text, start, length), after);
    }

    public void ReplaceText(string updated)
    {
        var (start, _) = Selection;
        if (updated != _text) Apply(new EditState(_text, start, 0), new EditState(updated, Math.Min(start, updated.Length), 0));
    }

    public void Undo()
    {
        if (_composing) return;
        SyncFromControl();
        if (_undo.Undo() is { } e)
        {
            WriteRange(e.Offset, e.Inserted.Length, e.Removed, e.CaretBefore, e.SelLenBefore);
            RevealSelection();
        }
    }

    public void Redo()
    {
        if (_composing) return;
        SyncFromControl();
        if (_undo.Redo() is { } e)
        {
            WriteRange(e.Offset, e.Removed.Length, e.Inserted, e.SelStartAfter, e.SelLenAfter);
            RevealSelection();
        }
    }

    public void Cut()
    {
        _undo.BreakCoalescing();
        Doc.Selection.Cut();
        _contentDirty = false;
        SyncFromControl(typing: false);
    }

    public void CopySelection() => Doc.Selection.Copy();

    public void SelectAll()
    {
        Select(0, _text.Length);
        Box.Focus(FocusState.Programmatic);
    }

    /// <summary>Plain text only: whatever rich formats the clipboard holds are ignored.</summary>
    public async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text) || _composing) return;
        string text;
        try { text = await content.GetTextAsync(); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return; }
        text = TextCodec.NormalizeLineEndings(text).Text;
        var (start, length) = Selection;
        ReplaceRange(start, length, text);
        RevealSelection();
    }

    // ---- Keys --------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_composing) return;
        bool shift = IsDown(VirtualKey.Shift), ctrl = IsDown(VirtualKey.Control), alt = IsDown(VirtualKey.Menu);
        if (ctrl || alt) return;

        switch (e.Key)
        {
            case VirtualKey.Enter:
                // Shift+Enter would insert a vertical tab: a Markdown editor has only one kind of line break.
                if (shift || !Run(MarkdownCommands.Enter))
                {
                    var (start, length) = Selection;
                    ReplaceRange(start, length, "\n");
                    RevealSelection();
                }
                else
                {
                    RevealSelection();
                }
                e.Handled = true;
                break;
            case VirtualKey.Tab:
                Run(shift ? MarkdownCommands.BackTab : MarkdownCommands.Tab);
                e.Handled = true;
                break;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDown(VirtualKey.Control)) return;
        WheelZoom?.Invoke(e.GetCurrentPoint(Box).Properties.MouseWheelDelta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    // ---- Appearance --------------------------------------------------------------------------

    public void SetTheme(bool dark)
    {
        _darkTheme = dark;
        bool highContrast = new AccessibilitySettings().HighContrast;
        Color text = dark ? Color.FromArgb(255, 0xE6, 0xE6, 0xE6) : Color.FromArgb(255, 0x1F, 0x1F, 0x1F);
        // Mac system colours tuned for at least 4.5:1 on the editor background (HANDOFF §6.4).
        _palette = highContrast
            ? Enumerable.Repeat(new UISettings().GetColorValue(UIColorType.Foreground), 8).ToArray()
            : dark
                ? [text, Hex(0xA8A8AD), Hex(0x4DA3FF), Hex(0xFF9F0A), Hex(0xC77DF5), Hex(0x40C8E0), Hex(0x9A98F7), Hex(0xFF6482)]
                : [text, Hex(0x6E6E73), Hex(0x0060CC), Hex(0xA85400), Hex(0x8E2FC0), Hex(0x0B7285), Hex(0x4B49C9), Hex(0xC8143C)];
        ResetStyles();
    }

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public void SetZoom(double zoom)
    {
        _zoom = zoom;
        Box.FontSize = BaseFontSizeDip * zoom;
        ApplyBaseFormatting();
        ResetStyles();
    }

    private float FontSizePoints => (float)(BaseFontSizeDip * _zoom * 0.75);

    /// <summary>Whole-document font and line spacing. Exact spacing keeps CJK fallback fonts from shifting lines.</summary>
    private void ApplyBaseFormatting()
    {
        var all = Doc.GetRange(0, TextConstants.MaxUnitCount);
        all.CharacterFormat.Name = _fontFace;
        all.CharacterFormat.Size = FontSizePoints;
        all.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, FontSizePoints * (float)LineHeightRatio);

        var defaults = Doc.GetDefaultCharacterFormat();
        defaults.Name = _fontFace;
        defaults.Size = FontSizePoints;
        Doc.SetDefaultCharacterFormat(defaults);

        var paragraph = Doc.GetDefaultParagraphFormat();
        paragraph.SetLineSpacing(LineSpacingRule.Exactly, FontSizePoints * (float)LineHeightRatio);
        Doc.SetDefaultParagraphFormat(paragraph);
    }

    private void ResetStyles()
    {
        Array.Clear(_templates);
        if (_applied.Length > 0) Array.Fill(_applied, StyleBits.Unknown);
        SchedulePump();
    }

    private ITextCharacterFormat Template(byte style)
    {
        if (_templates[style] is { } cached) return cached;
        var format = Doc.GetRange(0, 0).CharacterFormat.GetClone();
        format.Name = _fontFace;
        format.Size = FontSizePoints;
        format.ForegroundColor = _palette[style & StyleBits.RoleMask];
        format.Bold = (style & StyleBits.Bold) != 0 ? FormatEffect.On : FormatEffect.Off;
        format.Italic = (style & StyleBits.Italic) != 0 ? FormatEffect.On : FormatEffect.Off;
        format.Strikethrough = (style & StyleBits.Strike) != 0 ? FormatEffect.On : FormatEffect.Off;
        if ((style & StyleBits.CodeBackground) != 0)
            format.BackgroundColor = _darkTheme ? Color.FromArgb(255, 0x45, 0x2A, 0x31) : Color.FromArgb(255, 0xFB, 0xE7, 0xEC);
        return _templates[style] = format;
    }

    // ---- Highlighting ------------------------------------------------------------------------

    private void StartTokenize()
    {
        _tokenizeCancel?.Cancel();
        var cancel = _tokenizeCancel = new CancellationTokenSource();
        string snapshot = _text;
        _ = Task.Run(() => MarkdownTokenizer.Tokenize(snapshot, cancel.Token), cancel.Token).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && ReferenceEquals(snapshot, _text))
            {
                _desired = task.Result;
                _desiredFor = snapshot;
                SchedulePump();
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>True once everything on screen shows its final colours.</summary>
    public bool StylesSettled
    {
        get
        {
            if (!ReferenceEquals(_desiredFor, _text)) return false;
            var (start, end) = ViewportChars();
            return _applied.AsSpan(start, end - start).SequenceEqual(_desired.AsSpan(start, end - start));
        }
    }

    public void SchedulePump()
    {
        if (_pumpQueued) return;
        _pumpQueued = true;
        Box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Pump);
    }

    /// <summary>Applies the differences in and around the viewport, in time-boxed slices.</summary>
    private void Pump()
    {
        _pumpQueued = false;
        if (_composing || !ReferenceEquals(_desiredFor, _text) || _desired.Length != _applied.Length) return;

        var watch = Stopwatch.StartNew();
        var (start, end) = ViewportChars();
        bool batching = false;
        int i = start;
        while (i < end)
        {
            if (_applied[i] == _desired[i]) { i++; continue; }
            if (!batching) { Doc.BatchDisplayUpdates(); batching = true; }

            byte style = _desired[i];
            int j = i + 1;
            while (j < end && _desired[j] == style && _applied[j] != style) j++;
            Doc.GetRange(i, j).CharacterFormat = Template(style);
            Array.Fill(_applied, style, i, j - i);
            i = j;
            if (watch.ElapsedMilliseconds >= PumpBudgetMs)
            {
                SchedulePump();
                break;
            }
        }
        if (batching) Doc.ApplyDisplayUpdates();
        LastPumpMs = watch.Elapsed.TotalMilliseconds;
    }

    private (int Start, int End) ViewportChars()
    {
        if (_text.Length == 0) return (0, 0);
        int top = 0, bottom = _text.Length;
        if (Scroller is { ViewportHeight: > 0 } scroller && Box.ActualWidth > 0)
        {
            top = OffsetAtViewportY(0);
            bottom = OffsetAtViewportY(scroller.ViewportHeight);
        }
        return (Math.Max(0, top - ViewportMargin), Math.Min(_text.Length, bottom + ViewportMargin));
    }

    /// <summary>The control lays out all its content inside the ScrollViewer: its points are content coordinates.</summary>
    private int OffsetAtViewportY(double y)
    {
        var point = new Point(Box.Padding.Left + 1, y + (Scroller?.VerticalOffset ?? 0));
        var range = Doc.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
        return Math.Clamp(range.StartPosition, 0, _text.Length);
    }

    // ---- Scroll sync (mirrors SplitEditorView.swift "Scroll sync") -----------------------------

    private LineIndex? _lineIndex;
    private LineIndex Lines => _lineIndex ??= new LineIndex(_text);
    private long _suppressScrollUntil;

    public bool ScrollEventsSuppressed => _clock.ElapsedMilliseconds < _suppressScrollUntil;

    private void AttachScroller()
    {
        if (Scroller is not null) return;
        Scroller = FindScrollViewer(Box);
        if (Scroller is null) return;
        Scroller.ViewChanged += (_, _) =>
        {
            SchedulePump();
            Scrolled?.Invoke();
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Y of a character within the scrolled content, independent of the current scroll offset.</summary>
    private double ContentY(int offset)
    {
        var range = Doc.GetRange(offset, offset);
        range.GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
        return rect.Y;
    }

    /// <summary>Top and height of one logical line, soft-wrapped rows included.</summary>
    private (double Top, double Height) LineRect(int line)
    {
        double top = ContentY(Math.Min(Lines.StartOf(line), _text.Length));
        double next = line + 1 < Lines.LineCount
            ? ContentY(Lines.StartOf(line + 1))
            : top + BaseFontSizeDip * _zoom * LineHeightRatio;
        return (top, Math.Max(next - top, 1));
    }

    /// <summary>Fractional source line at the top of the viewport.</summary>
    public (double Line, bool AtBottom) Position()
    {
        if (Scroller is not { } scroller || _text.Length == 0) return (0, false);
        double max = scroller.ScrollableHeight;
        double y = scroller.VerticalOffset;
        bool atBottom = max > 1 && y >= max - 1;

        int line = Lines.LineOf(OffsetAtViewportY(0));
        var (top, height) = LineRect(line);
        double fraction = Math.Clamp((y - top) / height, 0, 1);
        return (line + fraction, atBottom);
    }

    public void ScrollToLine(double line, bool bottom)
    {
        if (Scroller is not { } scroller || !double.IsFinite(line)) return;
        double max = scroller.ScrollableHeight;
        double y;
        if (bottom)
        {
            y = max;
        }
        else
        {
            int whole = Math.Clamp((int)Math.Floor(line), 0, Lines.LineCount - 1);
            var (top, height) = LineRect(whole);
            y = top + (line - Math.Floor(line)) * height;
        }
        y = Math.Clamp(y, 0, max);
        if (Math.Abs(scroller.VerticalOffset - y) < 0.5) return;
        _suppressScrollUntil = _clock.ElapsedMilliseconds + 150;
        scroller.ChangeView(null, y, null, disableAnimation: true);
    }

    public void ScrollToTop()
    {
        Select(0, 0);
        Scroller?.ChangeView(null, 0, null, disableAnimation: true);
    }

    public (double Y, double Extent, double Viewport) ScrollMetrics() =>
        Scroller is { } s ? (s.VerticalOffset, s.ExtentHeight, s.ViewportHeight) : (0, 0, 0);

    public void Focus() => Box.Focus(FocusState.Programmatic);

    /// <summary>Simulates typing through the control, for the smoke test.</summary>
    public void TypeForTest(string text) => Doc.Selection.TypeText(text);

    public void FlushForTest()
    {
        _contentDirty = false;
        SyncFromControl();
    }

    public string ControlTextForTest() => ReadControlText();

    public double ContentYForTest(int line) => ContentY(Lines.StartOf(line));

    public bool IsBold(int offset) => Doc.GetRange(offset, offset + 1).CharacterFormat.Bold == FormatEffect.On;

    public Color ForegroundAt(int offset) => Doc.GetRange(offset, offset + 1).CharacterFormat.ForegroundColor;
}

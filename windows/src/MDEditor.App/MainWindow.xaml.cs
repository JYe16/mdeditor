using System.Runtime.InteropServices;
using MDEditor.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace MDEditor;

/// <summary>One document window (no tabs in v1, like the Mac app).</summary>
public sealed partial class MainWindow : Window
{
    private const long MaxFileBytes = 50L * 1024 * 1024;
    private static int _openWindowCount;

    internal MarkdownEditor Editor { get; }
    internal PreviewHost Preview { get; }

    private readonly nint _hwnd;
    private readonly Splitter _splitter = new();
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private string? _path;
    private bool _readOnly;
    private TextFileEncoding _encoding = TextFileEncoding.Utf8;
    private LineEnding _lineEnding = LineEnding.Lf;
    private string _savedText = "";
    private FileSystemWatcher? _watcher;
    private (DateTime Stamp, long Length) _knownFile;
    private bool _dialogOpen;
    private bool _closeConfirmed;
    private MenuFlyoutItem? _autoSaveItem;
    private readonly List<MenuFlyoutItem> _formatItems = new();

    private readonly SUBCLASSPROC _subclass;

    private List<int> _findHits = new();
    private int _findIndex = -1;
    private string _viewMode = "split";

    private static AppSettings Settings => App.Instance!.Settings;
    private bool ShowsEditor => _viewMode != "preview";
    private bool ShowsPreview => _viewMode != "editor";

    internal bool IsDirty => Editor.Text != _savedText;
    internal bool IsPristine => _path is null && !IsDirty;
    internal bool IsShowing(string path) => _path is not null && string.Equals(Path.GetFullPath(path), _path, StringComparison.OrdinalIgnoreCase);
    internal string ViewMode => _viewMode;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Editor = new MarkdownEditor();
        Grid.SetColumn(Editor.Box, 0);
        EditorPane.Children.Insert(0, Editor.Box);
        SplitterHost.Children.Add(_splitter);
        _splitter.Dragged += OnSplitterDragged;

        Preview = new PreviewHost(Web, () => Editor.Text, () => Editor.Position(), () => _path is null ? null : Path.GetDirectoryName(_path), () => ShowsPreview);

        ConfigureWindow();
        BuildMenu();
        LocalizeStaticText();

        Editor.TextEdited += OnTextEdited;
        Editor.Scrolled += OnEditorScrolled;
        Editor.WheelZoom += delta => SetZoom(Settings.Zoom + delta);

        Preview.ScrollRequested += (line, bottom) => { if (ShowsEditor) Editor.ScrollToLine(line, bottom); };
        Preview.LinkClicked += OpenLink;
        Preview.KeyForwarded += OnPreviewKey;
        Preview.FilesDropped += files => { foreach (string f in files) App.Instance!.OpenFile(f, this); };
        Preview.RuntimeMissing += ShowWebViewMissing;

        // Without this, WinUI gives initial focus to the first tab stop: the menu bar.
        Editor.Box.Loaded += (_, _) => { if (ShowsEditor) Editor.Focus(); };

        Root.PreviewKeyDown += OnRootPreviewKeyDown;
        Root.DragOver += OnDragOver;
        Root.Drop += OnDrop;
        Root.ActualThemeChanged += (_, _) => ApplyTheme();

        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); if (Settings.AutoSave && _path is not null && IsDirty && !_readOnly) _ = SaveToAsync(_path); };
        _watchDebounce.Tick += (_, _) => { _watchDebounce.Stop(); _ = CheckExternalChangeAsync(); };

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        _subclass = SubclassProc;
        SetWindowSubclass(_hwnd, _subclass, 1, 0);

        _viewMode = Settings.ViewMode;
        _appliedZoom = Settings.Zoom;
        Editor.SetZoom(Settings.Zoom);
        ApplyMode(_viewMode, focusEditor: true);
        ApplyTheme();
        UpdateTitle();
        UpdateStatus();
        _ = Preview.InitializeAsync(Root.ActualTheme == ElementTheme.Dark, BackgroundColor(), Settings.Zoom);
    }

    // ---- Window chrome -----------------------------------------------------------------------

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "MDEditor.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            TitleIcon.Source = new BitmapImage(new Uri(icon));
        }
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();
        else
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(640 * scale);
            presenter.PreferredMinimumHeight = (int)(360 * scale);
        }

        int width = Settings.WindowWidth ?? (int)(1200 * scale);
        int height = Settings.WindowHeight ?? (int)(780 * scale);
        int stagger = 32 * _openWindowCount;
        if (Settings.WindowX is int x && Settings.WindowY is int y &&
            DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(x, y, width, height), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x + stagger, y + stagger, width, height));
        }
        else
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        if (Settings.WindowMaximized && _openWindowCount == 0 && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
        _openWindowCount++;
    }

    private void RememberBounds()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var settings = Settings;
        settings.WindowMaximized = maximized;
        if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            settings.WindowX = AppWindow.Position.X;
            settings.WindowY = AppWindow.Position.Y;
            settings.WindowWidth = AppWindow.Size.Width;
            settings.WindowHeight = AppWindow.Size.Height;
        }
    }

    private Color BackgroundColor() =>
        Root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Colors.White;

    private void ApplyTheme()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        Color background = BackgroundColor();
        Editor.Box.Background = new SolidColorBrush(background);
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            Editor.Box.Resources[key] = new SolidColorBrush(background);
        Editor.SetTheme(dark);
        Preview.SetTheme(dark, background);

        var titleBar = AppWindow.TitleBar;
        Color foreground = dark ? Colors.White : Colors.Black;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x20, 0, 0, 0);
    }

    private void UpdateTitle()
    {
        string name = _path is null ? L.T("Untitled") : Path.GetFileName(_path);
        string title = $"{(IsDirty ? "*" : "")}{name} — MDEditor";
        Title = title;
        TitleText.Text = title;
    }

    private void UpdateStatus()
    {
        var stats = TextStats.Of(Editor.Text);
        WordsText.Text = L.F("{0} words", stats.Words);
        CharsText.Text = L.F("{0} characters", stats.Characters);
        MinutesText.Text = L.F("{0} min read", stats.Minutes);
        MinutesText.Visibility = stats.Words > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void BringToFront()
    {
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        SetForegroundWindow(_hwnd);
    }

    // ---- Menus -------------------------------------------------------------------------------

    private MenuFlyoutItem Item(string text, Cmd command)
    {
        var item = new MenuFlyoutItem { Text = L.T(text) };
        if (Shortcuts.Display(command) is { } shortcut) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => Execute(command);
        return item;
    }

    private void BuildMenu()
    {
        static MenuBarItem Bar(string title, params MenuFlyoutItemBase[] items)
        {
            var bar = new MenuBarItem { Title = L.T(title) };
            foreach (var item in items) bar.Items.Add(item);
            return bar;
        }
        MenuFlyoutItemBase Separator() => new MenuFlyoutSeparator();

        var autoSave = new ToggleMenuFlyoutItem { Text = L.T("Auto Save"), IsChecked = Settings.AutoSave };
        autoSave.Click += (_, _) => Execute(Cmd.ToggleAutoSave);
        _autoSaveItem = autoSave;

        Menu.Items.Add(Bar("File",
            Item("New", Cmd.New), Item("Open…", Cmd.Open), Item("Save", Cmd.Save), Item("Save As…", Cmd.SaveAs), autoSave, Separator(),
            Item("Export as PDF…", Cmd.ExportPdf), Item("Print…", Cmd.Print), Separator(),
            Item("Close", Cmd.CloseWindow), Item("Exit", Cmd.Exit)));
        Menu.Items.Add(Bar("Edit",
            Item("Undo", Cmd.Undo), Item("Redo", Cmd.Redo), Separator(),
            Item("Cut", Cmd.Cut), Item("Copy", Cmd.Copy), Item("Paste", Cmd.Paste), Item("Select All", Cmd.SelectAll), Separator(),
            Item("Find…", Cmd.Find), Item("Replace…", Cmd.Replace), Item("Find Next", Cmd.FindNext), Item("Find Previous", Cmd.FindPrevious)));

        var format = new (string, Cmd)[]
        {
            ("Bold", Cmd.Bold), ("Italic", Cmd.Italic), ("Strikethrough", Cmd.Strike), ("Inline Code", Cmd.InlineCode), ("Link", Cmd.Link),
            ("Heading 1", Cmd.Heading1), ("Heading 2", Cmd.Heading2), ("Heading 3", Cmd.Heading3),
            ("Quote", Cmd.Quote), ("Bulleted List", Cmd.Bullet), ("Task List", Cmd.Task), ("Code Block", Cmd.CodeBlock),
        };
        var formatBar = new MenuBarItem { Title = L.T("Format") };
        for (int i = 0; i < format.Length; i++)
        {
            if (i is 5 or 8) formatBar.Items.Add(new MenuFlyoutSeparator());
            var item = Item(format[i].Item1, format[i].Item2);
            _formatItems.Add(item);
            formatBar.Items.Add(item);
        }
        Menu.Items.Add(formatBar);

        Menu.Items.Add(Bar("View",
            Item("Editor Only", Cmd.ViewEditor), Item("Split View", Cmd.ViewSplit), Item("Preview Only", Cmd.ViewPreview), Separator(),
            Item("Zoom In", Cmd.ZoomIn), Item("Zoom Out", Cmd.ZoomOut), Item("Actual Size", Cmd.ZoomReset)));
        Menu.Items.Add(Bar("Help", Item("About MDEditor", Cmd.About)));

        var context = new MenuFlyout();
        foreach (var (text, command) in new[] { ("Undo", Cmd.Undo), ("Redo", Cmd.Redo), ("Cut", Cmd.Cut), ("Copy", Cmd.Copy), ("Paste", Cmd.Paste), ("Select All", Cmd.SelectAll) })
            context.Items.Add(Item(text, command));
        Editor.Box.ContextFlyout = context;
    }

    private void LocalizeStaticText()
    {
        void Mode(ToggleButton button, string text)
        {
            ToolTipService.SetToolTip(button, L.T(text));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.T(text));
        }
        Mode(ModeEditor, "Editor Only");
        Mode(ModeSplit, "Split View");
        Mode(ModePreview, "Preview Only");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ModeButtons, L.T("Switch between editor, split view and preview"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Web, L.T("Preview"));

        FindBox.PlaceholderText = L.T("Find");
        ReplaceBox.PlaceholderText = L.T("Replace");
        ReplaceOneButton.Content = L.T("Replace");
        ReplaceAllButton.Content = L.T("Replace All");
        void Labeled(FrameworkElement element, string text)
        {
            ToolTipService.SetToolTip(element, L.T(text));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, L.T(text));
        }
        Labeled(FindBox, "Find");
        Labeled(ReplaceBox, "Replace");
        Labeled(MatchCaseButton, "Match case");
        Labeled(WholeWordButton, "Whole word");
        Labeled(FindPrevButton, "Previous");
        Labeled(FindNextButton, "Next");
        Labeled(FindCloseButton, "Close");
    }

    // ---- Commands ----------------------------------------------------------------------------

    internal void Execute(Cmd command)
    {
        switch (command)
        {
            case Cmd.New: App.Instance!.NewWindow(); break;
            case Cmd.Open: _ = OpenWithPickerAsync(); break;
            case Cmd.Save: _ = SaveAsync(); break;
            case Cmd.SaveAs: _ = SaveAsAsync(); break;
            case Cmd.ExportPdf: _ = ExportPdfAsync(); break;
            case Cmd.Print: _ = Preview.PrintAsync(); break;
            case Cmd.CloseWindow: RequestClose(); break;
            case Cmd.Exit: App.Instance!.RequestExit(); break;
            case Cmd.ToggleAutoSave:
                Settings.AutoSave = !Settings.AutoSave;
                if (_autoSaveItem is ToggleMenuFlyoutItem toggle) toggle.IsChecked = Settings.AutoSave;
                App.Instance!.SaveSettings();
                break;
            case Cmd.Undo: Editor.Undo(); break;
            case Cmd.Redo: Editor.Redo(); break;
            case Cmd.Cut: Editor.Cut(); break;
            case Cmd.Copy: Editor.CopySelection(); break;
            case Cmd.Paste: _ = Editor.PasteAsync(); break;
            case Cmd.SelectAll: Editor.SelectAll(); break;
            case Cmd.Find: ShowFind(replace: false); break;
            case Cmd.Replace: ShowFind(replace: true); break;
            case Cmd.FindNext: StepFind(1); break;
            case Cmd.FindPrevious: StepFind(-1); break;
            case Cmd.Bold: Format(MarkdownCommands.Bold); break;
            case Cmd.Italic: Format(MarkdownCommands.Italic); break;
            case Cmd.Strike: Format(MarkdownCommands.Strike); break;
            case Cmd.InlineCode: Format(MarkdownCommands.InlineCode); break;
            case Cmd.Link: Format(MarkdownCommands.Link); break;
            case Cmd.Heading1: Format(s => MarkdownCommands.Heading(s, 1)); break;
            case Cmd.Heading2: Format(s => MarkdownCommands.Heading(s, 2)); break;
            case Cmd.Heading3: Format(s => MarkdownCommands.Heading(s, 3)); break;
            case Cmd.Quote: Format(MarkdownCommands.Quote); break;
            case Cmd.Bullet: Format(MarkdownCommands.Bullet); break;
            case Cmd.Task: Format(MarkdownCommands.Task); break;
            case Cmd.CodeBlock: Format(MarkdownCommands.CodeBlock); break;
            case Cmd.ViewEditor: SetMode("editor"); break;
            case Cmd.ViewSplit: SetMode("split"); break;
            case Cmd.ViewPreview: SetMode("preview"); break;
            case Cmd.ZoomIn: SetZoom(Settings.Zoom + 0.1); break;
            case Cmd.ZoomOut: SetZoom(Settings.Zoom - 0.1); break;
            case Cmd.ZoomReset: SetZoom(1.0); break;
            case Cmd.About: _ = ShowAboutAsync(); break;
        }
    }

    private void Format(Func<EditState, EditState?> command)
    {
        if (!ShowsEditor) return;
        Editor.Run(command);
        Editor.RevealSelection();
        Editor.Focus();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private bool FocusInTextBox() =>
        FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox;

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift);
        if (IsDown(VirtualKey.Menu)) return;
        if (Editor.IsComposing) return;
        if (e.Key == VirtualKey.Escape && FindBar.Visibility == Visibility.Visible)
        {
            HideFind();
            e.Handled = true;
            return;
        }
        if (Shortcuts.Find(e.Key, ctrl, shift) is not { } command) return;
        // The find bar's own text boxes keep their standard editing keys.
        if (FocusInTextBox() && command is Cmd.Undo or Cmd.Redo or Cmd.Cut or Cmd.Copy or Cmd.Paste or Cmd.SelectAll) return;
        e.Handled = true;
        Execute(command);
    }

    private void OnPreviewKey(int code, bool ctrl, bool shift)
    {
        if (Shortcuts.Find((VirtualKey)code, ctrl, shift) is { } command && command is not (Cmd.Cut or Cmd.Paste))
            Execute(command);
    }

    // ---- Layout ------------------------------------------------------------------------------

    private void OnModeClick(object sender, RoutedEventArgs e) =>
        SetMode((string)((FrameworkElement)sender).Tag);

    private void SetMode(string mode)
    {
        Settings.ViewMode = mode;
        App.Instance!.BroadcastSettingsChanged();
    }

    private void SetZoom(double zoom)
    {
        Settings.Zoom = AppSettings.ClampZoom(zoom);
        App.Instance!.BroadcastSettingsChanged();
    }

    private double _appliedZoom = 1.0;

    internal void ApplySharedSettings()
    {
        if (_viewMode != Settings.ViewMode) ApplyMode(Settings.ViewMode, focusEditor: true);
        if (Math.Abs(_appliedZoom - Settings.Zoom) > 0.001)
        {
            _appliedZoom = Settings.Zoom;
            Editor.SetZoom(Settings.Zoom);
            Preview.SetZoom(Settings.Zoom);
        }
        if (_autoSaveItem is ToggleMenuFlyoutItem toggle) toggle.IsChecked = Settings.AutoSave;
    }

    private void ApplyMode(string mode, bool focusEditor)
    {
        bool previewWasVisible = ShowsPreview && PreviewPane.Visibility == Visibility.Visible;
        bool editorWasVisible = ShowsEditor && EditorPane.Visibility == Visibility.Visible;
        _viewMode = mode;

        EditorPane.Visibility = ShowsEditor ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = ShowsPreview ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = mode == "split" ? Visibility.Visible : Visibility.Collapsed;

        EditorColumn.MinWidth = mode == "preview" ? 0 : 240;
        PreviewColumn.MinWidth = mode == "editor" ? 0 : 240;
        EditorColumn.Width = mode == "preview" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PreviewColumn.Width = mode == "editor" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        ModeEditor.IsChecked = mode == "editor";
        ModeSplit.IsChecked = mode == "split";
        ModePreview.IsChecked = mode == "preview";
        foreach (var item in _formatItems) item.IsEnabled = ShowsEditor;

        if (ShowsPreview && !previewWasVisible) Preview.Invalidate();
        Preview.ScheduleRender();
        if (ShowsEditor && (!editorWasVisible || focusEditor))
            DispatcherQueue.TryEnqueue(Editor.Focus);
    }

    private void OnSplitterDragged(double delta)
    {
        double total = Panes.ActualWidth - _splitter.ActualWidth;
        double left = Math.Clamp(EditorPane.ActualWidth + delta, 240, Math.Max(240, total - 240));
        EditorColumn.Width = new GridLength(left, GridUnitType.Star);
        PreviewColumn.Width = new GridLength(total - left, GridUnitType.Star);
    }

    // ---- Editing events ----------------------------------------------------------------------

    private void OnTextEdited()
    {
        UpdateTitle();
        UpdateStatus();
        Preview.ScheduleRender();
        if (FindBar.Visibility == Visibility.Visible) RefreshFind(moveToNearest: false);
        if (Settings.AutoSave && _path is not null)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    private void OnEditorScrolled()
    {
        if (!Preview.IsReady || !ShowsPreview || Editor.ScrollEventsSuppressed) return;
        var (line, atBottom) = Editor.Position();
        Preview.SyncTo(line, atBottom);
    }

    // ---- Find and replace --------------------------------------------------------------------

    private void ShowFind(bool replace)
    {
        if (!ShowsEditor) SetMode("split");
        bool showRow = replace || ReplaceBox.Visibility == Visibility.Visible && FindBar.Visibility == Visibility.Visible;
        FindBar.Visibility = Visibility.Visible;
        ReplaceBox.Visibility = showRow ? Visibility.Visible : Visibility.Collapsed;
        ReplaceButtons.Visibility = showRow ? Visibility.Visible : Visibility.Collapsed;

        var (start, length) = Editor.Selection;
        if (length > 0 && !Editor.Text.AsSpan(start, length).Contains('\n'))
            FindBox.Text = Editor.Text.Substring(start, length);
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        RefreshFind(moveToNearest: false);
    }

    private void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void RefreshFind(bool moveToNearest)
    {
        _findHits = TextSearch.FindAll(Editor.Text, FindBox.Text, MatchCaseButton.IsChecked == true, WholeWordButton.IsChecked == true);
        var (start, _) = Editor.Selection;
        _findIndex = _findHits.Count == 0 ? -1 : Math.Max(0, _findHits.BinarySearch(start) is var i && i >= 0 ? i : Math.Min(~i, _findHits.Count - 1));
        if (moveToNearest && _findIndex >= 0) SelectHit();
        UpdateFindCount();
    }

    private void UpdateFindCount() =>
        FindCount.Text = FindBox.Text.Length == 0 ? "" : _findHits.Count == 0 ? L.T("No results") : L.F("{0} of {1}", _findIndex + 1, _findHits.Count);

    private void SelectHit()
    {
        Editor.Select(_findHits[_findIndex], FindBox.Text.Length);
        Editor.RevealSelection();
    }

    private void StepFind(int direction)
    {
        if (FindBox.Text.Length == 0)
        {
            ShowFind(replace: false);
            return;
        }
        RefreshFind(moveToNearest: false);
        if (_findHits.Count == 0) return;

        var (start, length) = Editor.Selection;
        bool onHit = _findIndex >= 0 && _findHits[_findIndex] == start && length == FindBox.Text.Length;
        if (onHit)
        {
            _findIndex = (_findIndex + direction + _findHits.Count) % _findHits.Count;
        }
        else if (direction < 0)
        {
            int before = _findHits.FindLastIndex(h => h < start);
            _findIndex = before >= 0 ? before : _findHits.Count - 1;
        }
        else
        {
            int after = _findHits.FindIndex(h => h >= start);
            _findIndex = after >= 0 ? after : 0;
        }
        SelectHit();
        UpdateFindCount();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => RefreshFind(moveToNearest: true);
    private void OnFindOptionClick(object sender, RoutedEventArgs e) => RefreshFind(moveToNearest: true);
    private void OnFindPrevClick(object sender, RoutedEventArgs e) => StepFind(-1);
    private void OnFindNextClick(object sender, RoutedEventArgs e) => StepFind(1);
    private void OnFindCloseClick(object sender, RoutedEventArgs e) => HideFind();

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        StepFind(IsDown(VirtualKey.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private void OnReplaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ReplaceCurrent();
        e.Handled = true;
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceCurrent();

    internal void ReplaceCurrent()
    {
        RefreshFind(moveToNearest: false);
        if (_findHits.Count == 0) return;
        var (start, length) = Editor.Selection;
        if (_findIndex >= 0 && _findHits[_findIndex] == start && length == FindBox.Text.Length)
        {
            Editor.ReplaceRange(start, length, ReplaceBox.Text);
            RefreshFind(moveToNearest: true);
        }
        else
        {
            StepFind(1);
        }
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var (text, _) = TextSearch.ReplaceAll(Editor.Text, FindBox.Text, ReplaceBox.Text, MatchCaseButton.IsChecked == true, WholeWordButton.IsChecked == true);
        Editor.ReplaceText(text);
        RefreshFind(moveToNearest: false);
    }

    internal void SetFindTextForTest(string find, string replace)
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Text = find;
        ReplaceBox.Text = replace;
        RefreshFind(moveToNearest: false);
    }

    internal int FindHitCountForTest => _findHits.Count;

    internal void SetThemeForTest(ElementTheme theme) => Root.RequestedTheme = theme;

    internal void SetZoomForTest(double zoom) => SetZoom(zoom);

    internal void ReplaceAllForTest() => OnReplaceAllClick(this, new RoutedEventArgs());

    // ---- Files -------------------------------------------------------------------------------

    private void InitPicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

    private async Task OpenWithPickerAsync()
    {
        var picker = new FileOpenPicker();
        InitPicker(picker);
        foreach (string extension in new[] { ".md", ".markdown", ".mdown", ".mkd", ".mdwn", ".txt", "*" })
            picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) App.Instance!.OpenFile(file.Path, this);
    }

    internal async Task LoadFileAsync(string path, bool isReload = false)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxFileBytes) throw new IOException(L.T("The file is larger than 50 MB."));
            byte[] data = await Task.Run(() => File.ReadAllBytes(fullPath));
            if (TextCodec.LooksBinary(data)) throw new IOException(L.T("This looks like a binary file, not text."));

            var decoded = TextCodec.Decode(data);
            var (selStart, selLength) = Editor.Selection;
            var (line, atBottom) = Editor.Position();

            _path = fullPath;
            _encoding = decoded.Encoding;
            _lineEnding = decoded.LineEnding;
            _savedText = decoded.Text;
            _readOnly = info.IsReadOnly;
            _knownFile = (info.LastWriteTimeUtc, info.Length);
            Editor.SetDocument(decoded.Text);

            if (isReload)
            {
                Editor.Select(selStart, selLength);
                Editor.ScrollToLine(line, atBottom);
            }
            else
            {
                DispatcherQueue.TryEnqueue(Editor.ScrollToTop);
                AddToRecents(fullPath);
            }
            WatchFile();
            Preview.ApplyBase();
            Preview.ScheduleRender();
            UpdateTitle();
            UpdateStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            await ShowMessageAsync(L.T("Could not open the file"), e.Message);
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (_path is null || _readOnly) return await SaveAsAsync();
        return await SaveToAsync(_path);
    }

    private async Task<bool> SaveAsAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = _path is null ? L.T("Untitled") : Path.GetFileNameWithoutExtension(_path) };
        InitPicker(picker);
        picker.FileTypeChoices.Add(L.T("Markdown"), new List<string> { ".md", ".markdown" });
        picker.FileTypeChoices.Add(L.T("Text"), new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        return file is not null && await SaveToAsync(file.Path);
    }

    internal async Task<bool> SaveToAsync(string path)
    {
        string text = Editor.Text;
        var encoding = _encoding == TextFileEncoding.Legacy ? TextFileEncoding.Utf8 : _encoding;
        byte[] data = TextCodec.Encode(text, encoding, _lineEnding);
        try
        {
            await Task.Run(() => SafeFile.WriteAtomic(path, data));
            var info = new FileInfo(path);
            bool renamed = !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
            _path = Path.GetFullPath(path);
            _encoding = encoding;
            _savedText = text;
            _readOnly = false;
            _knownFile = (info.LastWriteTimeUtc, info.Length);
            if (renamed)
            {
                WatchFile();
                Preview.ApplyBase();
                Preview.ScheduleRender();
            }
            AddToRecents(_path);
            UpdateTitle();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            await ShowMessageAsync(L.T("Could not save the file"), $"{e.Message}\n\n{L.T("Your text is still in this window. Try Save As to choose another location.")}");
            return false;
        }
    }

    private void WatchFile()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_path is null || Path.GetDirectoryName(_path) is not { } folder) return;
        try
        {
            _watcher = new FileSystemWatcher(folder, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return; // Some network and virtual folders have no change notifications.
        }

        void Changed(object? sender, FileSystemEventArgs args) => DispatcherQueue.TryEnqueue(() =>
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        });
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (s, a) => Changed(s, a);
    }

    private async Task CheckExternalChangeAsync()
    {
        if (_path is null || _dialogOpen) return;
        FileInfo info;
        try
        {
            info = new FileInfo(_path);
            if (!info.Exists || (info.LastWriteTimeUtc, info.Length) == _knownFile) return;
        }
        catch (IOException) { return; }

        if (!IsDirty)
        {
            await LoadFileAsync(_path, isReload: true);
            return;
        }

        _knownFile = (info.LastWriteTimeUtc, info.Length); // Ask once per change.
        var answer = await AskAsync(L.T("The file has changed on disk."),
            L.F("{0} was changed by another program. Reload it and lose your edits, or keep your version?", Path.GetFileName(_path)),
            L.T("Reload"), L.T("Keep my version"), null);
        if (answer == ContentDialogResult.Primary) await LoadFileAsync(_path, isReload: true);
    }

    private static void AddToRecents(string path) => SHAddToRecentDocs(SHARD_PATHW, path);

    // ---- Closing -----------------------------------------------------------------------------

    internal void RequestClose()
    {
        if (!_closeConfirmed && IsDirty)
            _ = ConfirmAndCloseAsync();
        else
            Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !IsDirty) return;
        args.Cancel = true;
        _ = ConfirmAndCloseAsync();
    }

    private async Task ConfirmAndCloseAsync()
    {
        if (_dialogOpen) return;
        BringToFront();
        string name = _path is null ? L.T("Untitled") : Path.GetFileName(_path);
        var answer = await AskAsync("MDEditor", L.F("Do you want to save changes to {0}?", name), L.T("Save"), L.T("Don't Save"), L.T("Cancel"));
        if (answer == ContentDialogResult.None) return;
        if (answer == ContentDialogResult.Primary && !await SaveAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RememberBounds();
        _openWindowCount = Math.Max(0, _openWindowCount - 1);
        _watcher?.Dispose();
        RemoveWindowSubclass(_hwnd, _subclass, 1);
    }

    // ---- Dialogs, PDF, links, drag and drop ----------------------------------------------------

    /// <summary>A dialog needs a XamlRoot, which does not exist yet when a file fails to open at startup.</summary>
    private async Task WhenLoadedAsync()
    {
        if (Root.XamlRoot is not null && Root.IsLoaded) return;
        var loaded = new TaskCompletionSource();
        Root.Loaded += (_, _) => loaded.TrySetResult();
        await loaded.Task;
    }

    private async Task<ContentDialogResult> AskAsync(string title, string body, string primary, string? secondary, string? close)
    {
        await WhenLoadedAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = close ?? "",
            DefaultButton = ContentDialogButton.Primary,
        };
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private async Task ShowMessageAsync(string title, string body)
    {
        if (!_dialogOpen) await AskAsync(title, body, L.T("OK"), null, null);
    }

    private async Task ShowAboutAsync()
    {
        string version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = L.F("Version {0}", version) });
        panel.Children.Add(new TextBlock { Text = "© 2026 JYe16" });
        panel.Children.Add(new TextBlock { Text = L.T("Released under the MIT License.") });
        panel.Children.Add(new TextBlock { Text = L.T("Third-party notices are in THIRD_PARTY_NOTICES.md next to the application."), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new HyperlinkButton { Content = L.T("Source code on GitHub"), NavigateUri = new Uri("https://github.com/JYe16/mdeditor"), Padding = new Thickness(0) });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L.T("About MDEditor"), Content = panel, CloseButtonText = L.T("OK") };
        _dialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private async Task ExportPdfAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = _path is null ? L.T("Untitled") : Path.GetFileNameWithoutExtension(_path) };
        InitPicker(picker);
        picker.FileTypeChoices.Add(L.T("PDF"), new List<string> { ".pdf" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (!await Preview.ExportPdfAsync(file.Path)) await ShowMessageAsync(L.T("Could not export the PDF"), file.Path);
    }

    private void ShowWebViewMissing()
    {
        Web.Visibility = Visibility.Collapsed;
        WebViewMissing.Visibility = Visibility.Visible;
        WebViewMissingTitle.Text = L.T("The preview component (WebView2) is missing.");
        WebViewMissingBody.Text = L.T("Install the WebView2 Runtime to see the preview. Editing still works.");
        WebViewMissingLink.Content = L.T("Download WebView2 Runtime");
    }

    private void OpenLink(string href)
    {
        var action = LinkResolver.Resolve(href, _path is null ? null : Path.GetDirectoryName(_path), p => File.Exists(p) || Directory.Exists(p));
        switch (action.Kind)
        {
            case LinkActionKind.OpenUri:
                _ = Launcher.LaunchUriAsync(new Uri(action.Target!));
                break;
            case LinkActionKind.OpenInEditor:
                App.Instance!.OpenFile(action.Target!, this);
                break;
            case LinkActionKind.OpenWithDefaultApp:
                ShellExecute(action.Target!);
                break;
            case LinkActionKind.Reveal:
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{action.Target}\"");
                break;
            case LinkActionKind.Beep:
                MessageBeep(0xFFFFFFFF);
                break;
        }
    }

    private static void ShellExecute(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        foreach (var item in await e.DataView.GetStorageItemsAsync())
            if (item is Windows.Storage.StorageFile file) App.Instance!.OpenFile(file.Path, this);
    }

    // ---- Win32 -------------------------------------------------------------------------------

    private const uint SHARD_PATHW = 3;
    private const uint WM_QUERYENDSESSION = 0x0011;

    private delegate nint SUBCLASSPROC(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    private nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_QUERYENDSESSION && IsDirty)
        {
            _ = ConfirmAndCloseAsync(); // Block shutdown / sign-out until the user has answered.
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SUBCLASSPROC proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHAddToRecentDocs(uint flags, string path);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);
}

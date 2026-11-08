using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI;

namespace MDEditor;

/// <summary>
/// Hosts the shared preview page (Resources/preview) in WebView2. State machine from HANDOFF §5.4, mirroring
/// SplitEditorView.Coordinator. The page itself is unmodified: an injected shim stands in for WKWebView's bridge.
/// </summary>
internal sealed class PreviewHost
{
    // Runs before app.js. Besides the bridge it forwards shortcuts (WebView2 would swallow them), file drops
    // and adds Windows fonts and print margins to the Mac-first stylesheet.
    private const string Shim = """
        (() => {
          const send = (name, body) => window.chrome.webview.postMessage({ name, body });
          window.webkit = { messageHandlers: new Proxy({}, {
            get: (_, name) => ({ postMessage: (body) => send(String(name), body) }),
          }) };
          window.chrome.webview.addEventListener('message', (e) => {
            const m = e.data;
            if (m.cmd === 'render') { window.render(m.text, m.line, m.bottom); if (m.ack) send('rendered', m.ack); }
            else if (m.cmd === 'setBase') window.setBase(m.url);
            else if (m.cmd === 'sync') window.syncToLine(m.line, m.bottom);
          });
          window.addEventListener('keydown', (e) => {
            const nativeCopy = e.ctrlKey && !e.shiftKey && !e.altKey && (e.keyCode === 67 || e.keyCode === 65);
            if (nativeCopy || e.altKey || !(e.ctrlKey || (e.keyCode >= 112 && e.keyCode <= 123))) return;
            e.preventDefault();
            send('key', { code: e.keyCode, ctrl: e.ctrlKey, shift: e.shiftKey });
          }, true);
          window.addEventListener('drop', (e) => {
            const files = e.dataTransfer && e.dataTransfer.files;
            if (files && files.length) window.chrome.webview.postMessageWithAdditionalObjects('drop', files);
          });
          window.addEventListener('DOMContentLoaded', () => {
            const style = document.createElement('style');
            style.textContent = `
              .markdown-body { font-family: -apple-system, "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", Arial, sans-serif; }
              .markdown-body code, .markdown-body pre, .markdown-body kbd, .markdown-body samp {
                font-family: "Cascadia Mono", Consolas, "Microsoft YaHei", monospace; }
              @page { margin: 0.55in; }`;
            document.head.appendChild(style);
          });
        })();
        """;

    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private readonly WebView2 _web;
    private readonly Func<string> _text;
    private readonly Func<(double Line, bool AtBottom)> _position;
    private readonly Func<string?> _documentFolder;
    private readonly Func<bool> _previewVisible;
    private readonly DispatcherQueueTimer _debounce;
    private readonly string _indexPath = Path.Combine(AppContext.BaseDirectory, "preview", "index.html");
    private Uri? _indexUri;

    private bool _pageReady;
    private string? _appliedBase;
    private string? _lastRendered;
    private int _ackCounter;
    private readonly Dictionary<string, TaskCompletionSource> _pendingAcks = new();

    public event Action<double, bool>? ScrollRequested;
    public event Action<string>? LinkClicked;
    public event Action<int, bool, bool>? KeyForwarded;
    public event Action<string[]>? FilesDropped;
    public event Action? RuntimeMissing;

    public bool IsReady => _pageReady;
    public CoreWebView2? Core => _web.CoreWebView2;

    public PreviewHost(WebView2 web, Func<string> text, Func<(double, bool)> position, Func<string?> documentFolder, Func<bool> previewVisible)
    {
        _web = web;
        _text = text;
        _position = position;
        _documentFolder = documentFolder;
        _previewVisible = previewVisible;
        _debounce = web.DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(40);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RenderNow();
    }

    public async Task InitializeAsync(bool dark, Color background, double zoom)
    {
        try
        {
            if (string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                RuntimeMissing?.Invoke();
                return;
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            RuntimeMissing?.Invoke();
            return;
        }

        SetBackground(background);
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, _) =>
        {
            _pageReady = false;
            _lastRendered = null;
            LoadPage();
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
        SetTheme(dark, background); // Calls made before this point had no CoreWebView2 to reach.
        SetZoom(zoom);
        LoadPage();
    }

    private void LoadPage()
    {
        if (!File.Exists(_indexPath))
        {
            _web.NavigateToString($"<body style='font:14px Segoe UI;padding:2em'>{L.T("Preview resources are missing.")}</body>");
            return;
        }
        _indexUri = new Uri(_indexPath);
        _web.CoreWebView2?.Navigate(_indexUri.AbsoluteUri);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        bool allowed = _indexUri is not null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri == _indexUri;
        if (!allowed) e.Cancel = true;
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch (JsonException) { return; }

        if (root.ValueKind == JsonValueKind.String)
        {
            if (root.GetString() == "drop" && e.AdditionalObjects is { Count: > 0 } objects)
                FilesDropped?.Invoke(objects.OfType<CoreWebView2File>().Select(f => f.Path).ToArray());
            return;
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var name)) return;
        root.TryGetProperty("body", out var body);
        switch (name.GetString())
        {
            case "ready":
                _pageReady = true;
                _appliedBase = null;
                _lastRendered = null;
                ApplyBase();
                ScheduleRender();
                break;
            case "scroll" when body.ValueKind == JsonValueKind.Object && body.TryGetProperty("line", out var line) && line.TryGetDouble(out double l):
                ScrollRequested?.Invoke(l, body.TryGetProperty("bottom", out var b) && b.ValueKind == JsonValueKind.True);
                break;
            case "link" when body.ValueKind == JsonValueKind.String:
                LinkClicked?.Invoke(body.GetString()!);
                break;
            case "key" when body.ValueKind == JsonValueKind.Object:
                KeyForwarded?.Invoke(body.GetProperty("code").GetInt32(), body.GetProperty("ctrl").GetBoolean(), body.GetProperty("shift").GetBoolean());
                break;
            case "rendered" when body.ValueKind == JsonValueKind.String:
                if (_pendingAcks.Remove(body.GetString()!, out var pending)) pending.TrySetResult();
                break;
        }
    }

    private void Post(Action<Utf8JsonWriter> write)
    {
        if (_web.CoreWebView2 is null) return;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder }))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }
        _web.CoreWebView2.PostWebMessageAsJson(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    public void ApplyBase()
    {
        if (!_pageReady || _documentFolder() is not { } folder) return;
        string url = new Uri(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar).AbsoluteUri;
        if (url == _appliedBase) return;
        _appliedBase = url;
        _lastRendered = null;
        Post(j => { j.WriteString("cmd", "setBase"); j.WriteString("url", url); });
    }

    public void ScheduleRender()
    {
        if (!_pageReady || !_previewVisible() || _lastRendered == _text()) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Forces a fresh render after the preview becomes visible again.</summary>
    public void Invalidate() => _lastRendered = null;

    private void RenderNow(string? ack = null)
    {
        if (!_pageReady) return;
        string text = _text();
        _lastRendered = text;
        var (line, atBottom) = _position();
        Post(j =>
        {
            j.WriteString("cmd", "render");
            j.WriteString("text", text);
            j.WriteNumber("line", line);
            j.WriteBoolean("bottom", atBottom);
            if (ack is not null) j.WriteString("ack", ack);
        });
    }

    /// <summary>Renders the current text and waits until the page has finished (print and PDF, in any view mode).</summary>
    public async Task RenderAndWaitAsync()
    {
        if (!_pageReady) return;
        _debounce.Stop();
        string ack = (++_ackCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource();
        _pendingAcks[ack] = pending;
        RenderNow(ack);
        await Task.WhenAny(pending.Task, Task.Delay(10_000));
        _pendingAcks.Remove(ack);
    }

    public void SyncTo(double line, bool bottom)
    {
        if (!_pageReady || !_previewVisible() || !double.IsFinite(line)) return;
        Post(j =>
        {
            j.WriteString("cmd", "sync");
            j.WriteNumber("line", line);
            j.WriteBoolean("bottom", bottom);
        });
    }

    public void SetZoom(double zoom)
    {
        ZoomFactor = zoom;
        _ = ApplyZoomAsync();
    }

    private double ZoomFactor { get; set; } = 1;

    // CoreWebView2 has no ZoomFactor in the WinUI wrapper; CSS zoom on the root gives the same reflow.
    private async Task ApplyZoomAsync()
    {
        if (_web.CoreWebView2 is null) return;
        string factor = ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _web.CoreWebView2.ExecuteScriptAsync($"document.documentElement.style.zoom = '{factor}'");
        if (_zoomScriptId is not null) _web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_zoomScriptId);
        _zoomScriptId = await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            $"document.addEventListener('DOMContentLoaded',()=>document.documentElement.style.zoom='{factor}')");
    }

    private string? _zoomScriptId;

    public void SetTheme(bool dark, Color background)
    {
        SetBackground(background);
        if (_web.CoreWebView2 is { } core)
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
    }

    private void SetBackground(Color background) =>
        _web.DefaultBackgroundColor = background;

    public async Task<string> EvaluateAsync(string script) =>
        _web.CoreWebView2 is { } core ? await core.ExecuteScriptAsync(script) : "null";

    public async Task PrintAsync()
    {
        if (_web.CoreWebView2 is not { } core) return;
        await RenderAndWaitAsync();
        core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
    }

    public async Task<bool> ExportPdfAsync(string path)
    {
        if (_web.CoreWebView2 is not { } core) return false;
        await RenderAndWaitAsync();
        var settings = core.Environment.CreatePrintSettings();
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.55;
        settings.ShouldPrintBackgrounds = false;
        return await core.PrintToPdfAsync(path, settings);
    }
}

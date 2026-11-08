using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MDEditor.Core;

namespace MDEditor;

/// <summary>
/// `MDEditor.exe --smoke-test[=report.txt]`: drives one real window (real RichEditBox and WebView2) through the
/// checks that cannot run headless, and exits 0/1. Input goes through the controls' APIs, never synthetic key
/// presses, so it cannot type into another application.
/// </summary>
internal sealed class SmokeTest(MainWindow window, string? output)
{
    private readonly StringBuilder _report = new();
    private int _failures;
    private MarkdownEditor Editor => window.Editor;

    private void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        _report.AppendLine(CultureInfo.InvariantCulture, $"{(ok ? "ok  " : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private void Note(string text) => _report.AppendLine(CultureInfo.InvariantCulture, $"info  {text}");

    private static Task Wait(int ms) => Task.Delay(ms);

    private async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs) await Wait(15);
    }

    private async Task<string?> JsString(string script)
    {
        string json = await window.Preview.EvaluateAsync(script);
        try { return JsonDocument.Parse(json).RootElement.ValueKind == JsonValueKind.String ? JsonDocument.Parse(json).RootElement.GetString() : json; }
        catch (JsonException) { return json; }
    }

    public async Task RunAsync()
    {
        try
        {
            await RunChecksAsync();
        }
        catch (Exception e)
        {
            Check("no exception escaped", false, e.ToString());
        }
        _report.AppendLine(CultureInfo.InvariantCulture, $"{(_failures == 0 ? "PASS" : "FAILED")}  failures={_failures}");
        string target = output ?? Path.Combine(Path.GetTempPath(), "mdeditor-smoke.txt");
        File.WriteAllText(target, _report.ToString(), Encoding.UTF8);
        App.Finish(_failures == 0 ? 0 : 1);
    }

    private async Task RunChecksAsync()
    {
        await WaitFor(() => window.Preview.IsReady && Editor.Scroller is not null);
        Check("preview page reported ready", window.Preview.IsReady);
        Check("editor scroller found", Editor.Scroller is not null);

        EditorBoundary();
        await Undo();
        await Highlighting();
        await Geometry();
        await PreviewRendering();
        await FindReplace();
        await Pdf();
        await FileRoundTrip();
    }

    private void EditorBoundary()
    {
        const string text = "line one\n\n中文😀 end\n";
        Editor.SetDocument(text);
        Check("control text round-trips with \\n", Editor.ControlTextForTest() == text, Escape(Editor.ControlTextForTest()));

        Editor.Select(text.IndexOf("end", StringComparison.Ordinal), 3);
        var (start, length) = Editor.Selection;
        Check("G5 offsets after CJK and emoji are UTF-16", start == text.IndexOf("end", StringComparison.Ordinal) && length == 3, $"{start},{length}");

        Editor.Run(MarkdownCommands.Bold);
        Check("G5 bold after emoji", Editor.ControlTextForTest() == "line one\n\n中文😀 **end**\n", Escape(Editor.ControlTextForTest()));

        Editor.SetDocument("");
        Check("empty document", Editor.ControlTextForTest() == "" && Editor.Text == "");
        Editor.SetDocument("a\n");
        Check("trailing newline preserved", Editor.ControlTextForTest() == "a\n", Escape(Editor.ControlTextForTest()));
    }

    private async Task Undo()
    {
        Editor.SetDocument("# Title\n\nplain\n");
        await WaitFor(() => Editor.StylesSettled);
        Editor.Select(15, 0);
        Editor.TypeForTest("x");
        await Wait(120);
        Editor.FlushForTest();
        Check("typing reaches the model", Editor.Text == "# Title\n\nplain\nx", Escape(Editor.Text));
        await WaitFor(() => Editor.StylesSettled);
        await Wait(100);

        Editor.Undo();
        Check("G2 one undo removes the typed text, not a colour change", Editor.ControlTextForTest() == "# Title\n\nplain\n", Escape(Editor.ControlTextForTest()));
        Editor.Redo();
        Check("redo restores it", Editor.ControlTextForTest() == "# Title\n\nplain\nx", Escape(Editor.ControlTextForTest()));
        Editor.Undo();

        Editor.Select(0, 7);
        Editor.Run(MarkdownCommands.Bold);
        await WaitFor(() => Editor.StylesSettled);
        Editor.Undo();
        Check("G2 format command is one undo step", Editor.ControlTextForTest() == "# Title\n\nplain\n", Escape(Editor.ControlTextForTest()));
        Check("no undo steps left after colouring", !Editor.CanUndo);
    }

    private async Task Highlighting()
    {
        Editor.SetDocument("# Title\n\n**bold** `code`\n");
        await WaitFor(() => Editor.StylesSettled);
        Check("heading text is bold", Editor.IsBold(3));
        Check("plain text is not bold", !Editor.IsBold(16 + 2));
        Check("bold span applied", Editor.IsBold(11));
        Check("heading colour differs from body", !Editor.ForegroundAt(3).Equals(Editor.ForegroundAt(19)), $"{Editor.ForegroundAt(3)} vs {Editor.ForegroundAt(19)}");

        var doc = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            doc.Append(CultureInfo.InvariantCulture, $"- item **{i}** with `code` and [a link](url) plus text\n");
        string big = doc.ToString()[..Math.Min(doc.Length, 299_000)];

        var load = Stopwatch.StartNew();
        Editor.SetDocument(big);
        Note($"G3 load of {big.Length} chars: {load.ElapsedMilliseconds} ms");
        await WaitFor(() => Editor.StylesSettled);
        Note($"G3 load + first colouring: {load.ElapsedMilliseconds} ms");
        Check("G3 first colouring settles", Editor.StylesSettled);

        var slices = new List<double>();
        var keystrokes = new List<double>();
        Editor.Select(2500, 0);
        for (int i = 0; i < 40; i++)
        {
            var key = Stopwatch.StartNew();
            Editor.TypeForTest("z");
            Editor.FlushForTest();
            keystrokes.Add(key.Elapsed.TotalMilliseconds);
            await Wait(50);
            slices.Add(Editor.LastPumpMs);
        }
        keystrokes.Sort();
        Note($"G3 keystroke UI work on 5000 lines: median {keystrokes[20]:F2} ms, p95 {keystrokes[38]:F2} ms");
        Note($"G3 longest colouring slice: {slices.Max():F2} ms");
        Check("G3 keystroke median under 16 ms", keystrokes[20] < 16, $"{keystrokes[20]:F2} ms");
    }

    private async Task Geometry()
    {
        var doc = new StringBuilder();
        for (int i = 0; i < 400; i++)
            doc.Append(i % 10 == 3 ? new string('w', 40) + " " + string.Concat(Enumerable.Repeat("long wrapped text ", 40)) + "\n" : $"line {i}\n");
        Editor.SetDocument(doc.ToString());
        await WaitFor(() => Editor.StylesSettled);
        await Wait(300);

        double previous = -1;
        bool monotonic = true;
        for (int line = 0; line < 400; line += 7)
        {
            double y = Editor.ContentYForTest(line);
            if (y <= previous) monotonic = false;
            previous = y;
        }
        var (_, extent, viewport) = Editor.ScrollMetrics();
        Note($"G4 extent {extent:F0}, viewport {viewport:F0}, y(0) {Editor.ContentYForTest(0):F1}, y(1) {Editor.ContentYForTest(1):F1}, y(399) {Editor.ContentYForTest(399):F0}");
        Check("G4 line positions increase", monotonic);

        foreach (double target in new[] { 37.0, 120.5, 301.25 })
        {
            Editor.ScrollToLine(target, bottom: false);
            await Wait(250);
            var (line, _) = Editor.Position();
            Check($"G4 scroll to line {target} reads back", Math.Abs(line - target) < 0.6, $"read {line:F2}, offset {Editor.ScrollMetrics().Y:F0}");
        }

        double before = Editor.ContentYForTest(100);
        Editor.ScrollToLine(250, false);
        await Wait(250);
        double after = Editor.ContentYForTest(100);
        Check("G4 content Y ignores scroll offset", Math.Abs(before - after) < 1.5, $"{before:F1} vs {after:F1}");

        Editor.ScrollToLine(0, bottom: true);
        await Wait(250);
        Check("G4 bottom reports atBottom", Editor.Position().AtBottom);
    }

    private async Task PreviewRendering()
    {
        string source = """
            ---
            title: test
            ---
            # Heading One

            | a | b |
            | - | - |
            | 1 | 2 |

            - [x] done

            > [!NOTE]
            > hello

            ```python
            print("hi")
            ```

            $E=mc^2$
            """;
        Editor.SetDocument(source);
        await window.Preview.RenderAndWaitAsync();
        Check("H1 rendered", await JsString("document.querySelector('h1')?.textContent") == "Heading One");
        Check("table rendered", await JsString("String(document.querySelectorAll('table td').length)") == "2");
        Check("task list rendered", await JsString("String(document.querySelectorAll('.task-list-item').length)") == "1");
        Check("alert rendered", await JsString("String(document.querySelectorAll('.alert-note').length)") == "1");
        Check("front matter rendered", await JsString("String(document.querySelectorAll('.frontmatter').length)") == "1");
        Check("code highlighted", await JsString("String(document.querySelectorAll('pre .hljs-built_in, pre .hljs-string').length > 0)") == "true");
        Check("KaTeX rendered", await JsString("String(document.querySelectorAll('.katex').length)") == "1");
        Check("Windows fonts injected", (await JsString("getComputedStyle(document.querySelector('.markdown-body')).fontFamily"))?.Contains("Segoe UI", StringComparison.Ordinal) == true);
        await ZoomAndTheme();
    }

    private async Task ZoomAndTheme()
    {
        var doc = new StringBuilder();
        for (int i = 0; i < 60; i++) doc.Append(CultureInfo.InvariantCulture, $"## Section {i}\n\nSome body text for section {i}.\n\n");
        Editor.SetDocument(doc.ToString());
        await window.Preview.RenderAndWaitAsync();

        // Line 80 is the "## Section 20" heading; the preview must put it at the top at any zoom.
        const string syncAndMeasure = "window.syncToLine(80, false); String(Math.round(document.querySelectorAll('h2')[20].getBoundingClientRect().top))";
        Check("preview line sync at 100%", Math.Abs(double.Parse(await JsString(syncAndMeasure) ?? "999", CultureInfo.InvariantCulture)) <= 2);

        window.SetZoomForTest(1.5);
        await Wait(300);
        string? top = await JsString(syncAndMeasure);
        Check("preview line sync at 150%", Math.Abs(double.Parse(top ?? "999", CultureInfo.InvariantCulture)) <= 2, top ?? "");
        Check("preview zoom applied", await JsString("document.documentElement.style.zoom") == "1.5");
        var (_, extent150, _) = Editor.ScrollMetrics();
        window.SetZoomForTest(1.0);
        await Wait(300);
        var (_, extent100, _) = Editor.ScrollMetrics();
        Check("editor zoom scales geometry", extent150 > extent100 * 1.3, $"{extent150:F0} vs {extent100:F0}");

        window.SetThemeForTest(Microsoft.UI.Xaml.ElementTheme.Dark);
        await Wait(300);
        Check("dark theme reaches the preview", await JsString("String(matchMedia('(prefers-color-scheme: dark)').matches)") == "true");
        window.SetThemeForTest(Microsoft.UI.Xaml.ElementTheme.Light);
        await Wait(300);
        Check("light theme reaches the preview", await JsString("String(matchMedia('(prefers-color-scheme: dark)').matches)") == "false");

        string huge = string.Concat(Enumerable.Repeat("# not coloured above the limit\n", 40_000));
        var load = Stopwatch.StartNew();
        Editor.SetDocument(huge);
        Note($"1.2 MB document load: {load.ElapsedMilliseconds} ms");
        Check("over-limit document loads intact", Editor.ControlTextForTest() == huge);
        await WaitFor(() => Editor.StylesSettled);
        Check("over-limit document has no heading colour", !Editor.IsBold(3));
    }

    private async Task FindReplace()
    {
        Editor.SetDocument("foo bar foo\n");
        window.SetFindTextForTest("foo", "x");
        Check("find counts hits", window.FindHitCountForTest == 2, window.FindHitCountForTest.ToString(CultureInfo.InvariantCulture));
        window.ReplaceAllForTest();
        await Wait(50);
        Check("replace all", Editor.ControlTextForTest() == "x bar x\n", Escape(Editor.ControlTextForTest()));
        Editor.Undo();
        Check("replace all is one undo step", Editor.ControlTextForTest() == "foo bar foo\n" && !Editor.CanUndo, Escape(Editor.ControlTextForTest()));
    }

    private async Task Pdf()
    {
        Editor.SetDocument("# PDF heading\n\nbody\n");
        window.Execute(Cmd.ViewEditor);
        await Wait(200);
        string pdf = Path.Combine(Path.GetTempPath(), $"mdeditor-smoke-{Environment.ProcessId}.pdf");
        bool ok = await window.Preview.ExportPdfAsync(pdf);
        long size = File.Exists(pdf) ? new FileInfo(pdf).Length : 0;
        Check("PDF export works with the preview hidden", ok && size > 1000, $"{size} bytes");
        if (File.Exists(pdf)) File.Delete(pdf);
        window.Execute(Cmd.ViewSplit);
    }

    private async Task FileRoundTrip()
    {
        string dir = Directory.CreateTempSubdirectory("mdeditor-smoke").FullName;
        string path = Path.Combine(dir, "a.md");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', 13, 10, (byte)'b', 13, 10]);
        await window.LoadFileAsync(path);
        Check("CRLF + BOM file loads as \\n", Editor.Text == "a\nb\n" && !window.IsDirty, Escape(Editor.Text));
        Editor.Select(1, 0);
        Editor.Run(MarkdownCommands.Bold);
        Check("dirty after edit", window.IsDirty);
        await window.SaveToAsync(path);
        byte[] saved = File.ReadAllBytes(path);
        Check("BOM and CRLF preserved on save", saved.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'*', (byte)'*', (byte)'*', (byte)'*', 13, 10, (byte)'b', 13, 10 }), Convert.ToHexString(saved));
        Check("clean after save", !window.IsDirty);

        File.WriteAllText(path, "changed outside\n");
        await WaitFor(() => Editor.Text == "changed outside\n", 4000);
        Check("clean document reloads silently", Editor.Text == "changed outside\n", Escape(Editor.Text));
        Directory.Delete(dir, recursive: true);
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}

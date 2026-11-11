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
        await JumpToSource();
        await JumpSampleDocument();
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

    // ---- Double-click in the preview jumps to its source (HANDOFF-v1.1 §1) ------------------------

    private async Task JumpToSource()
    {
        var doc = new StringBuilder("# Jump test\n\nFirst paragraph with [a link](#jump-test) and mkstart.\n\n");
        for (int i = 0; i < 80; i++)
            doc.Append(CultureInfo.InvariantCulture, $"## Section {i}\n\nParagraph for section {i} with marker mk{i}x and more words.\n\n");
        doc.Append("```text\nalpha\nbeta\ngamma\ndelta\n```\n\nThe end.\n");
        string text = doc.ToString();

        window.SetZoomForTest(1.0);
        window.Execute(Cmd.ViewSplit);
        await Wait(300);
        Editor.SetDocument(text);
        await window.Preview.RenderAndWaitAsync();
        await WaitFor(() => Editor.StylesSettled);
        await Wait(300);

        // A. Target far from the editor's view. Real mouse input through DevTools, so the browser makes the word
        //    selection itself, exactly as with a user double-click.
        const string far = "mk60x";
        int farAt = text.IndexOf(far, StringComparison.Ordinal);
        await ScrollPreviewTo(far);
        await Wait(400);                        // the editor follows the preview...
        Editor.ScrollToLine(0, bottom: false);  // ...then is parked at the top, where the target is off screen
        await Wait(400);
        double editorBefore = Editor.ScrollMetrics().Y, previewBefore = await PreviewScrollY();
        window.FocusPreviewForTest();           // the state after a real click: keyboard focus is inside WebView2
        await Wait(100);
        Check("jump: precondition, the preview owns keyboard focus", window.PreviewHasFocusForTest, window.FocusedElementForTest?.GetType().Name ?? "null");
        await DoubleClickWord(far);
        await WaitFor(() => Editor.Selection.Length > 0, 3000);
        await Wait(400);                        // time for any echo to reach the preview
        Check("jump: double-click selects the word's source", Editor.Selection == (farAt, far.Length), $"{Editor.Selection} vs ({farAt}, {far.Length})");
        var (offsetA, extentA, viewportA) = Editor.ScrollMetrics();
        double lineY = Editor.ContentYForTest(text[..farAt].Count(c => c == '\n'));
        Check("jump: off-screen line scrolls the editor", offsetA > editorBefore + 200, $"{editorBefore:F0} -> {offsetA:F0}");
        Check("jump: the line ends up on screen", lineY >= offsetA && lineY <= offsetA + viewportA, $"line y {lineY:F0}, viewport {offsetA:F0}..{offsetA + viewportA:F0}, extent {extentA:F0}");
        Check("jump: preview did not move", Math.Abs(await PreviewScrollY() - previewBefore) < 1, $"{previewBefore:F1} -> {await PreviewScrollY():F1}");
        Check("jump: keyboard focus moves from the preview to the editor", ReferenceEquals(window.FocusedElementForTest, Editor.Box),
            window.FocusedElementForTest?.GetType().Name ?? "null");

        // B. Target already on screen: select it, do not scroll.
        const string near = "mk61x";
        int nearAt = text.IndexOf(near, StringComparison.Ordinal);
        await ScrollPreviewTo(near);            // a programmatic scroll the editor must not follow back into view changes
        await Wait(400);
        double steady = Editor.ScrollMetrics().Y;
        await DoubleClickWord(near);
        await WaitFor(() => Editor.Selection.Start == nearAt, 3000);
        await Wait(300);
        Check("jump: second word is selected", Editor.Selection == (nearAt, near.Length), $"{Editor.Selection} vs ({nearAt}, {near.Length})");
        Check("jump: on-screen line does not scroll", Math.Abs(Editor.ScrollMetrics().Y - steady) < 0.5,
            $"{steady:F1} -> {Editor.ScrollMetrics().Y:F1}");

        // C. At 150 % the preview uses CSS zoom: real mouse coordinates and element rectangles must still agree,
        //    because the fallback path (no word match) turns the pointer height into a source line.
        window.SetZoomForTest(1.5);
        await Wait(500);
        await ScrollPreviewTo("delta");
        await Wait(300);
        await JsString("document.addEventListener('mousedown', (e) => { const r = e.target.closest('pre').getBoundingClientRect(); window.__probe = JSON.stringify({ cy: e.clientY, cx: e.clientX, top: r.top, bottom: r.bottom }); }, true); 'ok'");
        string? box = await JsString("(() => { const r = document.querySelector('#content pre').getBoundingClientRect(); return JSON.stringify({ x: r.left + r.width / 2, y: r.bottom - 6 }); })()");
        using (var target = JsonDocument.Parse(box ?? "{}"))
            await DispatchMouse("mousePressed", target.RootElement.GetProperty("x").GetDouble(), target.RootElement.GetProperty("y").GetDouble(), 1);
        string? probe = await JsString("window.__probe || ''");
        if (string.IsNullOrEmpty(probe))
        {
            Check("jump@150%: mouse coordinates match element rectangles", false, "no mousedown reached the page");
        }
        else
        {
            using var p = JsonDocument.Parse(probe);
            double cy = p.RootElement.GetProperty("cy").GetDouble(), top = p.RootElement.GetProperty("top").GetDouble(), bottom = p.RootElement.GetProperty("bottom").GetDouble();
            using var t = JsonDocument.Parse(box!);
            double sent = t.RootElement.GetProperty("y").GetDouble();
            Check("jump@150%: mouse coordinates match element rectangles", Math.Abs(cy - sent) < 1.5 && cy >= top && cy <= bottom,
                $"sent y {sent:F1}, clientY {cy:F1}, block {top:F1}..{bottom:F1}");
        }
        await DispatchMouse("mouseReleased", 0, 0, 1);

        const string code = "delta";
        int codeAt = text.IndexOf(code, StringComparison.Ordinal);
        await DoubleClickWord(code);
        await WaitFor(() => Editor.Selection.Start == codeAt, 3000);
        await Wait(300);
        Check("jump@150%: word in a code block", Editor.Selection == (codeAt, code.Length), $"{Editor.Selection} vs ({codeAt}, {code.Length})");

        // The fallback: nothing selected, so only the pointer height in the block counts. Near the bottom of the fenced
        // block the caret must land on one of its lines, as a caret (no selection) at the start of the line.
        await JsString("getSelection().removeAllRanges(); const pre = document.querySelector('#content pre'); const r = pre.getBoundingClientRect(); pre.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: r.left + 20, clientY: r.top + r.height * 0.9 })); 'ok'");
        int fenceAt = text.IndexOf("```text", StringComparison.Ordinal);
        int fenceEnd = text.IndexOf("```\n\nThe end.", StringComparison.Ordinal);
        await WaitFor(() => Editor.Selection.Length == 0 && Editor.Selection.Start != codeAt, 3000);
        var (caret, caretLength) = Editor.Selection;
        Check("jump@150%: fallback places a caret inside the block", caretLength == 0 && caret >= fenceAt && caret <= fenceEnd
            && (caret == 0 || text[caret - 1] == '\n'), $"caret {caret}, block {fenceAt}..{fenceEnd}");

        // D. Preview-only: double-click must keep meaning "select this word". Nothing may change in the editor.
        window.SetZoomForTest(1.0);
        window.Execute(Cmd.ViewPreview);
        await Wait(500);
        Editor.Select(3, 0);
        double hiddenOffset = Editor.ScrollMetrics().Y;
        const string only = "mk10x";
        await ScrollPreviewTo(only);
        await Wait(300);
        double previewOnly = await PreviewScrollY();
        await DoubleClickWord(only);
        await Wait(600);
        Check("jump: preview-only mode leaves the editor alone", Editor.Selection == (3, 0) && Math.Abs(Editor.ScrollMetrics().Y - hiddenOffset) < 0.5, $"{Editor.Selection}");
        Check("jump: preview keeps its own word selection", await JsString("getSelection().toString().trim()") == only);
        Check("jump: preview-only does not scroll the preview", Math.Abs(await PreviewScrollY() - previewOnly) < 1);
        window.Execute(Cmd.ViewSplit);
        await Wait(500);

        // E. A double-click on a link does not jump (the first click already opened it).
        Editor.Select(5, 0);
        double beforeLink = Editor.ScrollMetrics().Y;
        await JsString("const a = document.querySelector('#content a'); getSelection().selectAllChildren(a); a.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, detail: 2 })); 'ok'");
        await Wait(400);
        Check("jump: double-click on a link does not move the editor", Editor.Selection == (5, 0) && Math.Abs(Editor.ScrollMetrics().Y - beforeLink) < 0.5, $"{Editor.Selection}");

        window.SetZoomForTest(1.0);
    }

    /// <summary>
    /// HANDOFF-v1.1 §4 "by hand" list, run with real double-clicks on examples/sample.md at 100 % and 150 %, light and
    /// dark: a word in the Swift block, a Chinese word, a table cell, a heading, a quote far from the editor's view and
    /// the inline formula. Skipped (with a note) when the repository's examples folder is not next to the build.
    /// </summary>
    private async Task JumpSampleDocument()
    {
        string? sample = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && sample is null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "examples", "sample.md");
            if (File.Exists(candidate)) sample = candidate;
        }
        if (sample is null)
        {
            Note("examples/sample.md not found next to the build: sample round trip skipped");
            return;
        }

        string md = File.ReadAllText(sample, Encoding.UTF8).Replace("\r\n", "\n");
        window.Execute(Cmd.ViewSplit);
        foreach (var (zoom, dark) in new[] { (1.0, false), (1.5, false), (1.0, true), (1.5, true) })
        {
            string tag = $"{zoom * 100:F0}%-{(dark ? "dark" : "light")}";
            window.SetThemeForTest(dark ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light);
            window.SetZoomForTest(zoom);
            Editor.SetDocument(md);
            await window.Preview.RenderAndWaitAsync();
            await WaitFor(() => Editor.StylesSettled);
            await Wait(500);

            await CheckJump(tag, md, "嵌套一层", "  - 嵌套一层");
            await CheckJump(tag, md, "按源码行对齐", "| 滚动同步");
            await CheckJump(tag, md, "数学公式", "## 数学公式");
            await CheckJump(tag, md, ".katex", "行内公式", css: true, expectText: "E = mc^2");
            await CheckJump(tag, md, "好的工具应该隐形", "> 好的工具应该隐形");
            await CheckJump(tag, md, "Greeting", "struct Greeting", expectText: "Greeting");

            if (output is not null)
                await window.CaptureForTestAsync($"{output}.jump-{tag}.png");
        }
        window.SetThemeForTest(Microsoft.UI.Xaml.ElementTheme.Light);
        window.SetZoomForTest(1.0);
        Editor.SetDocument(md);
        await window.Preview.RenderAndWaitAsync();
        await Wait(500);

        // An observation, not a check: the page scrolls to an in-page #anchor on the first click, so the second click of a
        // double-click lands on whatever moved under the pointer. Recorded so the behaviour is known rather than guessed.
        const string anchor = "跳到顶部";
        await ScrollPreviewTo(anchor);
        await Wait(300);
        Editor.Select(0, 0);
        double anchorBefore = await PreviewScrollY();
        await DoubleClickWord(anchor);
        await Wait(700);
        Note($"double-click on an in-page #anchor link: preview scrollY {anchorBefore:F0} -> {await PreviewScrollY():F0}, editor selection {Editor.Selection}");
        await Wait(300);
    }

    /// <summary>
    /// Real double-click on <paramref name="target"/> with the editor parked at the top. The editor must select the same
    /// text the browser selected (or <paramref name="expectText"/>), on the line that contains <paramref name="lineMarker"/>,
    /// with that line on screen, and the preview must not move.
    /// </summary>
    private async Task CheckJump(string tag, string md, string target, string lineMarker, bool css = false, string? expectText = null)
    {
        int expectedLine = md[..md.IndexOf(lineMarker, StringComparison.Ordinal)].Count(c => c == '\n');
        await ScrollPreviewTo(target, css);
        await Wait(300);                        // the editor follows the preview...
        Editor.ScrollToLine(0, bottom: false);  // ...then is parked at the top
        Editor.Select(0, 0);                    // sentinel: anything else is the jump
        await Wait(300);
        double previewBefore = await PreviewScrollY();
        window.FocusPreviewForTest();
        await DoubleClickWord(target, css);
        await WaitFor(() => Editor.Selection.Length > 0, 2000);
        await Wait(300);

        string? word = expectText ?? await JsString("getSelection().toString().trim()");
        var (start, length) = Editor.Selection;
        string selected = Editor.Text.Substring(start, length);
        int line = Editor.Text[..start].Count(c => c == '\n');
        var (offset, _, viewport) = Editor.ScrollMetrics();
        double y = Editor.ContentYForTest(line);
        bool visible = y >= offset - 1 && y <= offset + viewport;
        bool still = Math.Abs(await PreviewScrollY() - previewBefore) < 1;
        bool focused = ReferenceEquals(window.FocusedElementForTest, Editor.Box);
        Check($"sample {tag}: double-click '{target}'", selected == word && line == expectedLine && visible && still && focused,
            $"selected '{selected}' vs '{word}', line {line} vs {expectedLine}, on screen {visible}, preview still {still}, editor focused {focused}");
    }

    private async Task<double> PreviewScrollY() =>
        double.Parse(await JsString("String(window.scrollY)") ?? "NaN", CultureInfo.InvariantCulture);

    // `target` is a piece of rendered text, or a CSS selector when `css` is set (for things without text, like formulas).
    private Task<string?> ScrollPreviewTo(string target, bool css = false) => JsString(css
        ? $"document.querySelector('{target}').scrollIntoView({{ block: 'center' }}); 'ok'"
        : $$"""
          (() => {
            const w = document.createTreeWalker(document.getElementById('content'), NodeFilter.SHOW_TEXT);
            for (let n = w.nextNode(); n; n = w.nextNode())
              if (n.nodeValue.includes('{{target}}')) { n.parentElement.scrollIntoView({ block: 'center' }); return 'ok'; }
            return 'missing';
          })()
          """);

    private async Task DoubleClickWord(string target, bool css = false)
    {
        string? json = await JsString(css
            ? $"(() => {{ const b = document.querySelector('{target}').getBoundingClientRect(); return JSON.stringify({{ x: b.x + b.width / 2, y: b.y + b.height / 2 }}); }})()"
            : $$"""
              (() => {
                const w = document.createTreeWalker(document.getElementById('content'), NodeFilter.SHOW_TEXT);
                for (let n = w.nextNode(); n; n = w.nextNode()) {
                  const at = n.nodeValue.indexOf('{{target}}');
                  if (at < 0) continue;
                  const r = document.createRange();
                  r.setStart(n, at);
                  r.setEnd(n, at + {{target.Length}});
                  const b = r.getBoundingClientRect();
                  return JSON.stringify({ x: b.x + b.width / 2, y: b.y + b.height / 2 });
                }
                return JSON.stringify({ x: -1, y: -1 });
              })()
              """);
        using var point = JsonDocument.Parse(json ?? "{}");
        double x = point.RootElement.GetProperty("x").GetDouble(), y = point.RootElement.GetProperty("y").GetDouble();
        await DispatchMouse("mouseMoved", x, y, 0);
        await DispatchMouse("mousePressed", x, y, 1);
        await DispatchMouse("mouseReleased", x, y, 1);
        await DispatchMouse("mousePressed", x, y, 2);
        await DispatchMouse("mouseReleased", x, y, 2);
    }

    /// <summary>Trusted mouse input at a viewport position in the preview (DevTools protocol, no focus or cursor needed).</summary>
    private async Task DispatchMouse(string type, double x, double y, int clickCount)
    {
        string px = x.ToString("F2", CultureInfo.InvariantCulture), py = y.ToString("F2", CultureInfo.InvariantCulture);
        string buttons = type == "mousePressed" ? "1" : "0";
        await window.Preview.Core!.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
            $$"""{"type":"{{type}}","x":{{px}},"y":{{py}},"button":"left","buttons":{{buttons}},"clickCount":{{clickCount}}}""");
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

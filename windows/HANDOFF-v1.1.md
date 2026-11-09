# MDEditor for Windows: v1.1 brief (double-click to jump to source, new icon)

Audience: the agent that adds these two changes to the Windows app. It is a delta on top of [`HANDOFF.md`](HANDOFF.md):
the working rules there (§13, §14: don't push, tag or release; ask rather than guess) still apply, and so does
"don't touch `Sources/`, `Package.swift`, `scripts/*.sh`" (the macOS side is finished and was tested on a Mac).

The macOS app and the shared preview already contain everything below except the Windows-native half. Read these first:

| File | Why |
| --- | --- |
| `Resources/preview/app.js`, section "jump to source (double-click)" | The shared half. Already done; you should not need to edit it |
| `Sources/MDEditor/SplitEditorView.swift`, `jumpEditor(toLine:column:length:)` | The macOS reference for what the native half does |
| `windows/src/MDEditor.App/PreviewHost.cs`, `MainWindow.xaml.cs`, `MarkdownEditor.cs` | Where your code goes |

---

## 1. Feature: double-click rendered text, land on its source

**What the user sees.** In split view, double-clicking a word in the right-hand preview moves the left-hand editor to
that word's Markdown source: the word is selected, the editor gets keyboard focus, and the line is brought on screen if
it was not. If the exact word cannot be matched (an image, an alert title, a stale preview), the caret goes to the
start of the most likely source line instead.

**Rules (all of these are implemented on macOS; match them):**

1. **Editor hidden → do nothing.** In preview-only mode double-click must keep meaning "select this word" (people copy
   words out of the preview). The preview selection is never cleared by the app.
2. **Double-clicking a link does not jump.** The first click already opened the link. (`app.js` also ignores the second
   click of a double-click on a link, so it no longer opens twice. That fix is shared and already in.)
3. **Scroll only when needed.** If the line is within two line-heights of the top or bottom edge, or outside the
   viewport, centre it vertically. Otherwise do not scroll. Either way the scroll must **not** be echoed back to the
   preview (the existing `Editor.ScrollEventsSuppressed` window, 150 ms, exists for exactly this), because the preview
   is where the user is looking and it must not move under the pointer. The panes drift apart until the next manual
   scroll, which re-syncs them. That is intended.
4. **Select, don't just place the caret**, when `length > 0`. macOS also plays the Find "bounce" on the range; Windows
   has no equivalent, so the selection highlight is the cue. Do not invent an animation.
5. Clamp everything: line to `[0, LineCount-1]`, column to the line length (excluding the newline), length to what is
   left of the line. The preview can be up to ~40 ms behind the editor, so the numbers may be slightly stale.

## 2. The contract (already implemented in `app.js`)

The page posts through the same bridge as `scroll` and `link`. Your injected shim already turns
`window.webkit.messageHandlers.jump.postMessage(body)` into `chrome.webview.postMessage({ name: "jump", body })`, so
**no shim change is needed**. Today `OnWebMessage` ignores unknown names, so the current Windows build is unaffected.

```json
{ "name": "jump", "body": { "line": 22, "column": 7, "length": 8 } }
```

* `line`: 0-based source line, counted in `\n` as in `LineIndex` (`MDEditor.Core/TextModel.cs`), after front matter.
* `column`: UTF-16 offset within that line. .NET strings are UTF-16, so use it as is. No conversion.
* `length`: UTF-16 units to select; `0` means "place the caret".
* All three are JSON integers.

How `app.js` finds the position (so you can reason about failures): it takes the word the browser selected on
double-click (trimmed: Chromium may include a trailing space), counts how many times that word occurs in the block's
rendered text before the click, and returns the same-numbered occurrence in the block's source. Math (`data-tex`) is
matched by its TeX; everything else falls back to the pointer's height within the block. Expected misses: math returns
the whole formula, not the glyph you clicked; synthetic alert titles ("Note") land on the `> [!NOTE]` line.

## 3. What to build on Windows

1. **`PreviewHost`**: add `public event Action<int, int, int>? JumpRequested;` and a case in `OnWebMessage`:
   `case "jump" when body.ValueKind == JsonValueKind.Object` → read `line`, `column`, `length` with `TryGetInt32`
   (ignore the message if `line` is missing; default the others to 0) → raise the event.
2. **`MarkdownEditor`**: add one method, e.g. `JumpTo(int line, int column, int length)`, next to `ScrollToLine`.
   * Offsets: `Lines.StartOf(line)`, and the end of the line is `Lines.StartOf(line + 1) - 1` (or `_text.Length` for the
     last line). Offsets are in **model** coordinates (`\n`), which is what `Select(start, length)` takes; the control
     itself uses `\r`, so never feed it raw `Doc` positions.
   * Put the pure part (clamping and the line → `(start, length)` arithmetic) in `MDEditor.Core` as a small static
     function so it is unit-testable on any OS, e.g. `JumpTarget.Resolve(LineIndex lines, int textLength, int line, int column, int length)`.
   * Scrolling: reuse `LineRect(line)` and `Scroller.ChangeView(null, y, null, disableAnimation: true)` exactly like
     `ScrollToLine` does, and set `_suppressScrollUntil = _clock.ElapsedMilliseconds + 150` **before** `ChangeView`.
   * Then `Select(start, length)` and `Focus()`. Do not call `RevealSelection()`: it scrolls by its own rules and would
     fight rule 3.
3. **`MainWindow`**: `Preview.JumpRequested += (l, c, n) => { if (ShowsEditor) Editor.JumpTo(l, c, n); };`, beside the
   existing `ScrollRequested` line.

Windows-specific traps:

* The preview is hosted with CSS `zoom` on the root element. The word-matching path does not depend on geometry, but the
  fallback path reads `getBoundingClientRect()` and `clientY`. Check the fallback at zoom 150 % (double-click an image
  or an alert title) and tell the user if it is off, rather than patching `app.js` silently: that file is shared.
* A `RichEditBox` that is not focused hides its selection. `Focus()` must come before or right after `Select`, and
  you must verify the selection is visible, not just that `Editor.Selection` reads back correctly.
* Focus moves from WebView2 to the editor. Make sure the shortcut forwarding (`KeyForwarded`) still works afterwards.

## 4. Tests

* **Core unit tests** (`windows/tests/MDEditor.Core.Tests`): `JumpTarget.Resolve` with a line in the middle, the first
  and last line, a line past the end, a negative line, a column past the line end, a length running past the line end,
  an empty line, a document without a trailing newline, and text with CJK and emoji (offsets are UTF-16).
* **Smoke test** (`SmokeTest.cs`, same style as the existing preview checks): load a short document, wait for
  `rendered`, then drive a real round trip with `Preview.EvaluateAsync`: select a word in the DOM and dispatch a
  `dblclick` (the harness in Appendix A does this), and assert that `Editor.Selection` equals the word's offset and
  length and that `Editor.Scroller.VerticalOffset` changed only when the line was off screen. Also assert that in
  preview-only mode nothing changes, and that the preview's scroll position did not change.
* **By hand**, with `examples/sample.md` in split view: double-click `Greeting` in the Swift block (editor selects it on
  the `struct Greeting {` line), a Chinese word in the first paragraph, a table cell, a heading, the inline formula
  `E = mc^2` (selects the whole formula), a link (opens once, editor does not move), and a word in the preview that is
  far from the editor's current position (editor scrolls and centres, preview stays put). Repeat at 150 % zoom and in
  dark mode.

## 5. Icon

`Resources/AppIcon.png` and `AppIcon.icns` were redrawn (a squircle split into an ink half and a paper half with one
white-to-ink "M" across the seam; `scripts/make-icon.swift` draws it). The Windows icon is cut from that PNG, so:

1. Run `windows/scripts/make-ico.ps1` to regenerate `windows/src/MDEditor.App/Assets/MDEditor.ico`. The new artwork
   keeps the same 824 px body centred in the 1024 px canvas, so the existing crop (`100, 100, 824, 824`) is still right.
2. Look at 16, 20, 24 and 32 px renderings on a **light** and a **dark** taskbar, in the title bar, Alt+Tab, and the
   installer wizard (`installer/MDEditor.iss`). The right half of the icon is near-white; a dark hairline was added to its
   rim so the edge holds on a light taskbar, but this has only been looked at on macOS. If it is weak at 16 px, report it;
   do not edit `make-icon.swift` to fix it without asking, because the macOS icon must not change.
3. Check that `docs/images/` screenshots still show the right icon, if any of them include the title bar.

## 6. Docs and version

* `windows/README.md`: mention the feature next to scroll sync. `windows/REPORT.md`: add rows for the feature and the
  icon with honest Verified / Not verified status, as in the existing checklist.
* The root `README.md` and `README.zh-CN.md` already describe the feature as macOS-only for now. When Windows ships it,
  remove that qualifier in both files.
* macOS is moving to **1.1.0**. When this lands, bump `windows/Directory.Build.props` to `1.1.0` so the platforms match.
  Do not tag or release.

## 7. Definition of done

`dotnet test` green including the new Core tests; the smoke test passes on a real window; the by-hand list in §4 done at
100 % and 150 %; the icon checked at small sizes on both taskbar themes; `REPORT.md` updated. Report anything you could
not verify instead of leaving it out.

---

## Appendix A: the round-trip harness used to validate `app.js`

This was run in a desktop browser against `Resources/preview/index.html` on `examples/sample.md` plus a few
repeated-word cases (197 of 209 words matched exactly; the other 12 were 10 formula glyphs and the two synthetic alert
titles described in §2, so exactly those are expected to show up in `bad`). Paste it into
`Preview.EvaluateAsync` after rendering a document, with `md` set to that document. It stubs the bridge, simulates
selecting every word in the rendered text, dispatches `dblclick`, and checks that the reported `line:column:length`
slices the same word out of the source.

```js
(md) => {
  const lines = md.split('\n'), msgs = [];
  window.webkit = { messageHandlers: new Proxy({}, { get: (_, n) => ({ postMessage: (b) => msgs.push([n, b]) }) }) };
  window.render(md, 0, false);
  const seg = new Intl.Segmenter('zh', { granularity: 'word' });
  const walker = document.createTreeWalker(document.getElementById('content'), NodeFilter.SHOW_TEXT);
  let total = 0, exact = 0; const bad = [];
  for (let tn = walker.nextNode(); tn; tn = walker.nextNode()) {
    if (tn.parentElement.closest('a')) continue;
    for (const s of seg.segment(tn.nodeValue)) {
      if (!s.isWordLike) continue;
      total++;
      const r = document.createRange(); r.setStart(tn, s.index); r.setEnd(tn, s.index + s.segment.length);
      const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r);
      const rect = r.getBoundingClientRect();
      msgs.length = 0;
      tn.parentElement.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: rect.x + 1, clientY: rect.y + 1, detail: 2 }));
      const m = msgs.find((x) => x[0] === 'jump');
      if (!m || !m[1].length) { bad.push([s.segment, 'fallback or none']); continue; }
      const { line, column, length } = m[1];
      if (lines[line].substr(column, length) === s.segment) exact++; else bad.push([s.segment, lines[line]]);
    }
  }
  return { total, exact, bad };
}
```

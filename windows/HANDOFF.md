# MDEditor for Windows — implementation brief (WinUI 3)

Audience: the agent that will build the Windows version of MDEditor on a Windows machine.
You have no other context than this file and the repository, so everything you need to decide is either
here, in the reference code listed in §1, or in the "Ask the user" list in §14.

Paths are relative to the repository root. Authority order when sources disagree:
**this brief → Mac reference code/tests → your own judgement** (and write down any judgement call, §13).

---

## 0. TL;DR

* Build a **native WinUI 3 (Windows App SDK) desktop app in C#** that is feature-for-feature equivalent to the
  existing macOS app: **left = Markdown editor, right = live preview**, scroll-synced by source line.
* The **preview is shared**: reuse `Resources/preview/` (HTML/JS/CSS + vendored marked, DOMPurify, highlight.js,
  KaTeX) inside a **WebView2**. Do not fork or copy it. The Mac app hosts the same page in a WKWebView.
* The **editor is native** (not a web editor). See §7 for the control choice, a mandatory spike and acceptance gates.
* Everything in `windows/`. **Do not touch** `Sources/`, `Package.swift`, `Tests/`, `scripts/*.sh`, `Resources/*.lproj`,
  `Resources/Info.plist`, `Resources/AppIcon.icns`. (Mac side is owned elsewhere and you cannot test it.)
* Keep all logic that is not UI in a UI-free class library with unit tests (port of the 39 Mac tests, §6.3 / Appendix C).
* Deliver: source, `dotnet test` green, installer + portable zip for x64 and ARM64, a GitHub Actions workflow,
  `windows/README.md`, and a final report (§12).
* Do **not** push, tag, release, publish or sign anything. Report back instead.

---

## 1. Context: what exists

MDEditor is a small macOS Markdown editor (Swift/SwiftUI/AppKit, v1.0.1) in this repository. It is being open-sourced
(planned license: MIT) and distributed through a Homebrew tap; the Windows app will ship from the same repository and GitHub Releases.

```
Package.swift, Sources/MDEditor/*.swift   macOS app (reference implementation — read, don't edit)
Tests/EditingTests/main.swift             39 headless tests of the editing commands (port these, Appendix C)
Resources/preview/                        SHARED preview page: index.html, app.js, preview.css, vendor/…
Resources/AppIcon.png                     1024×1024 icon master (use it for the Windows icon, §10.5)
Resources/*.lproj/Localizable.strings     en + zh-Hans UI strings (Appendix B)
examples/sample.md                        feature demo document — your manual acceptance document
README.md                                 user-facing feature list (Chinese)
scripts/make-icon.swift                   how the icon is drawn (reference only)
windows/                                  <- everything you create goes here
```

Read these before designing anything (≈ 1,200 lines in total):

| File | Why |
| --- | --- |
| `Sources/MDEditor/MarkdownTextView.swift` | Enter/Tab behaviour and all Format commands (exact semantics) |
| `Sources/MDEditor/SplitEditorView.swift` | Render scheduling, scroll-sync algorithm, link handling, print, view modes, zoom |
| `Sources/MDEditor/MarkdownHighlighter.swift` | Editor syntax colouring rules and regexes |
| `Sources/MDEditor/ContentView.swift` | Status bar and the word-count algorithm |
| `Sources/MDEditor/MDEditorApp.swift` | Menu structure and shortcuts |
| `Sources/MDEditor/MarkdownDocument.swift` | File decoding rules |
| `Resources/preview/app.js` | **The preview contract** (header comment + the `window.render/setBase/syncToLine` functions) |
| `Tests/EditingTests/main.swift` | Executable spec for the editing commands |

---

## 2. Goals and non-goals

**Goals**

1. Behavioural parity with the Mac app (feature list in `README.md`, detailed in §6).
2. Feels like a first-class Windows app: native menus, Fluent styling, Mica/theme following, high-DPI, keyboard
   accessible, Windows shortcut conventions, standard file dialogs, file associations, Jump List/Recent.
3. Fully **offline**; no telemetry; no network access of its own.
4. Small, fast, and robust with Chinese IMEs (a large part of the audience types Chinese — IME correctness is a hard
   requirement, not polish).

**Non-goals (v1)**: tabs, plugins, Mermaid/footnotes/emoji shortcodes (the shared preview doesn't support them),
Store/MSIX distribution, auto-update, code-signing certificates, Windows 10 < 1809.

---

## 3. Fixed technical decisions

| Topic | Decision |
| --- | --- |
| UI framework | **WinUI 3** via the latest **stable Windows App SDK** (verify the current version; pin it) |
| Language / runtime | **C#**, the latest LTS .NET that the chosen Windows App SDK supports (.NET 10 LTS expected; .NET 8 acceptable). Nullable enabled, warnings-as-errors for your own code |
| Preview host | **WebView2** (Evergreen runtime). This is the same role as WKWebView on the Mac, not a "non-native" shortcut |
| Packaging model | **Unpackaged, self-contained** (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`). No MSIX / no certificate needed. Verify that file dialogs, WebView2 user-data folder and file association still work unpackaged (pitfalls in §11) |
| Architectures | **x64 and ARM64** (skip x86) |
| Min OS | Windows 10 version 2004 (build 19041) is the practical floor for current Windows App SDK; Windows 11 is the primary target. Confirm and document the real floor |
| Backdrop | Mica on Windows 11 with a plain fallback on Windows 10 |
| Third-party UI libs | Allowed only if MIT/Apache/BSD: **CommunityToolkit.WinUI** (e.g. `Sizers` for the split divider, `Segmented`) is fine. No GPL/LGPL/AGPL anything (the project is planned as MIT) |
| Window model | **One window per document**, single process (like the Mac app). No tabs in v1 |

---

## 4. Repository layout to create

```
windows/
  HANDOFF.md                      this file (leave in place)
  README.md                       build / run / test / install instructions (you write)
  MDEditor.sln
  Directory.Build.props           Version=1.0.1, nullable, analyzers, LangVersion, Authors/Copyright placeholders
  .editorconfig
  .gitignore                      bin/ obj/ *.user .vs/ artifacts/
  THIRD_PARTY_NOTICES.md          NuGet packages + pointer to Resources/preview/vendor/licenses/
  src/
    MDEditor.Core/                UI-free library (net LTS): editing commands, list/quote continuation, Tab shifting,
                                  text stats, line index, highlighter tokenizer, decode/encode, link resolver
    MDEditor.App/                 WinUI 3 app (windows, controls, WebView2 host, menus, settings, localization)
  tests/
    MDEditor.Core.Tests/          xUnit; all behavioural tests live here and must run on any OS (`dotnet test`)
  scripts/
    build.ps1                     restore + build + test
    publish.ps1                   self-contained publish for x64 + arm64 → artifacts/
    make-installer.ps1            Inno Setup (or WiX) installer + portable zip + SHA-256 sums
    make-ico.ps1 (or .py)         icon generation from Resources/AppIcon.png (§10.5)
.github/workflows/windows.yml     CI (§9). Only create this one file under .github/
```

Why a Core library: the Mac code is tested headlessly and the logic is easy to get subtly wrong (Enter, Tab,
toggles, CJK counting). Define pure functions over an immutable edit state, e.g.

```csharp
record EditState(string Text, int SelStart, int SelLength);   // UTF-16 offsets, '\n' line endings
static class MarkdownCommands {
    static EditState? Enter(EditState s);          // null = "do the default newline"
    static EditState Tab(EditState s);             static EditState BackTab(EditState s);
    static EditState Bold(EditState s); /* Italic, Strike, InlineCode, Link, CodeBlock, Heading(n), Quote, Bullet, Task */
}
```

The UI layer applies the result to the editor as a **minimal replace** (one replaced range, not "set whole text")
so undo/redo, IME and caret behave normally. This mirrors how the Mac code goes through `insertText(_:replacementRange:)`.

---

## 5. The shared preview contract (WebView2)

`Resources/preview/index.html` + `app.js` is a self-contained page. Today it talks to the Mac host through
`window.webkit.messageHandlers.<name>.postMessage(...)` and the host calls JS functions. **Keep the page unchanged** and
adapt on the Windows side with a tiny injected shim.

### 5.1 JS API the host calls (all defined by `app.js`)

| Call | Meaning |
| --- | --- |
| `render(markdown, topLine, atBottom)` | Incremental re-render (only changed top-level blocks are rebuilt), then scroll the preview so source line `topLine` (fractional, 0-based) is at the top; `atBottom=true` pins to the bottom |
| `setBase(url)` | Folder of the open document as a `file:///…/` URL **with trailing slash**; relative images/links resolve against it. Changing it rebuilds everything |
| `syncToLine(line, atBottom)` | Editor scrolled; make the preview follow |

### 5.2 Messages the page sends to the host

| `name` | `body` | Meaning |
| --- | --- | --- |
| `ready` | `true` | Scripts loaded; safe to call the API |
| `scroll` | `{ line: number, bottom: boolean }` | User scrolled the preview; scroll the editor to that fractional source line |
| `link` | `string` (raw `href`) | A link was clicked (in-page `#anchors` are handled by the page itself) |

### 5.3 Bridge shim

Register once with `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` (runs before `app.js`):

```js
(() => {
  const send = (name, body) => window.chrome.webview.postMessage({ name, body });
  window.webkit = { messageHandlers: new Proxy({}, {
    get: (_, name) => ({ postMessage: (body) => send(String(name), body) }),
  }) };
  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    if (m.cmd === 'render')      window.render(m.text, m.line, m.bottom);
    else if (m.cmd === 'setBase') window.setBase(m.url);
    else if (m.cmd === 'sync')    window.syncToLine(m.line, m.bottom);
  });
})();
```

Host → page: `CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { cmd = "render", text, line, bottom }))`
(avoids string-escaping bugs and `ExecuteScriptAsync` size/latency problems with large documents).
Page → host: `WebMessageReceived` → `args.WebMessageAsJson` → `{ name, body }`.

### 5.4 Host state machine (mirror of `SplitEditorView.Coordinator`)

```
pageReady = false;  appliedBase = null;  lastRendered = null
on message "ready":   pageReady = true; appliedBase = null; lastRendered = null; applyBase(); scheduleRender()
scheduleRender():     if !pageReady || !mode.ShowsPreview || lastRendered == text → return
                      debounce 40 ms → renderNow()
renderNow():          lastRendered = text; pos = editorPosition();
                      post {cmd:"render", text: textWithLfLineEndings, line: pos.line, bottom: pos.atBottom}
applyBase():          if pageReady && document has a file: base = file-URI of its folder + "/";
                      if base != appliedBase { appliedBase = base; lastRendered = null; post {cmd:"setBase", url: base} }
on editor scrolled:   if pageReady && mode.ShowsPreview && now >= suppressEditorScrollUntil:
                         post {cmd:"sync", line, bottom}
on message "scroll":  if mode.ShowsEditor: scrollEditor(line, bottom)   // sets suppressEditorScrollUntil = now + 150 ms
on mode change:       if preview becomes visible → lastRendered = null (forces a fresh render)
on WebView2 process failure (ProcessFailed): pageReady = false; lastRendered = null; reload the page
```

Rules that matter:

* **Always send LF line endings to the preview.** `render()` counts `\n` to map source lines; a text with bare `\r`
  (which is what a RichEditBox hands you) would make every block start at line 0.
* While an **IME composition is active**, do not render, do not push text to the document model, do not restyle.
  Resume on composition end (Mac: `hasMarkedText()` guard in `textDidChange`).
* Before **printing / exporting PDF**, force a render of the current text and await it, even if the preview pane
  is hidden (view mode = editor only).

### 5.5 WebView2 setup checklist

* Load `Resources/preview/index.html` as a **`file:///` URL** (navigate to it; copy the `preview/` folder next to the
  exe at build time, from `../Resources/preview` — single source of truth, no committed copy). Using a virtual host
  (`https://appassets…`) would make document-relative `file://` images blocked as mixed origins; the Mac app has the same
  "page is file://, images are file://" model. Verify local images in the document folder render.
* **User data folder** must be `%LOCALAPPDATA%\MDEditor\WebView2` (unpackaged default is next to the exe, which is
  not writable under `Program Files`).
* Settings: `AreDevToolsEnabled=false` (release), `AreDefaultContextMenusEnabled=false` (or a tiny custom Copy menu),
  `IsStatusBarEnabled=false`, `IsZoomControlEnabled=false`, `IsPinchZoomEnabled=false`, `IsSwipeNavigationEnabled=false`,
  `IsGeneralAutofillEnabled=false`, `IsPasswordAutosaveEnabled=false`, `AreBrowserAcceleratorKeysEnabled=false`
  (else F5/Ctrl+R/Ctrl+F etc. act on the page), `AreDefaultScriptDialogsEnabled=false`, `AreHostObjectsAllowed=false`.
* `NavigationStarting`: allow only the initial `file:///…/preview/index.html` load; **cancel everything else**.
  `NewWindowRequested`: `Handled = true` (never open windows). `PermissionRequested`: deny all.
* Colour scheme: the page follows `prefers-color-scheme`. WebView2 maps that to `Profile.PreferredColorScheme`
  (Auto = system). If the app has its own theme override, set it to match `ActualTheme` and update on change. Set
  `DefaultBackgroundColor` to the current theme background to avoid a white flash while loading.
* Zoom: `ZoomFactor = zoom` (see §6.9). The editor font scales with the same factor.
* `CoreWebView2` initialisation is async; queue the first `setBase`/`render` until `ready` arrives (state machine above).
* If the WebView2 Runtime is missing (rare on Win 11/updated Win 10): show a clear dialog with the Evergreen
  bootstrapper link; the installer should install/verify it.
* Fonts: `preview.css` font stacks start with Apple fonts. For Windows, **append** `"Segoe UI", "Microsoft YaHei UI",
  "Microsoft YaHei"` to the body stack and `"Cascadia Mono", Consolas, "Microsoft YaHei"` to the code stack. Prefer
  doing this with an extra `<style>` injected by the same document-created script (shared files untouched). If you
  must edit `Resources/preview/*`, keep it **additive and Mac-neutral** and list each edit in your final report so the
  Mac side can re-verify it.

---

## 6. Feature specification (parity matrix)

Mac concept → Windows equivalent. "Authoritative" = port the Mac behaviour exactly; Appendix C has the executable examples.

### 6.1 Documents and files

* **New / Open / Save / Save As / Close**, standard dialogs (`FileOpenPicker`/`FileSavePicker`; in an unpackaged
  app they need `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)` — pitfall §11). Open filter: `.md .markdown
  .mdown .mkd .mdwn .txt` + "All files". Drag-and-drop a file onto a window opens it (in a new window, or focuses the
  window that already has it open).
* **Untitled** document on launch with no arguments (like the Mac). With file arguments, open only those.
* **Dirty tracking** with a title marker (Notepad style: `*name.md — MDEditor`) and a Save / Don't Save / Cancel prompt
  on close/exit. The Mac autosaves in place; on Windows use explicit save, plus an optional "auto save" setting off by default.
* **Decoding (authoritative)**: UTF-8 first (strip BOM); then UTF-16 (BOM) ; then Windows-1252 / ISO-8859-1 as last resort
  (register `CodePagesEncodingProvider`). **Encoding on save**: same as opened for UTF-8 (BOM kept if present) and
  UTF-16; legacy single-byte files are saved as UTF-8. New files: UTF-8 without BOM.
* **Line endings**: remember what the file used (LF / CRLF; mixed → majority) and write the same back; new files use LF.
  Internally the editor model uses `\n`.
* **Safe save**: write to a temp file in the same folder then replace atomically (`File.Replace`/move). Documents often
  live in cloud-synced folders (Dropbox, Nextcloud, OneDrive…); a half-written file must never be visible to a sync client.
* **External change detection** (`FileSystemWatcher`, debounced): unmodified document → silently reload keeping caret and
  scroll; modified document → ask (Reload / Keep mine). The Mac gets this from NSDocument.
* **Single instance**: `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("MDEditor")` +
  `RedirectActivationToAsync`, so double-clicking a second `.md` opens a new window in the running process.
* **File association**: registered by the installer for `.md .markdown .mdown .mkd .mdwn` (ProgID `MDEditor.Markdown`,
  as an "Open with" candidate — Windows protects the default-app choice, so the app cannot silently take it; document
  "Settings → Default apps"). `.txt` registered as Alternate only. Add `Applications\MDEditor.exe` verbs.
* **Recent documents**: call `SHAddToRecentDocs` on open/save so the taskbar Jump List shows recents.

### 6.2 Window, layout and view modes

* Default window 1200 × 780 DIP, minimum 640 × 360. Remember last size/position (settings file, §6.12).
* Layout: toolbar row (centered segmented control: **Editor / Split / Preview**, with tooltips/automation names),
  content row, status bar row (22 px).
* **Split view**: editor left, preview right, draggable divider; **initial and re-entry position 50/50**; each side min
  240 DIP. Use `CommunityToolkit.WinUI.Controls.Sizers` or a small custom splitter.
* **View modes** persist globally and across launches. Switching to *Editor* hides the preview (keep the WebView2 alive,
  don't destroy it); *Preview* hides the editor; *Split* shows both. Focus goes to the editor whenever it becomes visible.
* The window tracks the system light/dark theme live.

### 6.3 Editor behaviour (authoritative: `MarkdownTextView.swift`)

All commands operate on the selection and must produce **a single undoable edit**.

**Enter** (`insertNewline`) — only when there is **no selection**, **no IME composition**, and the caret is at the **end of
the line**; otherwise do the default newline.

* List line regex `^([ \t]*)((?:[-*+]|(\d{1,9})([.)]))[ \t]+)(\[[ xX]\][ \t]+)?`:
  * item body empty (only whitespace after the marker) → **delete the whole line's text** (list ends), caret on the empty line;
  * else insert `"\n" + indent + nextMarker` where an ordered marker increments (`9)` → `10)`, keeps the delimiter and the
    original spacing) and a task item gets a fresh `[ ] `.
* Quote regex `^([ \t]{0,3}(?:>[ \t]?)+)`: empty quote body → delete the line text; else `"\n" + prefix`.
* Anything else → default newline.

**Tab / Shift+Tab** (indent unit = 4 spaces; never insert a `\t`; no IME composition):

* Tab with a selection **or** when the caret line is a list line → indent **all touched lines** (skip lines that are
  empty); with a bare caret on a non-list line → insert 4 spaces at the caret.
* Shift+Tab → outdent all touched lines: remove one leading `\t`, or up to 4 leading spaces.
* Selection after the edit: caret case keeps the caret on the same text (shifted by the first line's delta, never before
  line start); selection case keeps **all shifted lines selected** (without the trailing newline) so the key can repeat.

**Format commands** (menu + shortcuts, §6.8). `lineRange` = full lines touching the selection incl. trailing `\n`:

| Command | Behaviour |
| --- | --- |
| Bold `**`, Italic `*`, Strike `~~`, Inline code `` ` `` | If the selection is **already wrapped** by the marker → unwrap and keep the inner text selected. Empty selection → insert marker pair and put the caret between. Else wrap and keep the inner text selected |
| Link | Replace selection `T` with `[T](url)`, select `url` (works with empty `T`) |
| Heading 1/2/3 | On the caret's line: strip existing `#`×1–6 plus one space, then add `#×n␠` — **unless the line already had exactly level n, in which case it toggles off**. Caret to end of the line |
| Quote `> `, Bullet `- `, Task `- [ ] ` | Toggle a line prefix over all selected lines: if every non-empty line already starts with the prefix → remove it, else add to all non-empty lines (blank lines untouched). Selection covers all affected lines (without trailing newline) |
| Code block | Insert ```` ```\n<selection>\n```\n ````, with a leading `\n` if the selection doesn't start at a line start. No selection → caret on the empty line inside the fence; with selection → caret after the block |

### 6.4 Syntax colouring (authoritative: `MarkdownHighlighter.swift`)

Only **colour, weight, slant, strikethrough and a faint background** may change — **never font size or family**
(keeps line geometry stable for scroll sync). Skip colouring entirely above **300,000 characters**. Run on the text
with `\n` endings (offset-preserving copy if the control stores `\r`). Re-run after every edit except during IME
composition (debounce / limit to the changed region if full runs are too slow — see §7).

Block level, line by line (state machine):

1. **Front matter**: if line 0 trimmed is `---`, every line through the closing `---` or `...` is *secondary*.
2. **Fenced code**: opening fence = up to 3 spaces + ≥3 of `` ` `` or `~`; the fence lines are *secondary*; lines inside are *code*
   colour; closing fence = same char, ≥ opening length, nothing else on the line; an unterminated fence runs to EOF.
3. **ATX heading** (`#`×1–6 followed by space or EOL): whole line *heading* colour + **bold**; the `#` run itself *secondary*.
4. **Block quote** (up to 3 spaces then `>`): whole line *secondary*.

Inline/other regexes (.NET syntax equal to the ICU originals; use `RegexOptions.Multiline` where noted; **skip matches that
intersect a fenced block or front matter**). Apply **in this order** (later wins on overlapping attributes):

```
listMarker (Multiline)  ^[ \t]*((?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?)      → group 1 = accent2 (orange)
hrule (Multiline)       ^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$                          → secondary
tableSep (Multiline)    ^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(?:\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$   → secondary
italic                  (?<![*\w\\])([*_])(?![*_\s])([^\n*_]*?[^\s*_\\])\1(?![*\w])    → italic + purple
bold                    (\*\*|__)(?=\S)([^\n]*?\S)\1                                    → bold
strike                  ~~(?=\S)[^\n]*?\S~~                                             → strikethrough + secondary
link                    !?\[([^\]\n]*)\]\(([^)\n]*)\)                                  → group 1 blue, group 2 secondary
autolink                <(?:https?://|mailto:)[^>\s]+>                                  → blue
math                    \$\$[^\n]+?\$\$|(?<![\\$])\$(?![\s$])[^$\n]*?(?<![\s\\])\$(?!\d)  → indigo
inlineCode              (`+)(?!`)[^\n]*?[^`\n](\1)(?!`)|`[^`\n]`                       → pink + 10 % pink background, not italic
```

Colours (Mac uses system colours; use these as a start and **tune for ≥ 4.5:1 contrast** on the actual editor background):

| Role | Light | Dark |
| --- | --- | --- |
| heading / link text / autolink (blue) | `#007AFF` | `#0A84FF` |
| list marker (orange) | `#FF9500` | `#FF9F0A` |
| italic (purple) | `#AF52DE` | `#BF5AF2` |
| inline code (pink) | `#FF2D55` | `#FF375F` |
| fenced code body (teal) | `#30B0C7` | `#40C8E0` |
| math (indigo) | `#5856D6` | `#5E5CE6` |
| secondary (markers, quotes, URLs, rules) | `#8A8A8E` | `#99999D` |
| body text | theme primary text | theme primary text |

Editor font: monospace (`Cascadia Mono`, fallback `Consolas`) 14 DIP × zoom, extra line spacing ≈ 0.32 × font size,
content padding ≈ 18 DIP horizontal / 16 DIP vertical, caret in the accent colour, spell check / text prediction /
autocorrect **off**, wrapping **on**.

### 6.5 Scroll sync (authoritative: `SplitEditorView.swift` §"Scroll sync" and `app.js` §"scroll sync")

Coordinates are **fractional source line numbers** (0-based; `12.5` = halfway down line 12), not percentages.

* `editorPosition()`: find the logical line at the top of the visible area (a "line" = text between `\n`s, including its
  soft-wrapped rows); `fraction = (visibleTopY − lineTopY) / lineHeight` clamped to 0…1; `atBottom` = scrolled to within
  1 px of the maximum (and the document is actually scrollable).
* `scrollEditor(line, bottom)`: `bottom` → scroll to max; else `y = lineTopY(floor(line)) + frac × lineHeight`; clamp; do
  nothing if within 0.5 px; set the 150 ms suppression window before scrolling so it doesn't echo back.
* Keep a cached array of line start offsets (rebuild lazily after edits; binary-search it).
* An empty last line (after a trailing `\n`) and an empty document must not crash or return NaN.
* The preview does its own mapping and loop suppression (`suppressUntil`) — don't touch it.

### 6.6 Preview features (come for free from the shared page — verify, don't reimplement)

GFM tables/task lists/strikethrough/autolinks, `> [!NOTE]` alerts, YAML front matter, highlight.js (43 languages),
KaTeX math (`$…$`, `$$…$$`, price-safe), heading anchors, relative images, DOMPurify sanitising, light/dark/print CSS.

### 6.7 Links (authoritative: `openLink` in `SplitEditorView.swift`)

On a `link` message with `href`:

1. If it parses as an absolute URI: `http`, `https`, `mailto`, `tel` → open with the default handler
   (`Launcher.LaunchUriAsync`); **any other scheme (including `file:`, `javascript:`, `ms-*:`, and drive-letter paths
   like `C:\x` that parse as a scheme) is ignored.**
2. Otherwise treat it as a relative path (normalise `\` → `/`, drop `#fragment`, percent-decode). No saved file → system
   beep. Resolve against the document's folder, canonicalise; if the target doesn't exist → beep.
3. Extension `md markdown mdown mkd txt` → open in MDEditor (focus the existing window if that file is already open).
4. Extension in the **allow-list** — images (`png jpg jpeg gif webp svg bmp ico tif tiff heic avif`), `pdf`, audio/video
   (`mp3 wav m4a flac mp4 mov webm mkv`), `html htm`, text-like (`csv json xml log yml yaml toml`) → open with the default app.
5. **Everything else** (`exe bat cmd ps1 lnk msi js vbs scr jar …`) → **never launch**; just reveal it in Explorer
   (`explorer.exe /select,"path"`).

### 6.8 Menus and shortcuts

Mac ⌘ → Ctrl, ⌥ → Alt, ⇧ → Shift, **except** avoid Ctrl+Alt+letter/digit chords: on many European layouts `Ctrl+Alt` *is*
AltGr and would block typing characters. Use a WinUI `MenuBar` (File, Edit, Format, View, Help) with `KeyboardAccelerator`s
and show the accelerators in the menu. Recommended defaults (adjust if you find real conflicts, and note them in the report):

| Menu | Command | Shortcut |
| --- | --- | --- |
| File | New / Open… / Save / Save As… | Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S |
| File | Export as PDF… / Print… | (none) / Ctrl+P |
| File | Close window / Exit | Ctrl+W / Alt+F4 |
| Edit | Undo / Redo | Ctrl+Z / Ctrl+Y (also Ctrl+Shift+Z) |
| Edit | Cut / Copy / Paste / Select All | Ctrl+X / C / V / A (paste = **plain text only**) |
| Edit | Find… / Replace… / Next / Previous | Ctrl+F / Ctrl+H / F3 / Shift+F3 (Ctrl+F pre-fills the selection) |
| Format | Bold / Italic / Strikethrough | Ctrl+B / Ctrl+I / Ctrl+Shift+X |
| Format | Inline Code / Link | Ctrl+E / Ctrl+K |
| Format | Heading 1 / 2 / 3 | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Format | Quote / Bulleted List / Task List / Code Block | Ctrl+Shift+Q / Ctrl+Shift+L / Ctrl+Shift+T / Ctrl+Shift+K |
| View | Editor Only / Split / Preview Only | Ctrl+Shift+1 / 2 / 3 |
| View | Zoom In / Out / Actual Size | Ctrl+= (and Ctrl+numpad +) / Ctrl+- / Ctrl+0; Ctrl+mouse-wheel in the editor |
| Help | About (version, MIT license, third-party notices, GitHub link) | — |

Disable (rather than hide) Format items when the editor isn't visible.

### 6.9 Zoom

One global `zoom`, default 1.0, range **0.6–3.0**, step 0.1, rounded to one decimal. Editor font size = `14 × zoom`
(re-run colouring after changing the font), preview `ZoomFactor = zoom`. Persisted.

### 6.10 Find / replace

WinUI has no built-in find bar. Implement an in-window bar over the editor: find, replace, replace all, next/previous,
case-sensitive toggle, whole-word toggle, "n of m" counter, Esc closes, works while the preview is visible. Search wraps.
It must operate through the editor's normal edit path so Replace is undoable (Replace All = one undo step).

### 6.11 Print and PDF

* **Print…** → `CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System)` after the forced render of §5.4. Margins ≈
  40 pt (0.55"), job title = window title. The shared print CSS (`@media print`) forces a white page, drops the max
  width and avoids breaking code blocks/tables.
* **Export as PDF…** → `PrintToPdfAsync` with the same margins, default file name = document name.

### 6.12 Settings persistence

`%LOCALAPPDATA%\MDEditor\settings.json` (works unpackaged): `viewMode` (`editor|split|preview`), `zoom`, last window
bounds (and maximised flag), optional `autoSave`. Tolerate a missing/corrupt file. Multiple windows share the settings and
should react to a mode/zoom change made in another window.

### 6.13 Status bar (authoritative: `TextStats` in `ContentView.swift`)

`"<n> words"  "<n> characters"  "<n> min read"` (min read hidden when words = 0), tabular figures, secondary colour.

Algorithm over Unicode scalars (`Rune`): `characters` = count of non-whitespace scalars. Each scalar in the CJK ranges
`3040–30FF, 3400–4DBF, 4E00–9FFF, AC00–D7AF, F900–FAFF, 20000–2FA1F` counts as **one word** and breaks any Latin word.
Otherwise a letter or number scalar starts a Latin word if not already inside one; anything else ends the word.
`minutes = ceil(cjk/400 + latin/230)`, at least 1 when words > 0, else 0. (`Rune.IsLetter || Rune.IsNumber` is an accepted
stand-in for Swift's `isAlphabetic || numericType != nil`.)
Expected: `"Hello world"` → 2 words / 10 chars / 1 min; `"中文测试"` → 4 / 4 / 1; `"Hello 世界"` → 3 / 7 / 1; `""` → 0 / 0 / 0.

### 6.14 Localization

English (source) and Simplified Chinese, chosen by the Windows display language (any `zh-*` → Simplified is acceptable
for v1). Use `.resw` resources (`Strings/en-US`, `Strings/zh-Hans`) or the equivalent; no hard-coded UI strings.
Appendix B lists all existing strings plus suggested translations for the Windows-only ones.

### 6.15 Accessibility

All controls have automation names; full keyboard operation; honours high-contrast themes; the segmented control and
find bar are reachable by keyboard; text scaling of the OS does not clip the toolbar/status bar.

---

## 7. The editor control: decision, spike, gates

This is the highest-risk part. The editor must be native; **do the spike first (milestone M0) and record the outcome**
in `windows/docs/editor-decision.md` before building the rest.

**Preferred candidate: `RichEditBox` used as a plain-text editor** with character formatting for colouring. Hints (verify each):

* Disable rich features: `DisabledFormattingAccelerators = All` (otherwise Ctrl+B/I/U apply rich formatting),
  `IsSpellCheckEnabled=false`, `IsTextPredictionEnabled=false`, `TextWrapping=Wrap`, `ClipboardCopyFormat=PlainText`; intercept
  paste (`Paste` event) and insert plain text; intercept Tab/Enter in `PreviewKeyDown`.
* Text access via `Document.GetText/SetText`; the control uses **`\r` as paragraph separator** (look at `TextGetOptions`
  such as `UseLf`/`UseCrlf` and verify offsets). Convert at the boundary so the rest of the app only sees `\n`.
* IME: `TextCompositionStarted / Changed / Ended` mark the composition window. The Enter that commits an IME candidate
  must **not** trigger list continuation.
* Geometry for scroll sync: `ITextRange.GetRect(PointOptions.ClientCoordinates, …)` for a line's first/last character,
  `ITextRange.SetPoint` / hit-testing for "line at top"; the scroll position comes from the control's inner `ScrollViewer`
  (find it through `VisualTreeHelper`; `ViewChanged` is the scroll event).
* Colouring through `ITextRange.CharacterFormat` (ForegroundColor, Bold, Italic, Strikethrough, BackgroundColor).

**Gates — all must pass, otherwise change approach:**

| # | Gate | How to test |
| --- | --- | --- |
| G1 | Pinyin/Wubi/Japanese IME typing is flawless: no dropped/duplicated characters, no caret jumps, candidate window placed correctly, Enter/Space/Esc during composition never trigger list/Tab logic | Microsoft Pinyin & Japanese IME, type inside a list item and inside headings |
| G2 | **Undo/redo is not polluted by colouring**: Ctrl+Z after typing removes the typed text, not "a colour change"; undo/redo after a Format command or Replace All is a single step | Manual + automated |
| G3 | Colouring a **5,000-line / 300 kB** document never blocks typing (no perceivable lag; target < 16 ms of UI-thread work per keystroke, otherwise restyle only the dirty region/visible area on a debounce) | Stopwatch around the colouring pass |
| G4 | Line geometry for scroll sync is exact enough that preview and editor stay aligned on `examples/sample.md` and on a 2,000-line file with long wrapped lines, images and tables | Visual check at several scroll positions |
| G5 | Offsets are UTF-16 code units consistent with .NET string indices, including emoji (surrogate pairs) and CJK | Unit/automation test |
| G6 | Plain-text paste only; copy yields plain text without rich-text artefacts | Paste from Word/web |

**If colouring pollutes undo (G2)**: common remedies are (a) apply the formatting inside a call sequence that doesn't create
undo entries, if the API offers it, or (b) disable the native undo stack (`Document.UndoLimit = 0`) and implement your own
undo/redo of `(offset, removed, inserted)` edits recorded from text-change events, with typing coalescing and your Format
commands as single entries. Pick whichever passes G1–G6.

**Fallbacks, in order**, only if no RichEditBox approach can pass the gates:

1. A plain `TextBox` editor **without** syntax colouring (robust, but a visible regression from the Mac app).
2. A custom Win2D/DirectWrite text control (large effort — estimate before starting).
3. A WebView2-hosted CodeMirror/Monaco editor. **This is not "native WinUI" — do not choose it without the user's explicit approval.**

Whichever you choose, stop after the spike and put the evidence (what passed/failed, screenshots or timings) in the
decision document, and ask the user if you end up on fallback 1–3.

---

## 8. Milestones (each ends with `dotnet test` green and a runnable build)

| M | Content | Exit criteria |
| --- | --- | --- |
| **M0** Spikes | (a) editor control gates G1–G6; (b) WebView2 loads `Resources/preview/index.html` through the bridge and renders `examples/sample.md`, with images, KaTeX, code highlighting, dark mode | `windows/docs/editor-decision.md`, a throw-away demo that works |
| **M1** Core | `MDEditor.Core` + xUnit: Enter, Tab/BackTab, all Format commands, text stats, line index, highlighter tokenizer, decoding/encoding/line-ending round trip, link resolver | All Appendix C vectors pass, plus your extra edge cases (empty doc, CRLF, surrogate pairs, 1 MB text) |
| **M2** Shell | Window, toolbar + view modes, splitter, editor + preview wiring, render scheduling, scroll sync both directions, zoom, theme, status bar | Live preview + sync behave like the Mac on `sample.md` |
| **M3** Documents & commands | File ops, dirty/title, recents, single instance + CLI args, external-change handling, menus/shortcuts, find/replace, print/PDF, links, drag-and-drop | Manual checklist (§12) |
| **M4** Polish | Localization (en + zh-Hans), icon, About, settings persistence, accessibility pass, error handling (unreadable file, read-only file, WebView2 missing) | Checklist + review of every string |
| **M5** Ship | Publish scripts, installer, portable zip, checksums, CI workflow, `windows/README.md`, THIRD_PARTY_NOTICES | CI green on `windows-latest` for x64 and ARM64 build; installer installs/uninstalls cleanly on a clean VM |

---

## 9. Build, packaging and CI

* `scripts/build.ps1` – restore, build Release, run tests. `scripts/publish.ps1` – `dotnet publish -c Release -r win-x64`
  and `win-arm64`, self-contained, `PublishReadyToRun=true`; **no trimming** (WinUI/WinRT reflection). Copy
  `Resources/preview/` into `<output>/preview/` (and keep `preview/vendor/licenses/`).
* Installer: **Inno Setup** (preferred; silent-install flags `/VERYSILENT /SUPPRESSMSGBOXES` for winget/Scoop later) or WiX.
  Per-user install by default (no admin prompt), optional all-users. Registers file associations (§6.1) and an
  uninstaller. Outputs: `MDEditor-<version>-win-x64-setup.exe`, `MDEditor-<version>-win-arm64-setup.exe`, plus portable
  `MDEditor-<version>-win-<arch>.zip`, and `SHA256SUMS.txt`.
* Version single-sourced from `windows/Directory.Build.props` (currently **1.0.1**, matching the Mac release); the
  installer, assembly version and About box all read it.
* **No code signing in v1.** The installer will trigger SmartScreen ("Windows protected your PC → More info → Run anyway").
  Document this in `windows/README.md` (it is the Windows analogue of the Mac Gatekeeper note). Leave a clearly marked
  hook in the scripts where `signtool` would go.
* **`.github/workflows/windows.yml`**: on pull requests and pushes → `windows-latest`: setup .NET, `dotnet test` (Core tests),
  build x64 and ARM64. On tags `v*` additionally produce the installers/zips and **attach them to the GitHub Release for
  that tag** (create the release if missing, otherwise append: the macOS workflow writes to the same release from a
  different job — use `gh release create … || gh release upload … --clobber`, never delete/overwrite the release itself).
  Add a headless **smoke test** step if feasible (launch with a `--smoke-test` switch that opens a window, renders a small
  document, asserts the preview DOM contains the expected heading via a WebView2 script call, exits 0/1).
  Only this file under `.github/` is yours.

---

## 10. Other details

### 10.1 Command line
`MDEditor.exe [file …]` opens each file in its own window. `--smoke-test` (CI only). Unknown switches are ignored.

### 10.2 Error handling
Unreadable / locked / too-large (> 50 MB) / binary-looking file → friendly dialog, no crash. Read-only file → open
normally, "Save" becomes "Save As". Disk-full or permission errors on save keep the document dirty and keep the user's text.

### 10.3 Performance budget
Cold start to interactive editor < 1.5 s on a mid-range laptop; typing latency imperceptible on a 5,000-line document;
rendering a 300 kB document does not freeze the UI (preview work happens in the WebView2 process; keep the host side
allocation-light, don't re-send text that hasn't changed — `lastRendered`).

### 10.4 Security
Treat document content as untrusted: the preview is sanitised by DOMPurify, navigation is locked (§5.5), links follow the
allow-list (§6.7). No network calls from the host. No telemetry, no crash upload.

### 10.5 Icon
Master is `Resources/AppIcon.png` (1024², transparent, macOS grid: the artwork is an ~824 px squircle inset 100 px with a
soft shadow below). For Windows: **crop to the 824 px squircle (x/y 100…924) without the shadow**, then render `.ico` with
16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px frames (PNG-compressed 256). Use it for the exe, the window, the installer and
the file-type icon. Do not redraw it; if you think it needs a Windows-specific variant, ask. Script it
(`scripts/make-ico.*`) and commit the resulting `.ico`.

---

## 11. Known pitfalls (read before you debug for hours)

1. **RichEditBox line endings** are `\r`; regexes with `$`/`^` in Multiline mode and the preview's `\n` line counting break
   silently if you don't convert at the boundary (offset-preserving `\r`→`\n` copy for the highlighter; real `\n` for the preview).
2. **IME**: never mutate the text, move the caret, restyle, or re-render during composition; don't treat the Enter/Space that
   commits a candidate as a user "Enter". Test with real IMEs, not just `SendKeys`.
3. **AltGr = Ctrl+Alt** on many layouts — don't bind Ctrl+Alt+letter/digit.
4. **Unpackaged file pickers** throw unless initialised with the window handle (`InitializeWithWindow`).
5. **WebView2 user data folder** defaults to a non-writable location under `Program Files` → set it explicitly (§5.5).
6. **`ApplicationData.Current.LocalSettings`** needs package identity → use the JSON settings file.
7. **High DPI / mixed-DPI monitors**: use DIP units; test 100/150/200 % and moving a window between monitors; WebView2 should follow.
8. **Focus**: WebView2 swallows keyboard shortcuts once focused. Accelerators on the `MenuBar`/window must still work while the
   preview has focus (handle at window level, or re-route), and focus should go back to the editor after view-mode changes.
9. **Tab in WinUI** is focus traversal; the editor must intercept it (`PreviewKeyDown`, `Handled = true`).
10. **Title-bar customisation** (`ExtendsContentIntoTitleBar`) + Mica need per-version fallbacks; keep drag regions correct and
    ensure the caption buttons stay readable in both themes.
11. **ARM64**: avoid x64-only native dependencies; build and (if you can) run on ARM64.
12. **Large documents**: don't colour or diff on the UI thread without a budget (§7 G3, §10.3).
13. **Don't** call `ExecuteScriptAsync` with the whole document inlined; use `PostWebMessageAsJson`.
14. **Surrogate pairs / CJK**: all offsets are UTF-16 code units; the Core library must never split a surrogate pair when it
    wraps/unwraps selections.

---

## 12. Definition of done and the report you must write

**Acceptance checklist** (run on a real Windows 11 machine, ideally also Windows 10 and ARM64; use `examples/sample.md`
as the main document; tick each in the report):

- [ ] Fresh install from the installer; launches without admin; uninstall removes everything except user settings/documents
- [ ] Opening `sample.md` shows editor left / preview right, 50/50; tables, task lists, alerts, front matter, code highlighting,
      KaTeX, relative images all render; dark/light switch live
- [ ] Typing updates the preview in well under 100 ms; no flicker, images don't reload on every keystroke
- [ ] Scroll either pane → the other follows by source line; clicking `#anchor` links scrolls both; end-of-document aligns
- [ ] All 39 ported editing tests green; the same behaviours work in the real window (Enter in lists/quotes/tasks, Tab/Shift+Tab, every Format command)
- [ ] Chinese IME (Microsoft Pinyin): typing, candidate selection with Enter/Space/numbers, inside lists/headings/quotes — no glitches
- [ ] View modes via toolbar + shortcuts; mode and zoom persist across restarts; zoom 0.6–3.0 affects both panes
- [ ] Find/Replace/Replace All, undo of each; Ctrl+F prefill; F3/Shift+F3
- [ ] Save/Save As/Open/New, dirty marker, unsaved-changes prompt (window close, Alt+F4, system shutdown/sign-out), safe atomic save, CRLF/BOM/UTF-16/Windows-1252 files round-trip as specified
- [ ] External modification of an open file: clean doc reloads silently; dirty doc asks
- [ ] Double-click `.md` (after choosing MDEditor as default app) opens in the running instance; multiple files → multiple windows; drag-and-drop
- [ ] Link handling matrix of §6.7 (http, mailto, relative `.md`, relative image, `.exe` (reveal only), `file:` (ignored))
- [ ] Print dialog and Export PDF produce correct output with a white background, in every view mode
- [ ] English and Simplified Chinese UI complete, no clipped text at 100–200 % scaling
- [ ] Narrator/keyboard-only pass of the main flows; high-contrast theme readable
- [ ] 300 kB and 5,000-line documents are comfortable; > 300,000 chars disables colouring but everything else works
- [ ] CI green; installer + zip + checksums produced for x64 and ARM64

**Final report** (`windows/REPORT.md`, short): what is done; every deviation from this brief and why; the editor decision;
known gaps; edits (if any) to shared `Resources/preview/*` (file + diff summary); the exact shortcut table you shipped;
measured timings (§10.3, G3); anything you need the user to decide.

---

## 13. Working rules

* Work only under `windows/` (+ `.github/workflows/windows.yml`). Anything else you believe must change → write it into the
  report; don't change it.
* Don't push, tag, publish a release, upload anywhere, or sign. Commit locally only if the user asked you to.
* Pin NuGet versions; record every package and its license in `THIRD_PARTY_NOTICES.md`; MIT/Apache/BSD only.
* No personal data in the repo (no usernames, machine names, absolute paths, emails). Use `MDEditor` as the product name and
  leave publisher/copyright fields as clearly marked placeholders (`Directory.Build.props`) — the user will fill them.
* Keep the repository cross-platform friendly: `dotnet test` for `MDEditor.Core.Tests` must pass on macOS/Linux too
  (the Mac side will run it).
* Prefer small, reviewable commits/changes and keep the code commented where behaviour mirrors the Mac (cite the Swift
  function it mirrors).
* When a decision isn't covered here, choose the simplest option that keeps Mac parity, and log it in the report.

---

## 14. Ask the user (don't guess)

1. Publisher / copyright holder string for the installer and About box.
2. Windows minimum version if the Windows App SDK floor turns out higher than Windows 10 2004.
3. Whether to proceed if the editor spike ends on fallback 1–3 (§7).
4. Whether the installer should offer to become the default `.md` handler guidance page after install.
5. Any shortcut that conflicts with a screen reader or common IME hot-key on their setup.

---

## Appendix A — Reference: how the Mac app is wired (for orientation)

* Window = one `NSDocument` (SwiftUI `DocumentGroup`), `ContentView` = `SplitEditorView` (NSSplitView: NSScrollView/NSTextView on
  TextKit 1 + WKWebView) + `StatusBar`; toolbar segmented picker = view mode; `@AppStorage` keeps `viewMode` and `zoom`.
* Text edits go through `textDidChange` → (not during IME composition) re-colour → update the document binding → debounce 40 ms →
  `render(text, topLine, atBottom)`.
* The preview posts `scroll`; the editor posts `syncToLine` through `evaluateJavaScript`; both sides suppress echo for 150 ms.

## Appendix B — Strings

Existing (English source → Simplified Chinese):

| English | 简体中文 |
| --- | --- |
| View | 视图 |
| Editor Only | 仅编辑器 |
| Split View | 分栏视图 |
| Preview Only | 仅预览 |
| Switch between editor, split view and preview | 在编辑器、分栏和预览之间切换 |
| Zoom In / Zoom Out / Actual Size | 放大 / 缩小 / 实际大小 |
| Print… | 打印… |
| Find / Find… / Find and Replace… | 查找 / 查找… / 查找和替换… |
| Find Next / Find Previous | 查找下一个 / 查找上一个 |
| Format | 格式 |
| Bold / Italic / Strikethrough | 粗体 / 斜体 / 删除线 |
| Inline Code / Link | 行内代码 / 链接 |
| Heading 1 / Heading 2 / Heading 3 | 一级标题 / 二级标题 / 三级标题 |
| Quote / Bulleted List / Task List / Code Block | 引用 / 无序列表 / 任务列表 / 代码块 |
| `%d words` | `%d 字` |
| `%d characters` | `%d 个字符` |
| `%d min read` | `约 %d 分钟阅读` |

Windows-only strings you will need (suggested Chinese; improve as you see fit): File 文件 · Edit 编辑 · Help 帮助 ·
New 新建 · Open… 打开… · Save 保存 · Save As… 另存为… · Export as PDF… 导出为 PDF… · Close 关闭 · Exit 退出 ·
Undo 撤销 · Redo 重做 · Cut 剪切 · Copy 复制 · Paste 粘贴 · Select All 全选 · Replace… 替换… · Replace 替换 ·
Replace All 全部替换 · Match case 区分大小写 · Whole word 全字匹配 · About MDEditor 关于 MDEditor · Untitled 未命名 ·
"Do you want to save changes to {0}?" 是否保存对“{0}”的更改？ · Save 保存 / Don't Save 不保存 / Cancel 取消 ·
"The file has changed on disk." 文件已在磁盘上被修改。 · Reload 重新加载 · Keep my version 保留我的版本 ·
"The preview component (WebView2) is missing." 缺少预览组件（WebView2）。

## Appendix C — Executable examples for `MDEditor.Core.Tests`

Notation: `|` = caret, `«…»` = selection, `\n` = newline, `␠` only used for emphasis. Each line is
`[command] "input" -> "expected"`. These are the exact cases from `Tests/EditingTests/main.swift`; port them 1:1, then add your own.

```
Enter: list continuation
[enter] "- item|"            -> "- item\n- |"
[enter] "* item|"            -> "* item\n* |"
[enter] "1. one|"            -> "1. one\n2. |"
[enter] "9) nine|"           -> "9) nine\n10) |"
[enter] "  - sub|"           -> "  - sub\n  - |"
[enter] "- [x] done|"        -> "- [x] done\n- [ ] |"
[enter] "- a\n- |"           -> "- a\n|"
[enter] "1. a\n2. |"         -> "1. a\n|"
[enter] "> quote|"           -> "> quote\n> |"
[enter] "> a\n> |"           -> "> a\n|"
[enter] "plain|"             -> "plain\n|"            (default newline)
[enter] "- ab|cd"            -> "- ab\n|cd"           (caret not at end of line → default newline)
[enter] "intro\n- a|"        -> "intro\n- a\n- |"

Tab / Shift-Tab
[tab]     "a|b"              -> "a    |b"
[tab]     "- item|"          -> "    - item|"
[backtab] "    - item|"      -> "- item|"
[backtab] "  - item|"        -> "- item|"
[backtab] "- item|"          -> "- item|"
[tab]     "«a\nb»"           -> "«    a\n    b»"
[backtab] "«    a\n    b»"   -> "«a\nb»"

Inline formatting
[bold]   "a «word» b"        -> "a **«word»** b"
[bold]   "a **«word»** b"    -> "a «word» b"
[bold]   "a |b"              -> "a **|**b"
[italic] "«x»"               -> "*«x»*"
[strike] "«x»"               -> "~~«x»~~"
[code]   "«x»"               -> "`«x»`"
[bold]   "«中文»"             -> "**«中文»**"

Block formatting
[h1]     "title|"            -> "# title|"
[h2]     "# title|"          -> "## title|"
[h1]     "# title|"          -> "title|"
[h2]     "a\nb|"             -> "a\n## b|"
[quote]  "«a\nb»"            -> "«> a\n> b»"
[quote]  "«> a\n> b»"        -> "«a\nb»"
[bullet] "«a\nb»"            -> "«- a\n- b»"
[task]   "«a»"               -> "«- [ ] a»"
[link]   "«text»"            -> "[text](«url»)"
[link]   "|"                 -> "[](«url»)"
[codeblock] "|"              -> "```\n|\n```\n"
[codeblock] "«a = 1»"        -> "```\na = 1\n```\n|"
```

(The "default newline" cases mean your command returns "not handled"; the test harness then applies a plain `\n` insertion at
the selection, like a text box would.)

Extra cases to add yourself: empty document; selection spanning a trailing newline; CRLF input normalised at the boundary;
ordered list `99.`→`100.`; `+` bullets; indented quote `   > x`; unwrap when markers are adjacent but not both present;
heading on a line that has 7+ `#`; code block inside an existing fence; surrogate pairs (`😀`) around the selection.

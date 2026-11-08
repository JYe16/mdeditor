# MDEditor for Windows: report

Version 1.0.1, WinUI 3 (Windows App SDK 2.6.5), .NET 10, unpackaged and self-contained, x64 and ARM64.
The editor decision and its evidence are in `docs/editor-decision.md`.

## Acceptance checklist (HANDOFF §12)

Run on one Windows 11 x64 machine (build 26300) at 175 % scaling.
**Verified** means exercised in the real application; **not verified** means no one has checked it yet.

| Item | Status |
| --- | --- |
| Install, launch without admin, uninstall removes everything except settings | Verified with a folder containing spaces and Chinese characters, plus in-place upgrade |
| `sample.md`: split view, tables, task lists, alerts, front matter, highlighting, KaTeX | Verified. Relative images: path logic only, none in the test document |
| Dark/light switch | Verified |
| Live preview update | Verified; the render debounce is 40 ms |
| Scroll sync by source line, both directions, end of document | Editor side and preview mapping verified by round trip; dragging by hand not done |
| 39 ported editing tests, plus real Enter/Tab/Format keys | Verified: 140 unit tests pass |
| Chinese IME (Microsoft Pinyin) | Verified with real key events; candidate-window placement not checked |
| View modes, zoom 0.6–3.0, persistence | Verified except the toolbar buttons, which share the same code path as the tested command |
| Find, Replace All, undo | Replace All and its undo verified; find bar typing and F3 not run by hand |
| Save, dirty marker, prompt, atomic save, CRLF/BOM round trip | Verified; UTF-16 and Windows-1252 by unit test. The shutdown/sign-out prompt is implemented but not verified |
| External change | Clean reload verified; the dirty-document question not run |
| Open in running instance; multiple files; relative path from another folder | Verified. Drag and drop **not verified** |
| Link matrix (§6.7) | Unit tests only; no link was clicked in the real window |
| Print dialog; PDF export in editor-only mode | Print dialog opens; PDF verified. Paper output not inspected |
| English and Simplified Chinese | Verified at 175 %; other scales not checked |
| Narrator, keyboard-only, high contrast | **Not verified**. Automation names and the high-contrast palette are in place |
| 300 kB and 5,000-line documents; over-limit documents | Verified, including 1.2 MB with colouring off (2.7 s to load) |
| CI; ARM64 | Workflow written, **never run**. ARM64 builds and packages but was **never executed** |
| Windows 10 | **Not verified**; build 19041 is the declared minimum |

## Deviations from HANDOFF.md

1. **Single instance uses a mutex and a named pipe**, not `AppInstance`. Redirected activation passes the original command line, so a relative path from another working directory would resolve wrongly. The pipe sends absolute paths.
2. **UI strings are a code table (`L.cs`)**, not `.resw`, which avoids depending on PRI language selection in an unpackaged app. All strings are in one file.
3. **Custom splitter and toggle buttons** instead of CommunityToolkit: one fewer dependency for about 60 lines.
4. **Preview zoom uses CSS zoom on the root element.** The WinUI WebView2 wrapper has no `ZoomFactor`. Scroll sync is tested at 150 %.
5. **Shortcuts are handled in one window-level table**, and menus only display them. This is what lets them work while WebView2 has focus.
6. **Print margins are set with `@page` in the injected style**, because `ShowPrintUI` takes no margins.
7. **Caret colour** is the system colour: RichEditBox has no API for it.
8. **Front matter is excluded from inline colouring** as the brief says. The Mac code excludes only fences.
9. **`install.exe`** is an extra copy of the x64 installer, requested by the user.
10. **`--lang` and `--theme` switches** were added for diagnostics.
11. **"Open with" registration is best-effort.** Security software can refuse these registry writes; setup then finishes and tells the user instead of failing.
12. Links with drive letters, UNC paths and alternate data streams are ignored after percent-decoding. That is stricter than §6.7, because clicking a UNC link could leak credentials.

Resources under `Resources/preview/` were not modified.

## Shortcuts

As in HANDOFF §6.8, with no changes. On a machine where a global hotkey tool owns a chord, that tool wins; screenshot tools commonly take Ctrl+1 and Ctrl+2.

## Timings

Keystroke on 5,000 lines, across runs: median 2–5 ms, p95 5–11 ms. Longest colouring slice: 8 ms.
Load plus first colouring of 279 kB: 0.8–0.9 s. Launch of the installed build to a titled window: about 0.4 s, without a document open.

## Known issue

`Application.Exit()` crashes in `CoreMessagingXP.dll` with Windows App SDK 2.6.5. The app does not call it: windows close normally
(exit code 0, verified) and the smoke test leaves through `Environment.Exit`.

## Decisions left to the maintainer

* Inno Setup is free for non-commercial use; commercial use requires a licence from its author.
* Code signing is not set up; hooks are marked in the scripts.

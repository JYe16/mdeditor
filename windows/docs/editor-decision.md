# Editor control decision (HANDOFF §7)

**Decision: RichEditBox used as a plain-text editor, with undo history kept by the app (remedy b).**
No fallback was needed. The implementation is `src/MDEditor.App/MarkdownEditor.cs`.

## How it works

* `Document.UndoLimit = 0`. RichEdit records colour changes in its own history, so that history is switched off.
  The app diffs the control's text after each change and records `(offset, removed, inserted)` in `UndoStack`, merging typing.
  Format commands, Replace All and paste are single entries.
* Text crosses the boundary in one place. RichEdit's `\r` maps 1:1 to `\n`, so offsets are preserved.
  The paragraph mark the control always keeps at the end is not part of the document.
* Colouring runs in the background. The tokenizer produces one style byte per UTF-16 unit; the UI thread applies only the
  differences between desired and applied styles, in and around the viewport, in slices of at most about 6 ms.
* All RichEdit formatting shortcuts are intercepted by a window-level key table. Enter, Tab, Shift+Enter and paste are handled in the app.
* Nothing is styled or re-synchronised during IME composition. The committed text is read once when composition ends.

## Coordinates (found by measurement, not documented)

The control lays out its whole content inside its ScrollViewer. `GetRect` and `GetRangeFromPoint` therefore use *content*
coordinates: `y(line 0) = 0`, the value does not change with the scroll offset, and viewport position `y` is content position
`y + VerticalOffset`. `PointOptions.NoVerticalScroll` must not be relied on.

## Gates

Measured on Windows 11 build 26300, x64, Windows App SDK 2.6.5, 5,000 lines / 279 kB.

| Gate | Result | Evidence |
| --- | --- | --- |
| G1 IME | Pass with Microsoft Pinyin; Japanese not run | Real key events into a real window: Space commits a candidate inside a list; Enter commits raw letters without continuing the list; the next Enter continues it; Esc cancels; a multi-syllable commit is one undo step. Candidate-window placement was not checked |
| G2 Undo not polluted | Pass | Smoke test: after colouring settles, one undo removes the typed text; a Format command and Replace All are one step; no extra steps remain |
| G3 Typing cost | Pass | UI-thread work per keystroke across runs: median 2–5 ms, p95 5–11 ms. Longest colouring slice 8 ms. Load plus first colouring 0.8–0.9 s |
| G4 Line geometry | Pass | Round trip of 37.0, 120.5 and 301.25 on a 400-line file with long wrapped lines reads back within 0.01 line; bottom pins; preview sync lands within 2 px at 100 % and 150 % |
| G5 UTF-16 offsets | Pass | Smoke test and unit tests with CJK and surrogate pairs |
| G6 Plain text | Pass by construction | Paste handler reads only the plain-text clipboard format; `ClipboardCopyFormat=PlainText` |

## Known limit

RichEditBox has no API for caret colour, so the caret keeps the system colour rather than the accent colour.

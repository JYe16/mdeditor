# MDEditor

[简体中文](README.zh-CN.md)

A native macOS Markdown editor: **write on the left, see it rendered on the right.** Written in Swift
(SwiftUI + AppKit); the preview runs in a WKWebView and every rendering library is bundled, so it works
**completely offline**. macOS 13+, universal binary (Apple silicon + Intel).

> **Windows:** a native WinUI 3 version is planned in [`windows/`](windows/) — not available yet.

## Install

### Homebrew

```bash
brew tap JYe16/tap
brew trust --tap JYe16/tap        # recent Homebrew requires explicit trust for third-party taps
brew install --cask mdeditor
```

### DMG

Download `MDEditor-<version>.dmg` from the [Releases](../../releases) page, open it and drag **MDEditor**
into Applications.

### First launch

MDEditor is **ad-hoc signed but not notarized** (there is no paid Apple Developer account behind it).
The Homebrew cask clears the download quarantine flag for you. If you installed from the DMG and macOS refuses to
open the app the first time, either:

- go to System Settings → Privacy & Security, scroll down and click **Open Anyway** next to MDEditor, or
- run once in Terminal: `xattr -dr com.apple.quarantine /Applications/MDEditor.app`

## Features

- **Split live preview** (default), or editor-only / preview-only: `⌘1` `⌘2` `⌘3`, or the toolbar buttons
- **Scroll sync aligned by source line:** scroll either side and the other follows; clicking a `#anchor` link in the
  preview also jumps the editor
- GitHub-flavored Markdown: tables, task lists, strikethrough, autolinks, `> [!NOTE]` alerts, YAML front matter
- Syntax highlighting in code blocks (43 languages, including Swift, Python, LaTeX, MATLAB, Julia…)
- Math (KaTeX): inline `$E=mc^2$` and display `$$ … $$`; prices such as `$5 and $10` are not mistaken for math
- Editor syntax coloring (headings, bold/italic, code, links, lists, quotes…), follows light / dark mode
- Pressing Return continues lists / tasks / quotes (Return on an empty item ends the list); `Tab` / `⇧Tab` indent lists
- Input-method friendly: nothing is re-highlighted or re-rendered while you are composing (pinyin, kana…)
- Find & replace (`⌘F`), print / export PDF (`⌘P`), zoom (`⌘+` `⌘-` `⌘0`), word count and reading time
  (CJK counted per character)
- Relative image paths resolve against the document's folder; external links open in your default browser;
  relative `.md` links open in MDEditor
- Safe: the preview is sanitized with DOMPurify, so `<script>` etc. in a document never runs
- UI languages: English and Simplified Chinese (follows the system)

Not supported yet: Mermaid diagrams, footnotes, emoji shortcodes.

### Shortcuts

| Action | Shortcut | | Action | Shortcut |
| --- | --- | --- | --- | --- |
| Bold | `⌘B` | | Heading 1 / 2 / 3 | `⌥⌘1` `⌥⌘2` `⌥⌘3` |
| Italic | `⌘I` | | Quote | `⌘'` |
| Strikethrough | `⇧⌘X` | | Bulleted list | `⇧⌘8` |
| Inline code | `⌥⌘C` | | Task list | `⌥⌘T` |
| Link | `⌘K` | | Code block | `⌥⌘K` |

## Build from source

Needs Xcode (or the Command Line Tools). No Node required: the rendering libraries are checked in under
`Resources/preview/vendor/`.

```bash
./scripts/build.sh                     # build/MDEditor.app (universal, ad-hoc signed)
./scripts/make-dmg.sh                  # build/MDEditor-<version>.dmg
VERSION=1.1.0 ./scripts/make-dmg.sh    # choose the version number
./scripts/test.sh                      # headless regression tests for the editing commands (39)
```

For development, `swift build && swift run` from the repository root also works (the preview page is then read from
`Resources/preview`).

## Repository layout

```
Package.swift
Sources/MDEditor/
  MDEditorApp.swift          App entry, menu commands (View / Format / Find / Print)
  MarkdownDocument.swift     FileDocument: reads/writes .md (UTF-8, common legacy encodings)
  ContentView.swift          Main UI: toolbar switcher, status bar, word count
  SplitEditorView.swift      Split container: editor + WKWebView + scroll sync + render scheduling
  MarkdownTextView.swift     NSTextView subclass: list continuation, Tab, formatting commands
  MarkdownHighlighter.swift  Editor syntax coloring
Resources/
  Info.plist                 .md file-type association
  preview/                   Preview page: index.html, app.js (incremental render), vendor/ (marked, DOMPurify, highlight.js, KaTeX…)
  *.lproj/                   Localization
  AppIcon.icns, AppIcon.png
scripts/                     build.sh, make-dmg.sh, make-icon.swift, test.sh, release helpers
Tests/EditingTests/          Headless regression tests
windows/                     Windows (WinUI 3) version — in preparation
examples/sample.md           Feature demo document
```

Third-party libraries and their licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Implementation notes

- The editor uses **TextKit 1**: scroll sync needs exact line geometry. The text container width is maintained by
  `MarkdownTextView` with a lower bound, because TextKit 1 lays out forever (100 % CPU) when the container is no wider
  than its own padding — which happens while a window is still being laid out.
- The preview renders **incrementally per top-level block**: an edit rebuilds only the affected blocks, the rest of the
  DOM stays put, so there is no flicker and images don't reload.
- Scroll sync between the page and the app uses **source line numbers** as the coordinate, not scroll percentages, so it
  stays aligned in long documents.

## Contributing

Issues and pull requests are welcome. Please run `./scripts/test.sh` before sending a change that touches editing
behavior. Releases are described in [docs/RELEASING.md](docs/RELEASING.md).

## License

[MIT](LICENSE) © 2026 JYe16. Bundled third-party libraries keep their own licenses, see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

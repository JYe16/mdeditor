# MDEditor for Windows

Native WinUI 3 version of MDEditor: Markdown editor on the left, live preview on the right, scroll-synced by source line.
Double-click a word in the preview and the editor selects that word's Markdown source: it scrolls only if the line is
near or past an edge, and the preview stays where it is. (With the editor hidden, double-click just selects the word.)
The preview is the same page the macOS app uses (`Resources/preview`), hosted in WebView2.

Requires Windows 10 version 2004 (build 19041) or later, x64 or ARM64, and the Microsoft Edge WebView2 Runtime
(preinstalled on Windows 11; the installer offers to get it if missing). Nothing else: .NET and the Windows App SDK are bundled.

## Install

Run `install.exe` (or `MDEditor-<version>-win-<arch>-setup.exe`). The wizard asks whether to install for you only
(no administrator rights needed) or for all users, and lets you choose any folder.

The installer is not code-signed yet, so SmartScreen may say "Windows protected your PC": choose **More info → Run anyway**.

MDEditor is added to "Open with" for Markdown files. Windows reserves the default-app choice for the user: to make it the
default, use **Settings → Apps → Default apps → MDEditor**. The last installer page has a shortcut to that page.

Unattended install, for package managers:

```bash
install.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /DIR="D:\Apps\MDEditor"
```

Uninstall from **Settings → Apps**. Your documents and `%LOCALAPPDATA%\MDEditor\settings.json` are kept.
The portable `.zip` needs no installation: unzip it anywhere and run `MDEditor.exe`.

## Build

You need the .NET 10 SDK and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php) (found through `$env:ISCC`,
`PATH` or its default folder). Run these from `windows/`:

```bash
./scripts/build.ps1
```

```bash
./scripts/publish.ps1
```

```bash
./scripts/make-installer.ps1
```

`artifacts/` then holds `install.exe` (x64), both per-architecture installers, the portable zips and `SHA256SUMS.txt`.
`MDEditor.Core` and its tests need no Windows APIs: `dotnet test tests/MDEditor.Core.Tests` also works on macOS and Linux.

## Command line

```bash
MDEditor.exe [file ...]
```

Each file opens in its own window, in the already-running process when there is one. Diagnostic switches:

| Switch | Effect |
| --- | --- |
| `--smoke-test[=report.txt]` | Runs a real window through the checks that cannot run headless; exit code 0 or 1. With a report path it also writes `report.txt.jump-*.png` screenshots of the editor after a jump from the preview |
| `--lang=zh-CN` | Overrides the display language for this run |
| `--theme=dark` / `--theme=light` | Overrides the system theme for this run |

## Layout

| Path | Contents |
| --- | --- |
| `src/MDEditor.Core` | UI-free logic: editing commands, tokenizer, codec, link policy, undo, search |
| `src/MDEditor.App` | WinUI 3 app: windows, the RichEditBox wrapper, the WebView2 host |
| `tests/MDEditor.Core.Tests` | xUnit, including all 39 ported macOS editing tests |
| `installer/MDEditor.iss` | Inno Setup wizard |
| `docs/editor-decision.md` | Why the editor is built the way it is, with evidence |
| `REPORT.md` | Status, deviations from `HANDOFF.md`, and what has not been verified |

# MDEditor for Windows

Native WinUI 3 version of MDEditor: Markdown editor on the left, live preview on the right, scroll-synced by source line.
Double-click a word in the preview and the editor selects that word's Markdown source: it scrolls only if the line is
near or past an edge, and the preview stays where it is. (With the editor hidden, double-click just selects the word.)
The preview is the same page the macOS app uses (`Resources/preview`), hosted in WebView2.

Requires Windows 10 version 2004 (build 19041) or later, x64 or ARM64, and the Microsoft Edge WebView2 Runtime
(preinstalled on Windows 11; the installer offers to get it if missing). Nothing else: .NET and the Windows App SDK are bundled.

## Install

Run `install.exe` (or `MDEditor-<version>-win-<arch>-setup.exe`). The wizard asks for the install scope, then lets you
choose any folder:

* **All users** (recommended, needs administrator rights). Windows then lists MDEditor in **Settings → Apps → Default
  apps**, where you can make it the default for `.md`. The last wizard page has a shortcut to that page.
* **Just me** (no administrator rights). MDEditor is added to "Open with" (right-click a Markdown file → **Open with**),
  but Windows does not list a per-user install in Settings → Default apps. Setup says so when it finishes. To get
  MDEditor into that list later, run setup again and choose all users.

Windows reserves the default-app choice for the user, so setup never sets it itself.

The installer is not code-signed yet, so SmartScreen may say "Windows protected your PC": choose **More info → Run anyway**.

Unattended install, for package managers. Pass the scope explicitly:

```bash
install.exe /VERYSILENT /SUPPRESSMSGBOXES /ALLUSERS /DIR="D:\Apps\MDEditor"
install.exe /VERYSILENT /SUPPRESSMSGBOXES /CURRENTUSER /DIR="D:\Apps\MDEditor"
```

The first one needs an elevated prompt (administrator rights), the second does not.

Uninstall from **Settings → Apps** (an all-users install asks for administrator rights). Your documents and
`%LOCALAPPDATA%\MDEditor\settings.json` are kept.
The portable `.zip` needs no installation: unzip it anywhere and run `MDEditor.exe`. It registers nothing, so to open a
Markdown file with it use **Open with → Choose another app** and browse to `MDEditor.exe`.

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

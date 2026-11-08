; MDEditor installer. Built by scripts/make-installer.ps1, which passes the defines below.
; Wizard: language → install scope (just me / all users) → licence → free choice of folder → tasks → install.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (see scripts/make-installer.ps1)
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef BaseName
  #define BaseName "MDEditor-" + AppVersion + "-win-" + Arch + "-setup"
#endif
#if Arch == "arm64"
  #define Allowed "arm64"
#else
  #define Allowed "x64compatible"
#endif

#define Publisher "JYe16"
#define AppExe "MDEditor.exe"
#ifndef ProgId
  #define ProgId "MDEditor.Markdown"
#endif
; Kept stable across versions: it is how an upgrade finds the previous installation.
#ifndef AppGuid
  #define AppGuid "{{7C1E5A32-9D44-4B7E-8E0B-52C3F1A96D10}"
#endif

[Setup]
AppId={#AppGuid}
AppName=MDEditor
AppVersion={#AppVersion}
AppVerName=MDEditor {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/JYe16/mdeditor
AppSupportURL=https://github.com/JYe16/mdeditor/issues
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoProductName=MDEditor
VersionInfoDescription=MDEditor Setup
DefaultDirName={autopf}\MDEditor
DefaultGroupName=MDEditor
; The folder page is always shown, including for upgrades, so the location is always the user's choice.
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed={#Allowed}
ArchitecturesInstallIn64BitMode={#Allowed}
MinVersion=10.0.19041
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
AppMutex=MDEditor.SingleInstance
WizardStyle=modern
SetupIconFile=..\src\MDEditor.App\Assets\MDEditor.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=MDEditor
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#BaseName}
; "max" rather than "ultra64": the 32-bit compiler runs out of memory on the large dictionary.
Compression=lzma2/max
SolidCompression=yes
; Code signing hook (v1 ships unsigned): add SignTool=<name> here once a certificate exists.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.AssocTask=Add MDEditor to "Open with" for Markdown files (.md, .markdown, .mdown, .mkd, .mdwn)
chinesesimplified.AssocTask=将 MDEditor 添加到 Markdown 文件（.md、.markdown、.mdown、.mkd、.mdwn）的“打开方式”
english.DefaultAppsRun=Open Default Apps settings to make MDEditor the default for Markdown files
chinesesimplified.DefaultAppsRun=打开“默认应用”设置，将 MDEditor 设为 Markdown 文件的默认程序
english.AssocDescription=Markdown Document
chinesesimplified.AssocDescription=Markdown 文档
english.NotWritable=This folder cannot be written to with your current permissions:%n%n%1%n%nChoose another folder, or go back to the start of setup and install for all users.
chinesesimplified.NotWritable=当前权限无法写入此文件夹：%n%n%1%n%n请选择其他文件夹，或重新运行安装程序并选择“为所有用户安装”。
english.AssocBlocked=MDEditor is installed and works normally, but security software blocked adding it to "Open with".%n%nTo open Markdown files with it, right-click a file, choose Open with, then Choose another app, and browse to MDEditor.exe in the install folder.
chinesesimplified.AssocBlocked=MDEditor 已安装，可以正常使用，但安全软件阻止了将其添加到“打开方式”。%n%n如需用它打开 Markdown 文件，请右键文件，选择“打开方式”→“选择其他应用”，然后浏览到安装目录中的 MDEditor.exe。
english.WebView2Prompt=MDEditor's preview needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.%n%nDownload and install it from Microsoft now? (about 2 MB, then it downloads the runtime)
chinesesimplified.WebView2Prompt=MDEditor 的预览功能需要 Microsoft Edge WebView2 运行库，这台电脑尚未安装。%n%n现在从微软下载并安装吗？（引导程序约 2 MB，随后会下载运行库）
english.WebView2Failed=WebView2 could not be installed. Editing still works; the preview will show a download link.
chinesesimplified.WebView2Failed=WebView2 安装失败。编辑功能不受影响；预览区会显示下载链接。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "assoc"; Description: "{cm:AssocTask}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MDEditor"; Filename: "{app}\{#AppExe}"; AppUserModelID: "JYe16.MDEditor"
Name: "{autodesktop}\MDEditor"; Filename: "{app}\{#AppExe}"; AppUserModelID: "JYe16.MDEditor"; Tasks: desktopicon

[Registry]
; ProgID. Windows protects the default-app choice, so this only makes MDEditor an "Open with" candidate.
; noerror: security software (Huorong, for one) may refuse these writes. That must not stop the install; see AssocWasBlocked.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "{cm:AssocDescription}"; Flags: noerror uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mdown\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mkd\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mdwn\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: noerror uninsdeletevalue; Tasks: assoc

Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "MDEditor"; Flags: noerror uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: noerror; Tasks: assoc

; Listing in Settings → Default apps.
Root: HKA; Subkey: "Software\MDEditor"; Flags: noerror uninsdeletekeyifempty; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "MDEditor"; Flags: noerror uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Markdown editor with live preview"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "{#ProgId}"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "{#ProgId}"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "{#ProgId}"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "{#ProgId}"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\MDEditor\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdwn"; ValueData: "{#ProgId}"; Flags: noerror; Tasks: assoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "MDEditor"; ValueData: "Software\MDEditor\Capabilities"; Flags: noerror uninsdeletevalue; Tasks: assoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,MDEditor}"; Flags: nowait postinstall skipifsilent
Filename: "ms-settings:defaultapps"; Description: "{cm:DefaultAppsRun}"; Flags: shellexec nowait postinstall skipifsilent unchecked; Tasks: assoc

[UninstallDelete]
; The browser cache is disposable. settings.json next to it is the user's and is kept.
Type: filesandordirs; Name: "{localappdata}\MDEditor\WebView2"

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function HasVersion(Root: Integer; const Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
begin
  Result :=
    HasVersion(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    HasVersion(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    HasVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client);
end;

{ Walks up to the nearest existing folder and tries to create a file in it. }
function CanWriteTo(Path: String): Boolean;
var
  Probe: String;
begin
  Path := RemoveBackslashUnlessRoot(Path);
  while (not DirExists(Path)) and (Length(ExtractFileDir(Path)) < Length(Path)) do
    Path := ExtractFileDir(Path);
  Probe := AddBackslash(Path) + '.mdeditor-setup-' + IntToStr(Random(2147483647)) + '.tmp';
  Result := SaveStringToFile(Probe, '', False);
  if Result then
    DeleteFile(Probe);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and (not CanWriteTo(WizardDirValue)) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('NotWritable'), [WizardDirValue]), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InstallWebView2;
var
  Page: TDownloadWizardPage;
  ResultCode: Integer;
begin
  Page := CreateDownloadPage(SetupMessage(msgWizardPreparing), '', nil);
  Page.Clear;
  Page.Add(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '');
  Page.Show;
  try
    try
      Page.Download;
      if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE,
                  ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
        SuppressibleMsgBox(CustomMessage('WebView2Failed'), mbInformation, MB_OK, IDOK);
    except
      SuppressibleMsgBox(CustomMessage('WebView2Failed'), mbInformation, MB_OK, IDOK);
    end;
  finally
    Page.Hide;
  end;
end;

function AssocWasBlocked: Boolean;
var
  Command: String;
begin
  Result := WizardIsTaskSelected('assoc') and
    not RegQueryStringValue(HKA, 'Software\Classes\{#ProgId}\shell\open\command', '', Command);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and AssocWasBlocked then
  begin
    Log('Open with registration was refused by the system or security software.');
    SuppressibleMsgBox(CustomMessage('AssocBlocked'), mbInformation, MB_OK, IDOK);
  end;
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not WebView2Installed) then
    if MsgBox(CustomMessage('WebView2Prompt'), mbConfirmation, MB_YESNO) = IDYES then
      InstallWebView2;
end;

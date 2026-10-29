#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef AppUrl
  #define AppUrl "https://github.com/PolyMount/PolyMount"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the staged publish directory
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef Platform
  #define Platform "win-x64"
#endif
; The plugin contract this package carries, passed by the build. Only ever used to say, beside a
; plugin this setup does not ship, that the two cannot run together — the page still lets the
; reader keep or drop the file, because that is the only decision it takes.
#ifndef PluginApi
  #define PluginApi ""
#endif

[Setup]
AppId={{91D38E86-5EF4-4512-BC23-4B1481A758B7}
AppName=PolyMount
AppVersion={#AppVersion}
AppVerName=PolyMount {#AppVersion}
AppPublisher=PolyMount
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=PolyMount contributors. Licensed under the GNU GPL v3 or later.
DefaultDirName={autopf}\PolyMount
DefaultGroupName=PolyMount
SetupIconFile=..\assets\app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
; An installation directory is the reader's choice, also when this is an upgrade of an existing
; installation: Inno's default ("auto") hides the page in exactly that case.
DisableDirPage=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=PolyMount-{#AppVersion}-{#Platform}-Setup
UninstallDisplayIcon={app}\Tray\PolyMount.Tray.exe
CloseApplications=yes
RestartApplications=no
; The two behavioural tasks are checked on every install, an upgrade included: the previous
; selection is not reused, because Inno's checkedonce flag unchecks a task that it finds on an
; earlier copy of the program, and an upgrade that silently drops "start at sign-in" (while the
; Run entry it wrote before is still there) tells the reader something untrue.
UsePreviousTasks=no
SetupLogging=yes
VersionInfoCompany=PolyMount
VersionInfoDescription=PolyMount Setup
VersionInfoProductName=PolyMount
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCopyright=PolyMount contributors. GNU GPL v3 or later.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "overlays"; Description: "{cm:OriginBadges}"
Name: "useraccount"; Description: "{cm:UserAccount}"; Flags: unchecked
; The bundled source plugins are not tasks: they get a page of their own (see InitializeWizard),
; because "what this installation can mount" is a different question from "how should it start".

[Files]
Source: "{#PayloadDir}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
; The MCP server is started by an agent rather than by Windows, so there is no service, shortcut
; or run entry for it - only the executable and the runtime it carries.
Source: "{#PayloadDir}\Mcp\*"; DestDir: "{app}\Mcp"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Shell\PolyMount.Shell.dll"; DestDir: "{app}\Shell"; Flags: ignoreversion restartreplace; Check: ShellDllNeedsCopy
; The build's fingerprint: a hash of the source the DLL was compiled from, written by
; Build-Shell.ps1. The installed copy is compared with this rather than with the DLL, whose bytes
; differ between builds of identical source (PE timestamp, and the CI's timestamped signature).
Source: "{#PayloadDir}\Shell\build-id.txt"; DestDir: "{app}\Shell"; Flags: ignoreversion
Source: "{#PayloadDir}\Shell\build-id.txt"; Flags: dontcopy
Source: "{#PayloadDir}\Shell\icons\badge-*.ico"; DestDir: "{app}\Shell\icons"; Flags: ignoreversion
Source: "Install-Host.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "Install-Host.ps1"; Flags: dontcopy
Source: "install.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Dependencies\winfsp.msi"; Flags: dontcopy
Source: "{#PayloadDir}\Dependencies\vc_redist.x64.exe"; Flags: dontcopy
; The design notes are part of the working repository; a tree published without them has no docs
; to ship, and Inno aborts a Source that matches nothing. The build decides.
#ifdef DocsPresent
Source: "{#PayloadDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{group}\PolyMount"; Filename: "{app}\Tray\PolyMount.Tray.exe"
Name: "{group}\{cm:UninstallProgram,PolyMount}"; Filename: "{uninstallexe}"

[InstallDelete]
; The badges used to be one coloured dot per palette entry ("palette-dot-N.ico"). The shell no
; longer asks for those, and leaving them behind would look like a badge set that is still live.
Type: files; Name: "{app}\Shell\icons\palette-dot-*.ico"

[Registry]
Root: HKLM; Subkey: "Software\Classes\*\shell\PolyMountLayers"; ValueType: string; ValueName: "MUIVerb"; ValueData: "PolyMount"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\*\shell\PolyMountLayers"; ValueType: string; ValueName: "SubCommands"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\*\shell\PolyMountLayers\shell\ManageObject"; ValueType: string; ValueName: ""; ValueData: "{cm:ManageObject}"
Root: HKLM; Subkey: "Software\Classes\*\shell\PolyMountLayers\shell\ManageObject"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Document"
Root: HKLM; Subkey: "Software\Classes\*\shell\PolyMountLayers\shell\ManageObject\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Tray\PolyMount.Tray.exe"" --manage-cache ""%1"""
Root: HKLM; Subkey: "Software\Classes\Directory\shell\PolyMountLayers"; ValueType: string; ValueName: "MUIVerb"; ValueData: "PolyMount"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Directory\shell\PolyMountLayers"; ValueType: string; ValueName: "SubCommands"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\Directory\shell\PolyMountLayers\shell\ManageObject"; ValueType: string; ValueName: ""; ValueData: "{cm:ManageObject}"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\PolyMountLayers\shell\ManageObject"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Document"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\PolyMountLayers\shell\ManageObject\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Tray\PolyMount.Tray.exe"" --manage-cache ""%1"""
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PolyMountTray"; ValueData: """{app}\Tray\PolyMount.Tray.exe"""; Tasks: autostart; Flags: uninsdeletevalue
; Overlay handlers: one per badge, never a literal count. Build-Shell.ps1 draws one icon per glyph
; in src\PolyMount.Core\Core\OverlayGlyphs.inc and writes the number beside them, so adding a badge
; is the only edit either side needs. (The count cannot ride on the compiler command line: ISCC's
; /D values are strings, and #for needs an integer.)
; The class id carries the slot in its first field -- Overlay.cpp reads it back as
; base.Data1 + slot -- and ISPP has no hex formatter, so the index is appended in decimal, which
; stays a valid GUID only while it is one digit. Hence the guard below, and Core's ten-entry cap.
#define SlotsFile AddBackslash(PayloadDir) + "Shell\badge-slots.iss"
#if !FileExists(SlotsFile)
  #error Missing badge-slots.iss beside the shell icons: stage the payload with scripts\Build-Installer.ps1
#endif
#include SlotsFile
#if BadgeSlots > 10
  #error More than ten overlay slots needs the class id spelled in hex; see the comment above
#endif
#sub OverlayEntry
Root: HKLM; Subkey: "Software\Classes\CLSID\{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\Shell\PolyMount.Shell.dll"; Tasks: overlays; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Both"; Tasks: overlays
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\   PolyMount {#Index}"; ValueType: string; ValueName: ""; ValueData: "{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}"; Tasks: overlays; Flags: uninsdeletekey
#endsub
#define Index 0
#for {Index = 0; Index < BadgeSlots; Index++} OverlayEntry

[Run]
; Started through Explorer rather than as a child of Setup. Setup runs with RedirectionGuard (the
; Inno 6.3+ default) and every process it starts inherits that mitigation; an enforcing process
; cannot follow a junction created by the service account, so a tray started here would be unable
; to open the mount points it manages until the next sign-in. Explorer is not mitigated.
Filename: "{win}\explorer.exe"; Parameters: """{app}\Tray\PolyMount.Tray.exe"""; Description: "{cm:LaunchProgram,PolyMount}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Explorer is deliberately not restarted from here: the new Explorer would inherit the same
; mitigation and could not open the mounts. The finished page says how to restart it.

[CustomMessages]
english.ManageObject=Manage cache boundary...
english.PluginSvn=Subversion (SVN) repositories
english.PluginSmb=Folders shared by other machines (SMB)
english.PluginTxFShim=Programs that need transactional opens (opt-in per directory)
chinesesimplified.ManageObject=管理缓存分界线...
chinesesimplified.PluginSvn=Subversion (SVN) 仓库
chinesesimplified.PluginSmb=其他机器共享的文件夹（SMB）
chinesesimplified.PluginTxFShim=需要事务打开的程序（按目录启用）
english.PluginPageCaption=Plugins
english.PluginPageDescription=Choose which plugins this installation offers. Plugins already installed that this setup does not ship are listed after them; clear one to remove it.
english.PluginPageSubCaption=
english.PluginExtra=%1 (installed separately)
english.PluginIncompatible=  — incompatible: built against plugin contract %1, this installation provides %2
chinesesimplified.PluginIncompatible=  ——不兼容：按插件契约 %1 编译，本安装提供 %2
chinesesimplified.PluginPageCaption=插件
chinesesimplified.PluginPageDescription=选择这次安装提供哪些插件。已经装好、但不随本安装包分发的插件列在后面；取消勾选即删除。
chinesesimplified.PluginPageSubCaption=
chinesesimplified.PluginExtra=%1（单独安装）
english.AutoStart=Start the tray at Windows sign-in
english.OriginBadges=Show source badges in Explorer
english.UserAccount=Run the service as this Windows account (reuses your saved SVN sign-in)
english.ServiceFailure=PolyMount service setup failed. See the installer log and retry setup.
english.WinFspFailure=WinFsp installation failed.
english.RuntimeFailure=Visual C++ runtime installation failed.
english.RestartExplorerHint=If the source badges do not appear, restart Explorer from Task Manager (right-click Windows Explorer and choose Restart) or sign out and back in.
english.RemoveData=Also delete the PolyMount configuration and cached data?%n%nAnswer No to keep them for a later installation.
chinesesimplified.AutoStart=登录 Windows 时启动托盘
chinesesimplified.OriginBadges=在资源管理器中显示来源角标
chinesesimplified.UserAccount=以当前 Windows 账户运行服务（复用已保存的 SVN 登录）
chinesesimplified.ServiceFailure=PolyMount 服务安装失败，请查看安装日志并重新安装。
chinesesimplified.WinFspFailure=WinFsp 安装失败。
chinesesimplified.RuntimeFailure=Visual C++ 运行库安装失败。
chinesesimplified.RestartExplorerHint=如果来源角标没有出现，请在任务管理器里重启资源管理器（右键「Windows 资源管理器」→「重新启动」），或注销后重新登录。
chinesesimplified.RemoveData=同时删除 PolyMount 的配置与缓存的数据？%n%n选择「否」会保留它们，以便日后重新安装。

[Code]
{ The plugins this setup ships and offers. The wizard's page decides which of them the installed
  configuration names, and the script is told both the selection and the full list so it can also
  take an unselected one back out. Adding a plugin means a branch in the two tables below, a line
  in PluginLabel, and incrementing the count. }
const
  PluginCount = 3;

var
  PluginPage: TInputOptionWizardPage;
  { The plugins found in an existing installation that this setup does not ship, in the order the
    page lists them after its own. Discovered once, when the page is first shown -- the directory
    is only settled by then. }
  ExtraFiles: TStringList;
  ExtrasLoaded: Boolean;
  RestartExplorerAfterInstall: Boolean;
  ReplaceShellDll: Boolean;

function InitializeSetup(): Boolean;
begin
  { The Shell DLL is installed whether or not PrepareShellExtensionDll gets as far as comparing
    it (a silent install, the overlay task unchecked, no earlier registration), so copying is the
    default and only an identical copy turns it off. }
  ReplaceShellDll := True;
  ExtraFiles := TStringList.Create;
  Result := True;
end;

function ShellDllNeedsCopy(): Boolean;
begin
  Result := ReplaceShellDll;
end;

{ The shell extension DLL is loaded by Explorer once the overlay handlers are registered. A loaded
  image cannot be overwritten, so on an upgrade the new DLL is put in place by renaming the old
  one; Explorer keeps running the renamed file until it restarts. The restart is left to the tray:
  Setup runs with RedirectionGuard and every process it starts inherits it, and an enforcing
  Explorer cannot follow the junctions this product mounts. Nothing is restarted when no handler
  was registered (a fresh install) -- the finished page says how to make the colours appear. }
function OverlayRegistered(): Boolean;
begin
  Result := RegValueExists(HKLM,
    'Software\Classes\CLSID\{7d619ac0-dbe2-4f33-9a11-6cc2c8329001}\InprocServer32', '');
end;

{ Byte-for-byte comparison through the system's own tool: the script has no hash function, and
  this runs once per install rather than on any path a reader waits for. }
function SameFile(Left, Right: String): Boolean;
var
  Code: Integer;
begin
  Result := False;
  if not FileExists(Left) or not FileExists(Right) then exit;
  if Exec(ExpandConstant('{sys}\fc.exe'), '/b "' + Left + '" "' + Right + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code = 0;
end;

procedure PrepareShellExtensionDll();
var
  dll, installedId, stagedId, previous, marker: String;
begin
  RestartExplorerAfterInstall := False;
  ReplaceShellDll := True;
  if WizardSilent() then exit;
  if not WizardIsTaskSelected('overlays') then
  begin
    { The registrations are removed by this install, so a pending hand-off would restart Explorer
      for an extension it will no longer load. }
    DeleteFile(ExpandConstant('{app}\Shell\restart-explorer'));
    exit;
  end;
  if not OverlayRegistered() then exit;
  dll := ExpandConstant('{app}\Shell\PolyMount.Shell.dll');
  if not FileExists(dll) then
  begin
    { A destination with no copy there means this upgrade moved the installation: the Explorer
      that is running loaded the extension from the old directory, so the hand-off applies even
      when the bytes are the same. }
    marker := ExpandConstant('{app}\Shell\restart-explorer');
    ForceDirectories(ExtractFileDir(marker));
    SaveStringToFile(marker, 'moved', False);
    RestartExplorerAfterInstall := True;
    exit;
  end;
  { The installed build-id records what the extension Explorer is running was compiled from. When
    the new package's id is the same, nothing that Explorer loads has changed: no copy (a loaded
    image cannot be overwritten, identical or not) and no restart. A missing id means an
    installation older than this file, which is treated as a change -- one restart, after which
    later upgrades compare correctly. }
  installedId := ExpandConstant('{app}\Shell\build-id.txt');
  stagedId := '';
  try
    ExtractTemporaryFile('build-id.txt');
    stagedId := ExpandConstant('{tmp}\build-id.txt');
  except
    stagedId := '';
  end;
  if (stagedId <> '') and FileExists(installedId) and SameFile(installedId, stagedId) then
  begin
    ReplaceShellDll := False;
    { A marker left by an earlier install whose tray never started is not news now: nothing that
      Explorer loads has changed this time either. Removing it is what keeps a reinstall from
      asking again. }
    DeleteFile(ExpandConstant('{app}\Shell\restart-explorer'));
    exit;
  end;
  previous := dll + '.old';
  DeleteFile(previous);
  if not RenameFile(dll, previous) then exit;
  marker := ExpandConstant('{app}\Shell\restart-explorer');
  ForceDirectories(ExtractFileDir(marker));
  SaveStringToFile(marker, 'replaced', False);
  RestartExplorerAfterInstall := True;
end;

function PluginLabel(Index: Integer): String;
begin
  case Index of
    0: Result := ExpandConstant('{cm:PluginSvn}');
    1: Result := ExpandConstant('{cm:PluginSmb}');
    2: Result := ExpandConstant('{cm:PluginTxFShim}');
  else
    Result := '';
  end;
end;

procedure InitializeWizard();
var
  I: Integer;
begin
  { Its own page rather than checkboxes on the tasks page: picking what a mount can read is a
    different decision from picking start-up and Explorer behaviour, and sharing one page made
    the plugins look like one more optional extra. Every bundled plugin starts checked -- an
    installation with none of them has no source to mount -- and a silent install keeps them. }
  PluginPage := CreateInputOptionPage(wpSelectTasks,
    ExpandConstant('{cm:PluginPageCaption}'),
    ExpandConstant('{cm:PluginPageDescription}'),
    ExpandConstant('{cm:PluginPageSubCaption}'),
    False, False);
  for I := 0 to PluginCount - 1 do
  begin
    PluginPage.Add(PluginLabel(I));
    PluginPage.Values[I] := True;
  end;
end;

{ Declared before the page that uses it: Pascal Script reads top to bottom, and the plugin page
  asks the host script what it found long before the calls that install anything are defined. }
function HostActionTo(Action: String; Installed: Boolean; OutputFile: String): Boolean; forward;

function JoinExtraPlugins(SelectedOnly: Boolean): String;
var
  I, Index: Integer;
begin
  Result := '';
  for I := 0 to ExtraFiles.Count - 1 do
  begin
    Index := PluginCount + I;
    if (not SelectedOnly) or (PluginPage = nil) or PluginPage.Values[Index] then
    begin
      if Result <> '' then Result := Result + ';';
      Result := Result + ExtraFiles[I];
    end;
  end;
end;

{ The major part of a contract version: the number that has to match for a plugin to load at all.
  A plugin built against another major cannot run here; a different minor can. }
function ContractMajor(Version: String): String;
var
  P: Integer;
begin
  P := Pos('.', Version);
  if P > 0 then Result := Copy(Version, 1, P - 1) else Result := Version;
end;

procedure LoadExtraPlugins();
var
  I, Index: Integer;
  OutputFile: String;
  Lines: TArrayOfString;
  Parts: TStringList;
  Caption: String;
begin
  { Once: the page is rebuilt every time it is shown, and appending again on a trip back through
    the wizard would list the same plugin twice. }
  if ExtrasLoaded then exit;
  ExtrasLoaded := True;
  if WizardSilent() then exit;

  OutputFile := ExpandConstant('{tmp}\extra-plugins.txt');
  ExtractTemporaryFile('Install-Host.ps1');
  if not HostActionTo('Discover', False, OutputFile) then exit;
  if not FileExists(OutputFile) then exit;
  if not LoadStringsFromFile(OutputFile, Lines) then exit;

  Parts := TStringList.Create;
  Parts.Delimiter := #9;
  Parts.StrictDelimiter := True;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Trim(Lines[I]) = '' then continue;
    Parts.DelimitedText := Lines[I];
    if Parts.Count < 3 then continue;
    if ActiveLanguage = 'chinesesimplified' then Caption := Parts[2] else Caption := Parts[1];
    { A plugin built against another major cannot run under what this setup installs, and the
      row says so in words. The box stays checked and stays clearable: this page decides whether
      the file is kept, not whether it will load — that answer belongs to the plugin page in the
      tray, which can see the whole picture. }
    if ('{#PluginApi}' <> '') and (Parts.Count >= 4) and (Parts[3] <> '') and
       (ContractMajor(Parts[3]) <> ContractMajor('{#PluginApi}')) then
      Caption := Caption + FmtMessage(CustomMessage('PluginIncompatible'), [Parts[3], '{#PluginApi}']);
    Index := PluginCount + ExtraFiles.Count;
    ExtraFiles.Add(Parts[0]);
    PluginPage.Add(FmtMessage(CustomMessage('PluginExtra'), [Caption]));
    { Already installed and working: keeping it is what leaving it alone means, so it starts
      checked. Clearing the box is the only thing that ever removes it. }
    PluginPage.Values[Index] := True;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { A plugin someone installed by hand joins this page rather than getting one of its own: "what
    this installation can mount" is one question, and the answer was incomplete before. }
  if (PluginPage <> nil) and (CurPageID = PluginPage.ID) then LoadExtraPlugins();
  { The colours appear only after Explorer has read the overlay list, and this installer must not
    restart it itself (see [Run]); the finished page says what to do instead. }
  if (CurPageID = wpFinished) and WizardIsTaskSelected('overlays') and not RestartExplorerAfterInstall then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      CustomMessage('RestartExplorerHint');
end;

function PluginFile(Index: Integer): String;
begin
  case Index of
    0: Result := 'PolyMount.Plugin.Svn.dll';
    1: Result := 'PolyMount.Plugin.Smb.dll';
    2: Result := 'PolyMount.Plugin.TxFShim.dll';
  else
    Result := '';
  end;
end;

function JoinPluginFiles(SelectedOnly: Boolean): String;
var
  I: Integer;
  FileName: String;
  Include: Boolean;
begin
  Result := '';
  for I := 0 to PluginCount - 1 do
  begin
    FileName := PluginFile(I);
    { A page that was never built (an unexpected early exit from the wizard) counts as "all of
      them", which is the state the page starts in. }
    Include := (FileName <> '') and ((not SelectedOnly) or (PluginPage = nil) or PluginPage.Values[I]);
    if Include then
    begin
      if Result <> '' then Result := Result + ';';
      Result := Result + FileName;
    end;
  end;
end;

function HostAction(Action: String; Installed: Boolean): Boolean;
begin
  Result := HostActionTo(Action, Installed, '');
end;

function HostActionTo(Action: String; Installed: Boolean; OutputFile: String): Boolean;
var Script, Args: String; Code, Show: Integer;
begin
  if Installed then Script := ExpandConstant('{app}\installer\Install-Host.ps1')
  else Script := ExpandConstant('{tmp}\Install-Host.ps1');
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" -Action ' + Action +
    ' -InstallDirectory "' + ExpandConstant('{app}') + '"';
  if Action = 'Discover' then
    Args := Args + ' -Managed "' + JoinPluginFiles(False) + '" -Output "' + OutputFile + '"';
  if Action = 'Install' then
  begin
    if WizardIsTaskSelected('useraccount') then Args := Args + ' -UserAccount';
    Args := Args + ' -Plugins "' + JoinPluginFiles(True) + '"';
    Args := Args + ' -Managed "' + JoinPluginFiles(False) + '"';
    { Everything the page listed is named: this setup's own plugins above, and the ones it found
      on disk here. Naming none of the latter is how the page says they were cleared, which is the
      only way a plugin somebody installed by hand ever gets removed. A silent install has no
      page and is never asked, so it says nothing and nothing is removed. }
    if not WizardSilent() then Args := Args + ' -Keep "' + JoinExtraPlugins(True) + '"';
  end;
  // Only one of these calls has something to say: the first install that asks for a service
  // account prompts for its credentials in that console. Everything else -- stopping the old
  // service, checking the files, an upgrade -- runs hidden, so Setup does not flash a black
  // window at the reader several times on its way through.
  if (Action = 'Install') and WizardIsTaskSelected('useraccount') then Show := SW_SHOW else Show := SW_HIDE;
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', Show, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  PrepareShellExtensionDll();
  ExtractTemporaryFile('Install-Host.ps1');
  if not HostAction('Stop', False) then begin Result := CustomMessage('ServiceFailure'); exit; end;
  if not FileExists(ExpandConstant('{pf32}\WinFsp\bin\winfsp-x64.dll')) then begin
    ExtractTemporaryFile('winfsp.msi');
    if not Exec(ExpandConstant('{sys}\msiexec.exe'), '/i "' + ExpandConstant('{tmp}\winfsp.msi') + '" /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin Result := CustomMessage('WinFspFailure'); exit; end;
    if (Code <> 0) and (Code <> 3010) then begin Result := CustomMessage('WinFspFailure'); exit; end;
    if Code = 3010 then NeedsRestart := True;
  end;
  ExtractTemporaryFile('vc_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin Result := CustomMessage('RuntimeFailure'); exit; end;
  if (Code <> 0) and (Code <> 3010) and (Code <> 1638) then begin Result := CustomMessage('RuntimeFailure'); exit; end;
  if Code = 3010 then NeedsRestart := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if not HostAction('Check', True) then RaiseException(CustomMessage('ServiceFailure'));
    if not HostAction('Install', True) then RaiseException(CustomMessage('ServiceFailure'));
  end;
end;

function InitializeUninstall(): Boolean;
var Data: String;
begin
  Result := HostAction('Uninstall', True);
  if not Result then begin
    MsgBox(CustomMessage('ServiceFailure'), mbError, MB_OK);
    exit;
  end;
  { Kept unless asked otherwise: the configuration names the mounts and the stores hold everything
    that has been read from the sources, which is not something to discard on a guess. A silent
    uninstall is not a person to ask -- asking it would put a dialog on a machine nobody is
    watching -- so it keeps the data, which is the answer the dialog defaults to anyway. }
  Data := ExpandConstant('{commonappdata}\PolyMount');
  if DirExists(Data) and not UninstallSilent and (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO) = IDYES) then
    DelTree(Data, True, True, True);
end;

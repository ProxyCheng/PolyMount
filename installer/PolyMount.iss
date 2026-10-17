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
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=PolyMount-{#AppVersion}-win-x64-Setup
UninstallDisplayIcon={app}\Tray\PolyMount.Tray.exe
CloseApplications=yes
RestartApplications=no
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
Name: "autostart"; Description: "{cm:AutoStart}"; Flags: checkedonce
Name: "overlays"; Description: "{cm:OriginDots}"; Flags: checkedonce
Name: "useraccount"; Description: "{cm:UserAccount}"; Flags: unchecked
; One line per bundled source plugin. Adding a plugin means adding a task here and the same file
; name to the two tables in [Code]. Checked by default, and checked again on an upgrade: without
; it the product has no source to mount at all.
Name: "plugins_svn"; Description: "{cm:PluginSvn}"

[Files]
Source: "{#PayloadDir}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Shell\PolyMount.Shell.dll"; DestDir: "{app}\Shell"; Flags: ignoreversion restartreplace
Source: "{#PayloadDir}\Shell\icons\palette-dot-*.ico"; DestDir: "{app}\Shell\icons"; Flags: ignoreversion
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
#sub OverlayEntry
Root: HKLM; Subkey: "Software\Classes\CLSID\{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\Shell\PolyMount.Shell.dll"; Tasks: overlays; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Both"; Tasks: overlays
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\   PolyMount {#Index}"; ValueType: string; ValueName: ""; ValueData: "{{7d619ac{#Index}-dbe2-4f33-9a11-6cc2c8329001}"; Tasks: overlays; Flags: uninsdeletekey
#endsub
#define Index 0
#for {Index = 0; Index < 7; Index++} OverlayEntry

[Run]
Filename: "{app}\Tray\PolyMount.Tray.exe"; Description: "{cm:LaunchProgram,PolyMount}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Shell reads the overlay handler list once per Explorer session, so the dots do not appear until
; Explorer is restarted -- the task says so, and this makes it one click rather than a chore.
Filename: "{cmd}"; Parameters: "/c taskkill /f /im explorer.exe >nul 2>&1 & start explorer.exe"; Description: "{cm:RestartExplorer}"; Flags: postinstall skipifsilent runasoriginaluser; Tasks: overlays

[CustomMessages]
english.ManageObject=Manage cache boundary...
english.PluginSvn=SVN source: mount a Subversion repository
chinesesimplified.ManageObject=管理缓存分界线...
chinesesimplified.PluginSvn=SVN 源：挂载 Subversion 仓库
english.AutoStart=Start the tray at Windows sign-in
english.OriginDots=Show source-layer color dots in Explorer
english.UserAccount=Use a Windows user account for the service (prompt for credentials on first install)
english.ServiceFailure=PolyMount service setup failed. See the installer log and retry setup.
english.WinFspFailure=WinFsp installation failed.
english.RuntimeFailure=Visual C++ runtime installation failed.
english.RestartExplorer=Restart Explorer now so the source-layer colour dots appear
english.SvnMissing=The Subversion command-line client (svn.exe) is not on this computer's PATH. SVN mounts will not work until it is installed; directory mounts are unaffected.
english.RemoveData=Also delete the PolyMount configuration and every layer's cached data?%n%nAnswer No to keep them for a later installation.
chinesesimplified.AutoStart=登录 Windows 时启动托盘
chinesesimplified.OriginDots=在资源管理器显示来源层彩色圆点
chinesesimplified.UserAccount=使用 Windows 用户账户运行服务（首次安装会询问凭据）
chinesesimplified.ServiceFailure=PolyMount 服务安装失败，请查看安装日志并重新安装。
chinesesimplified.WinFspFailure=WinFsp 安装失败。
chinesesimplified.RuntimeFailure=Visual C++ 运行库安装失败。
chinesesimplified.RestartExplorer=立即重启资源管理器，让来源层彩色圆点生效
chinesesimplified.SvnMissing=本机 PATH 上找不到 Subversion 命令行客户端（svn.exe）。在装上它之前 SVN 挂载不可用；本地目录源不受影响。
chinesesimplified.RemoveData=同时删除 PolyMount 的配置与所有层的缓存数据？%n%n选择「否」会保留它们，以便日后重新安装。

[Code]
{ The plugins this setup ships and offers. The task name and the assembly name travel together:
  the wizard's checkboxes decide which of them the installed configuration names, and the script
  is told both the selection and the full list so it can also take an unselected one back out.
  Adding a plugin means adding a task in [Tasks], a branch below, and incrementing the count. }
const
  PluginCount = 1;

function PluginTask(Index: Integer): String;
begin
  case Index of
    0: Result := 'plugins_svn';
  else
    Result := '';
  end;
end;

function PluginFile(Index: Integer): String;
begin
  case Index of
    0: Result := 'PolyMount.Plugin.Svn.dll';
  else
    Result := '';
  end;
end;

function JoinPluginFiles(SelectedOnly: Boolean): String;
var
  I: Integer;
  Task, FileName: String;
begin
  Result := '';
  for I := 0 to PluginCount - 1 do
  begin
    Task := PluginTask(I);
    FileName := PluginFile(I);
    if (Task <> '') and ((not SelectedOnly) or WizardIsTaskSelected(Task)) then
    begin
      if Result <> '' then Result := Result + ';';
      Result := Result + FileName;
    end;
  end;
end;

function HostAction(Action: String; Installed: Boolean): Boolean;
var Script, Args: String; Code: Integer;
begin
  if Installed then Script := ExpandConstant('{app}\installer\Install-Host.ps1')
  else Script := ExpandConstant('{tmp}\Install-Host.ps1');
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" -Action ' + Action +
    ' -InstallDirectory "' + ExpandConstant('{app}') + '"';
  if Action = 'Install' then
  begin
    if WizardIsTaskSelected('useraccount') then Args := Args + ' -UserAccount';
    Args := Args + ' -Plugins "' + JoinPluginFiles(True) + '"';
    Args := Args + ' -Managed "' + JoinPluginFiles(False) + '"';
  end;
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_SHOW, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
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

{ Whether the Subversion client is reachable through the machine's PATH, which is the one the
  service resolves svn.exe against: a LocalService account does not inherit a user's PATH. }
function PathContainsSvn(PathValue: String): Boolean;
var
  Rest, Entry: String;
  Cut: Integer;
begin
  Result := False;
  Rest := PathValue;
  while Rest <> '' do
  begin
    Cut := Pos(';', Rest);
    if Cut = 0 then begin Entry := Rest; Rest := ''; end
    else begin Entry := Copy(Rest, 1, Cut - 1); Rest := Copy(Rest, Cut + 1, Length(Rest)); end;
    Entry := Trim(Entry);
    { The registry keeps these quoted when they contain a space; the quotes are not the directory. }
    if (Length(Entry) >= 2) and (Entry[1] = '"') and (Entry[Length(Entry)] = '"') then
      Entry := Copy(Entry, 2, Length(Entry) - 2);
    if Entry <> '' then
    begin
      if Copy(Entry, Length(Entry), 1) <> '\' then Entry := Entry + '\';
      if FileExists(Entry + 'svn.exe') then begin Result := True; exit; end;
    end;
  end;
end;

function SvnCliMissing: Boolean;
var MachinePath: String;
begin
  Result := not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', MachinePath)
    or not PathContainsSvn(MachinePath);
end;

{ A prerequisite the setup can neither satisfy nor refuse over: only an SVN source needs it, and a
  directory source works without it. Said here, before anything is written, it is the difference
  between "the product is broken" and "I have to install Subversion first". }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + MemoGroupInfo + MemoTasksInfo;
  if SvnCliMissing then Result := Result + NewLine + NewLine + Space + CustomMessage('SvnMissing');
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

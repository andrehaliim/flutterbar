; StatefulBar installer (Inno Setup 6). Built by tools\publish.ps1 -Installer, which passes the version:
;   ISCC.exe /DAppVersion=1.0.0 packaging\inno\StatefulBar.iss
; Input: artifacts\publish\StatefulBar (from tools\publish.ps1). Output: artifacts\StatefulBar-<version>-win-x64-setup.exe.
;
; Per-user install (%LOCALAPPDATA%\Programs\StatefulBar, no admin), matching the app's own HKCU integration:
; the Run value and the PATH entry written here use the same format as the app's "Start with Windows" and
; "Add CLI to PATH" menu items, so the menu reads them back as on.

#ifndef AppVersion
  #error Define AppVersion, e.g. ISCC /DAppVersion=1.0.0
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\StatefulBar"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "StatefulBar"
#define AppExe "StatefulBar.exe"
#define Publisher "Andre Haliim Studio"
#define RepoUrl "https://github.com/andrehaliim/statefulbar"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Never change AppId: it ties updates and the uninstaller to earlier installs.
AppId={{7FA9FEAC-B903-41B3-ACB0-33621A15225D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) 2026 {#Publisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
; Fixed folder: [InstallDelete] clears runtime files in it on update, so it must never be a shared folder.
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\StatefulBar.App\Assets\statefulbar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; A running widget holds its files open: Restart Manager closes it before the files are replaced.
CloseApplications=force
RestartApplications=no
; Broadcasts WM_SETTINGCHANGE after the PATH change, so new terminals see it.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} with Windows"; GroupDescription: "Options:"
Name: "addtopath"; Description: "Add the sbar CLI to PATH"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Runtime files of the previous version, so two .NET versions never mix. The exes are overwritten; the
; uninstaller files (unins000.*) are kept.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pdb"
Type: filesandordirs; Name: "{app}\licenses"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app writes from "Start with Windows" (StartupRegistration): quoted exe path.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvKey = 'Environment';

function NormalizeDir(Dir: String): String;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(Trim(RemoveQuotes(Trim(Dir)))));
end;

{ Rewrites the user PATH: drops every entry equal to Dir, then appends Dir when Add is true. Keeps %VARS% unexpanded. }
procedure UpdateUserPath(Dir: String; Add: Boolean);
var
  OldPath, NewPath, Entry, Target: String;
  Rest: String;
  P: Integer;
  Found: Boolean;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', OldPath) then
    OldPath := '';
  Target := NormalizeDir(Dir);
  NewPath := '';
  Found := False;
  Rest := OldPath;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    if Trim(Entry) = '' then
      Continue;
    if NormalizeDir(Entry) = Target then
    begin
      Found := True;
      if Add then
        NewPath := NewPath + ';' + Entry; { already present: keep it where it is }
      Continue;
    end;
    NewPath := NewPath + ';' + Entry;
  end;
  if Add and not Found then
    NewPath := NewPath + ';' + Dir;
  if (NewPath <> '') and (NewPath[1] = ';') then
    NewPath := Copy(NewPath, 2, Length(NewPath));
  if (Add and not Found) or (not Add and Found) then
  begin
    if NewPath = '' then
      RegDeleteValue(HKCU, EnvKey, 'Path')
    else
      RegWriteExpandStringValue(HKCU, EnvKey, 'Path', NewPath);
    Log('User PATH updated: ' + NewPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    UpdateUserPath(ExpandConstant('{app}'), True);
end;

{ Closes only the widget started from this install folder (a dev build elsewhere keeps running). Its Job Object
  ends any flutter session it started. }
procedure StopInstalledApp();
var
  ResultCode: Integer;
  Script: String;
begin
  Script := 'Get-Process StatefulBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ''' +
    ExpandConstant('{app}') + '\*'' } | Stop-Process -Force';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue, SettingsDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopInstalledApp();
    { Start with Windows: removed only when it starts this install, not another copy. }
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#AppName}', RunValue) and
       (NormalizeDir(RunValue) = NormalizeDir(ExpandConstant('{app}\{#AppExe}'))) then
      RegDeleteValue(HKCU, '{#RunKey}', '{#AppName}');
    UpdateUserPath(ExpandConstant('{app}'), False);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    SettingsDir := ExpandConstant('{userappdata}\{#AppName}');
    { Silent uninstalls always keep the settings. }
    if DirExists(SettingsDir) and not UninstallSilent() then
      if MsgBox('Also remove your StatefulBar settings and logs?' + #13#10#13#10 + SettingsDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(SettingsDir, True, True, True);
  end;
end;

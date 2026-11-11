#define MyAppExeName "Kivo.exe"
#ifndef PublishDir
#define PublishDir AddBackslash(SourcePath) + "..\publish\win-x64"
#endif
#ifndef MinimumWindowsVersion
#define MinimumWindowsVersion "10.0"
#endif
#define MyAppExe AddBackslash(PublishDir) + MyAppExeName
#define MyAppName GetStringFileInfo(MyAppExe, "ProductName")
#ifndef MyAppVersion
#define MyAppVersion GetVersionNumbersString(MyAppExe)
#endif
#define MyAppPublisher GetStringFileInfo(MyAppExe, "CompanyName")

[Setup]
AppId={{16C8742E-AD7A-4A83-AF71-EAA2F1405DB3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/aoyn1xw/Kivo
AppSupportURL=https://github.com/aoyn1xw/Kivo/issues
AppUpdatesURL=https://github.com/aoyn1xw/Kivo/releases
DefaultDirName={autopf}\Kivo
DefaultGroupName=Kivo
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinimumWindowsVersion}
CloseApplications=yes
RestartApplications=no
AppMutex=Local\ShadowPlay.SingleInstance.9f2c1a
UsePreviousAppDir=yes
SetupLogging=yes
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\flutter\windows\runner\resources\app_icon.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=Kivo-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\Kivo"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Kivo"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  LegacyUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{16C8742E-AD7A-4A83-AF71-EAA2F1405DB3}_is1';
  InvalidAttributes = $FFFFFFFF;
  ReparsePointAttribute = $400;

var
  RemoveAppData: Boolean;
  LegacyInstallDir: String;
  CleanupWarnings: String;

function FileAttributes(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure CleanupWarning(const Message: String);
begin
  Log('Legacy cleanup warning: ' + Message);
  CleanupWarnings := CleanupWarnings + Message + #13#10;
end;

// Reject relative/device/UNC paths, traversal, alternate streams, aliases and
// every reparse-point ancestor. Only an existing local path is accepted.
function SafeExistingPath(const Path: String): Boolean;
var
  Current: String;
  Attributes: LongWord;
begin
  Result := False;
  if (Length(Path) < 3) or (Copy(Path, 2, 2) <> ':\')
    or (Pos('/', Path) > 0) or (Pos('..', Path) > 0)
    or (Pos(':', Copy(Path, 3, Length(Path))) > 0)
    or (CompareText(Path, RemoveBackslashUnlessRoot(ExpandFileName(Path))) <> 0) then
    Exit;
  Current := Path;
  while Length(Current) > 3 do
  begin
    Attributes := FileAttributes(Current);
    if (Attributes = InvalidAttributes) or ((Attributes and ReparsePointAttribute) <> 0) then
      Exit;
    Current := ExtractFileDir(Current);
  end;
  Result := True;
end;

function InitializeSetup(): Boolean;
var
  Candidate, UninstallCommand, Expected: String;
begin
  Result := True;
  LegacyInstallDir := '';
  CleanupWarnings := '';
  // The historical installer was native x64, machine-wide. Never trust HKCU,
  // portable markers, or a registry path outside the exact historical default.
  if not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'InstallLocation', Candidate) then
    Exit;
  Candidate := RemoveBackslashUnlessRoot(Candidate);
  Expected := ExpandConstant('{autopf}\ShadowPlay');
  if not SafeExistingPath(Candidate) or (CompareText(Candidate, Expected) <> 0) then
  begin
    CleanupWarning('Unverified or non-default previous directory; automatic cleanup skipped.');
    Exit;
  end;
  if not RegQueryStringValue(HKLM64, LegacyUninstallKey, 'UninstallString', UninstallCommand)
    or (CompareText(RemoveQuotes(UninstallCommand), Candidate + '\unins000.exe') <> 0)
    or not SafeExistingPath(Candidate + '\unins000.exe')
    or not SafeExistingPath(Candidate + '\unins000.dat') then
  begin
    CleanupWarning('Previous machine uninstall inventory could not be verified; cleanup skipped.');
    Exit;
  end;
  LegacyInstallDir := Candidate;
end;

procedure DeleteLegacyFile(const Dir, FileName: String);
var
  Target: String;
begin
  Target := Dir + '\' + FileName;
  if not FileExists(Target) then
    Exit;
  try
    if not SafeExistingPath(Target) then
    begin
      CleanupWarning('Unsafe cleanup target preserved: ' + Target);
      Exit;
    end;
    if not DeleteFile(Target) then
      DelayDeleteFile(Target, 8);
    if FileExists(Target) then
      CleanupWarning('Could not delete: ' + Target)
    else
      Log('Legacy cleanup deleted: ' + Target);
  except
    CleanupWarning('Failed cleanup: ' + Target + ': ' + GetExceptionMessage);
  end;
end;

procedure RemoveLegacyApplicationFiles(const Dir: String);
begin
  // Exactly the assembly/exe names previous ShadowPlay builds published.
  // Third-party and runtime files kept the same names across the rebrand and
  // are deliberately left untouched, as are all user data files.
  DeleteLegacyFile(Dir, 'ShadowPlay.exe');
  DeleteLegacyFile(Dir, 'ShadowPlay.dll');
  DeleteLegacyFile(Dir, 'ShadowPlay.pdb');
  DeleteLegacyFile(Dir, 'ShadowPlay.deps.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.runtimeconfig.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Core.dll');
  DeleteLegacyFile(Dir, 'ShadowPlay.Core.pdb');
  DeleteLegacyFile(Dir, 'ShadowPlay.Core.deps.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Core.runtimeconfig.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Api.dll');
  DeleteLegacyFile(Dir, 'ShadowPlay.Api.pdb');
  DeleteLegacyFile(Dir, 'ShadowPlay.Api.deps.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Api.runtimeconfig.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Windows.exe');
  DeleteLegacyFile(Dir, 'ShadowPlay.Windows.dll');
  DeleteLegacyFile(Dir, 'ShadowPlay.Windows.pdb');
  DeleteLegacyFile(Dir, 'ShadowPlay.Windows.deps.json');
  DeleteLegacyFile(Dir, 'ShadowPlay.Windows.runtimeconfig.json');
end;

// Preserve unknown/user-created shortcuts. Only remove a common shortcut
// whose actual target is the verified legacy executable; never scan user homes.
procedure RemoveLegacyShortcut(const Path: String);
var
  Shell, Link: Variant;
begin
  if not FileExists(Path) then
    Exit;
  try
    if not SafeExistingPath(Path) then
    begin
      CleanupWarning('Unsafe shortcut preserved: ' + Path);
      Exit;
    end;
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    if (CompareText(Link.TargetPath, LegacyInstallDir + '\ShadowPlay.exe') <> 0)
      or (Link.Arguments <> '') then
    begin
      CleanupWarning('Unverified shortcut target preserved: ' + Path);
      Exit;
    end;
    // Keep the existing shortcut (including desktop preference/custom group)
    // useful, even when the new optional desktop shortcut was not selected.
    Link.TargetPath := ExpandConstant('{app}\Kivo.exe');
    Link.WorkingDirectory := ExpandConstant('{app}');
    Link.IconLocation := ExpandConstant('{app}\Kivo.exe,0');
    Link.Save;
    Log('Legacy shortcut retargeted to Kivo: ' + Path);
  except
    CleanupWarning('Failed shortcut migration: ' + Path + ': ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  try
    if LegacyInstallDir <> '' then
    begin
      if (CompareText(LegacyInstallDir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0)
        or not SafeExistingPath(LegacyInstallDir) then
        CleanupWarning('Different or unsafe installation directory; old files and uninstall inventory preserved.')
      else
      begin
        RemoveLegacyShortcut(ExpandConstant('{group}\ShadowPlay.lnk'));
        RemoveLegacyShortcut(ExpandConstant('{commondesktop}\ShadowPlay.lnk'));
        RemoveLegacyApplicationFiles(LegacyInstallDir);
      end;
    end
    else
      Log('No verified legacy installation: automatic cleanup not attempted.');
  except
    CleanupWarning('Cleanup stopped: ' + GetExceptionMessage);
  end;
  if (CleanupWarnings <> '') and not WizardSilent then
    MsgBox('Kivo is installed. Some legacy items were preserved or could not be migrated:' + #13#10 +
      CleanupWarnings + #13#10 +
      'See the Setup log for details. Review preserved items before removing them manually.',
      mbInformation, MB_OK);
end;

function InitializeUninstall(): Boolean;
begin
  RemoveAppData := False;

  if not UninstallSilent then
    RemoveAppData := MsgBox(
      'Remove Kivo application data too?' + #13#10 + #13#10 +
      'Choose Yes to delete settings, pairing information, logs, and cached previews from:' + #13#10 +
      ExpandConstant('{localappdata}\ShadowPlay') + #13#10 + #13#10 +
      'Your recordings and downloaded clips will not be deleted.',
      mbConfirmation, MB_YESNO) = IDYES;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveAppData then
    DelTree(ExpandConstant('{localappdata}\ShadowPlay'), True, True, True);
end;

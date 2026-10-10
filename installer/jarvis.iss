; JARVIS installer (Inno Setup 6). Built by build/package.ps1.
; Per-user install: no administrator rights needed, nothing touches system folders.

#ifndef AppVersion
  #define AppVersion "0.3.5"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
#endif

[Setup]
AppId={{6C1C3E2A-6B2F-4E2B-9F55-3A1F0B7A7E11}
AppName=JARVIS
AppVersion={#AppVersion}
AppVerName=JARVIS {#AppVersion}
AppPublisher=JARVIS
DefaultDirName={localappdata}\Programs\JARVIS
DefaultGroupName=JARVIS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=JARVIS-Setup-x64
SetupIconFile=..\assets\jarvis.ico
UninstallDisplayIcon={app}\jarvis.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "startup"; Description: "Start JARVIS when I sign in to Windows (recommended)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JARVIS"; Filename: "{app}\JARVIS.exe"; IconFilename: "{app}\jarvis.ico"; Check: HasDesktopShell
Name: "{group}\JARVIS"; Filename: "{app}\jarvis-core.exe"; IconFilename: "{app}\jarvis.ico"; Check: not HasDesktopShell
Name: "{group}\Uninstall JARVIS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\JARVIS"; Filename: "{app}\JARVIS.exe"; IconFilename: "{app}\jarvis.ico"; Tasks: desktopicon; Check: HasDesktopShell

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "JARVIS"; ValueData: """{app}\jarvis-core.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\JARVIS.exe"; Description: "Start JARVIS now"; Flags: nowait postinstall skipifsilent; Check: HasDesktopShell
Filename: "{app}\jarvis-core.exe"; Description: "Start JARVIS now"; Flags: nowait postinstall skipifsilent; Check: not HasDesktopShell

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM JARVIS.exe"; Flags: runhidden; RunOnceId: "KillShell"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM jarvis-core.exe"; Flags: runhidden; RunOnceId: "KillCore"

[Code]
function HasDesktopShell: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\JARVIS.exe'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    { Stop a running JARVIS before replacing its files. Your memory and settings are kept. }
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM JARVIS.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM jarvis-core.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

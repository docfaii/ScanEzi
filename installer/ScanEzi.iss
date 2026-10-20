; Inno Setup script for ScanEzi.
;
; Build with:
;   iscc installer\ScanEzi.iss /DMyAppVersion=2.0.0 /DSourceDir=publish\self-contained
;
; The release workflow compiles the self-contained publish so the installer has no
; .NET prerequisite. Output lands in installer\output.

#define MyAppName "ScanEzi"
#define MyAppExeName "ScanEzi.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\self-contained"
#endif

#ifndef OutputDir
  #define OutputDir "output"
#endif

[Setup]
AppId={{7C2F4B0E-4A1D-4F1E-9C6B-3B0E5F7D8A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=ScanEzi
AppPublisherURL=https://github.com/docfaii/ScanEzi
AppSupportURL=https://github.com/docfaii/ScanEzi/issues
AppUpdatesURL=https://github.com/docfaii/ScanEzi/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install: no administrator prompt, and the app can update itself in place.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
SetupIconFile=..\Assets\ScanEzi.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} installer
VersionInfoCompany=ScanEzi

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; The updater relaunches the app itself, so only start it after an interactive install.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\ScanEzi"

; Oversight installer — Inno Setup 6
; Produces a single self-contained Setup.exe that installs per-user with NO
; admin prompt (UAC). The .NET 8 runtime is bundled, so end users install nothing
; else — they just download one file, double-click, and click Install.
;
; To (re)build:  installer\build-installer.ps1   (publishes + compiles in one step)

#define MyAppName        "Oversight"
#define MyAppVersion     "2.1.4"
#define MyAppPublisher   "Trevor Zimmerman"
#define MyAppURL         "https://github.com/"
#define MyAppExeName     "Oversight.exe"

; Self-contained publish output (set by build-installer.ps1 / dotnet publish).
#ifndef MyPublishDir
  #define MyPublishDir   GetEnv("LOCALAPPDATA") + "\Oversight_build\publish"
#endif

[Setup]
; A unique AppId keeps upgrades/uninstalls tied to this product. Do not change it.
AppId={{12416BE9-1BBC-49C8-81D8-CA67002ACD92}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; --- Per-user install: no admin rights, no UAC prompt ---
PrivilegesRequired=lowest

; --- Windows 11 64-bit only (matches the app's SupportedOSPlatformVersion) ---
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; --- Output: one file the user uploads to their website ---
OutputDir=..\dist
OutputBaseFilename=Oversight-Setup-{#MyAppVersion}
SetupIconFile=..\Oversight\Assets\app.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

; If Oversight is running (it lives in the tray), close it cleanly before replacing files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent

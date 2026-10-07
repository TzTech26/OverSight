; Oversight installer — Inno Setup 6
; Produces a single self-contained Setup.exe that installs per-user with NO
; admin prompt (UAC). The .NET 8 runtime is bundled, so end users install nothing
; else — they just download one file, double-click, and click Install.
;
; To (re)build:  installer\build-installer.ps1   (publishes + compiles in one step)
; Brand images:  installer\make-brand-assets.ps1 (regenerates assets\*.bmp and the app icon)

#define MyAppName        "Oversight"
#define MyAppVersion     "2.2.0"
#define MyAppPublisher   "Trevor Zimmerman"
#define MyAppURL         "https://github.com/"
#define MyAppExeName     "Oversight.exe"
#define MyAppDescription "Screen zoom, crosshair overlay and auto clicker for Windows 11"

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
AppComments={#MyAppDescription}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
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
Compression=lzma2/ultra64
SolidCompression=yes

; --- Look: modern wizard with Oversight branding (100% + 200% DPI artwork) ---
WizardStyle=modern
WizardSizePercent=100
WizardImageFile=assets\wizard-large-100.bmp,assets\wizard-large-200.bmp
WizardSmallImageFile=assets\wizard-small-100.bmp,assets\wizard-small-200.bmp

; If Oversight is running (it lives in the tray), close it before replacing files —
; and force it if it doesn't respond to the polite request.
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nZoom in on any game, draw a custom crosshair and automate clicks — all from the system tray. Nothing else needs to be installed.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup";     Description: "Start Oversight automatically when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Run Oversight when Windows starts" switch writes (StartupManager).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Oversight"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the tray app isn't running while its files are removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "StopOversight"

[Code]
// The app can also turn "run at startup" on by itself, outside the installer's [Registry]
// log — so always clear the value on uninstall rather than leave a dead Run entry behind.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Oversight');
end;

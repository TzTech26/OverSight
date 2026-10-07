# installer/

Tooling that turns Oversight into a **downloadable installer** and manages
**versioned releases**. You normally only run one command here:
`build-installer.ps1`.

## Prerequisites (one-time)

- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- **Inno Setup 6** — `winget install JRSoftware.InnoSetup`

## Files

| File | What it is |
|---|---|
| **Oversight.iss** | Inno Setup script that defines the installer: per-user install (no admin/UAC), bundled .NET runtime, Start Menu + optional desktop shortcut, uninstaller, Windows 11 x64. The `#define MyAppVersion` line near the top is the **single source of truth for the version number** — bump it here to release a new version. |
| **build-installer.ps1** | Main entry point. Publishes a self-contained build, compiles `Oversight.iss`, then records the result into the release system on the **beta** channel. |
| **promote-release.ps1** | Promotes a tested build from **beta** to **verified** (the channel you publish from). Usage: `-Version 1.0.0`. |
| **list-releases.ps1** | Prints a table of every recorded release and the latest version per channel. |
| **make-brand-assets.ps1** | Regenerates the app icon (`Oversight/Assets/app.ico`, 16–256 px) and the installer wizard images (`assets/*.bmp`) from code. Run it after changing the logo colours or artwork. |
| **_release-common.ps1** | Shared helper functions, dot-sourced by the scripts above. The leading underscore means "not run directly." |

## Quick start

```powershell
# 1. (edit Oversight.iss -> bump MyAppVersion if releasing a new version)

# 2. Build the installer  ->  lands in ..\releases\beta\
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

# 3. Test the beta installer, then promote it  ->  ..\releases\verified\
powershell -ExecutionPolicy Bypass -File installer\promote-release.ps1 -Version 1.0.0

# See everything that's been built
powershell -ExecutionPolicy Bypass -File installer\list-releases.ps1
```

The finished installers and the version history live in **[../releases/](../releases/)** —
see [../releases/README.md](../releases/README.md) for the channel layout, the
manifest format, and how to publish to a website (including the SmartScreen note).

> `dist\` is just the raw Inno Setup compiler output (scratch). The version-managed,
> publishable copies are the ones under `releases\`.

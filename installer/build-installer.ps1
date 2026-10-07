# Builds the Oversight installer end-to-end:
#   1. Publishes a self-contained, single-folder Release build (bundles .NET 8)
#   2. Compiles installer\Oversight.iss into dist\Oversight-Setup-<ver>.exe
#   3. Records the build into the release system (releases\) on the BETA channel
#
# Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
# After it runs, the finished installer is in  releases\beta\  (and dist\ as raw output).
# Test it, then promote:  promote-release.ps1 -Version <ver>  -> releases\verified\

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_release-common.ps1")

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Csproj     = Join-Path $RepoRoot "Oversight\Oversight.csproj"
$Iss        = Join-Path $PSScriptRoot "Oversight.iss"
# Publish to LOCALAPPDATA (not the network share) to avoid locked/denied-file errors.
$PublishDir = Join-Path $env:LOCALAPPDATA "Oversight_build\publish"

# Single source of truth for the version: parse it out of Oversight.iss.
$Version = (Select-String -Path $Iss -Pattern '#define\s+MyAppVersion\s+"([^"]+)"').Matches[0].Groups[1].Value
if (-not $Version) { throw "Could not read MyAppVersion from $Iss" }
Write-Host "==> Oversight version $Version" -ForegroundColor Cyan

# Locate the Inno Setup compiler (winget installs it under LOCALAPPDATA\Programs).
$Iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Iscc) {
  throw "ISCC.exe not found. Install Inno Setup:  winget install JRSoftware.InnoSetup"
}

Write-Host "==> Publishing self-contained build..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
# -p:Version stamps the exe with the same version as the installer (Oversight.iss is the source of truth).
dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:Version=$Version -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "==> Compiling installer..." -ForegroundColor Cyan
& $Iscc "/DMyPublishDir=$PublishDir" $Iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$InstallerPath = Join-Path $RepoRoot "dist\Oversight-Setup-$Version.exe"
if (-not (Test-Path $InstallerPath)) { throw "Expected installer not found: $InstallerPath" }

Write-Host "==> Recording release (beta channel)..." -ForegroundColor Cyan
Register-Release -Version $Version -PublishDir $PublishDir -InstallerPath $InstallerPath

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    beta installer:  $(Join-Path $BetaDir ("Oversight-Setup-$Version.exe"))"
Write-Host "    version folder:  $(Join-Path $VersionsDir $Version)"
Write-Host "    Promote when tested:  installer\promote-release.ps1 -Version $Version"

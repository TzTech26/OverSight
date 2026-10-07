# Promotes a built (beta) version to the "verified" channel.
# The installer is copied into releases\verified\ (it also stays in beta\), and the
# manifest entry's channel flips to "verified".
#
# Usage:  powershell -ExecutionPolicy Bypass -File installer\promote-release.ps1 -Version 1.0.0

param([Parameter(Mandatory)] [string]$Version)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_release-common.ps1")

$verDir = Join-Path $VersionsDir $Version
if (-not (Test-Path $verDir)) { throw "Version $Version not found under $VersionsDir. Build it first." }

$m     = Read-Manifest
$entry = $m.releases | Where-Object { $_.version -eq $Version } | Select-Object -First 1
if (-not $entry) { throw "Version $Version is not in the manifest." }

$installerName = $entry.installer.file
$src           = Join-Path $verDir $installerName
if (-not (Test-Path $src)) { throw "Installer missing: $src" }

Initialize-Releases
Copy-Item $src (Join-Path $VerifiedDir $installerName) -Force

$entry.channel     = "verified"
$entry.promotedUtc = (Get-Date).ToUniversalTime().ToString("o")
$m.latest.verified = $Version
Write-Manifest $m

Write-Host "Promoted $Version to verified." -ForegroundColor Green
Write-Host "  verified\$installerName  <- this is the file to publish to your website."

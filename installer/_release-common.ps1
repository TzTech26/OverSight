# Shared helpers for the Oversight release system. Dot-sourced by the other
# scripts (build-installer.ps1, promote-release.ps1, list-releases.ps1).
#
# Layout produced under  <repo>\releases\ :
#   manifest.json                     master index of all versions + channels
#   beta\      / verified\            finished installers per channel
#   versions\<ver>\                   per-version: installer, app exe, files.json, release.json

Set-StrictMode -Off

$script:RepoRoot     = Split-Path -Parent $PSScriptRoot
$script:ReleasesDir  = Join-Path $RepoRoot "releases"
$script:VersionsDir  = Join-Path $ReleasesDir "versions"
$script:BetaDir      = Join-Path $ReleasesDir "beta"
$script:VerifiedDir  = Join-Path $ReleasesDir "verified"
$script:ManifestPath = Join-Path $ReleasesDir "manifest.json"

function New-Dir([string]$Path) {
  if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Initialize-Releases {
  New-Dir $ReleasesDir; New-Dir $VersionsDir; New-Dir $BetaDir; New-Dir $VerifiedDir
}

# Read manifest.json into a PSCustomObject, or a fresh skeleton if absent.
function Read-Manifest {
  if (Test-Path $ManifestPath) {
    return (Get-Content $ManifestPath -Raw | ConvertFrom-Json)
  }
  return [pscustomobject]@{
    product    = "Oversight"
    updatedUtc = $null
    latest     = [pscustomobject]@{ beta = $null; verified = $null }
    releases   = @()
  }
}

function Write-Manifest($Manifest) {
  $Manifest.updatedUtc = (Get-Date).ToUniversalTime().ToString("o")
  $Manifest | ConvertTo-Json -Depth 12 | Set-Content -Path $ManifestPath -Encoding UTF8
}

# Build the per-file list (path, size, sha256, fileversion) for every file in a folder.
function Get-FileManifest([string]$Root) {
  $rootFull = (Resolve-Path $Root).Path.TrimEnd('\') + '\'
  Get-ChildItem $Root -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($rootFull.Length)
    $fv  = $null
    try { $fv = $_.VersionInfo.FileVersion } catch {}
    [pscustomobject]@{
      path        = $rel
      size        = $_.Length
      sha256      = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
      fileVersion = $fv
    }
  }
}

# Record a freshly built version into versions\<ver>\, copy the installer into the
# beta channel, and update manifest.json. Re-running for the same version replaces it.
function Register-Release {
  param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$PublishDir,    # self-contained publish output folder
    [Parameter(Mandatory)] [string]$InstallerPath  # the compiled Setup .exe
  )
  Initialize-Releases

  $verDir = Join-Path $VersionsDir $Version
  New-Dir $verDir

  $installerName = Split-Path $InstallerPath -Leaf
  $appExeSrc     = Join-Path $PublishDir "Oversight.exe"

  Write-Host "  - hashing $((Get-ChildItem $PublishDir -Recurse -File | Measure-Object).Count) files..."
  $files = @(Get-FileManifest $PublishDir)

  # files.json — the per-file version list for this build.
  [pscustomobject]@{
    version   = $Version
    builtUtc  = (Get-Date).ToUniversalTime().ToString("o")
    fileCount = $files.Count
    files     = $files
  } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $verDir "files.json") -Encoding UTF8

  # Copy the completed installer + app exe into the version folder.
  Copy-Item $InstallerPath (Join-Path $verDir $installerName) -Force
  Copy-Item $appExeSrc     (Join-Path $verDir "Oversight.exe") -Force

  # Copy the finished installer into the beta channel.
  Copy-Item $InstallerPath (Join-Path $BetaDir $installerName) -Force

  $installerHash = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash
  $installerSize = (Get-Item $InstallerPath).Length
  $appExeHash    = (Get-FileHash $appExeSrc -Algorithm SHA256).Hash
  $appExeVer     = (Get-Item $appExeSrc).VersionInfo.FileVersion

  $entry = [pscustomobject]@{
    version       = $Version
    channel       = "beta"
    builtUtc      = (Get-Date).ToUniversalTime().ToString("o")
    promotedUtc   = $null
    installer     = [pscustomobject]@{ file = $installerName; size = $installerSize; sha256 = $installerHash }
    appExe        = [pscustomobject]@{ file = "Oversight.exe"; fileVersion = $appExeVer; sha256 = $appExeHash }
    fileCount     = $files.Count
    filesManifest = "versions/$Version/files.json"
  }

  # release.json mirrors the manifest entry, kept alongside the build.
  $entry | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $verDir "release.json") -Encoding UTF8

  $m = Read-Manifest
  $m.releases = @($m.releases | Where-Object { $_.version -ne $Version }) + $entry
  $m.latest.beta = $Version
  Write-Manifest $m

  Write-Host "  - registered $Version into beta (files: $($files.Count))" -ForegroundColor Green
}

# Prints all recorded releases and the current latest-per-channel pointers.
#
# Usage:  powershell -ExecutionPolicy Bypass -File installer\list-releases.ps1

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_release-common.ps1")

$m = Read-Manifest
if (-not $m.releases -or @($m.releases).Count -eq 0) {
  Write-Host "No releases recorded yet. Run build-installer.ps1 first."
  return
}

Write-Host ""
Write-Host "$($m.product) releases" -ForegroundColor Cyan
Write-Host "  latest beta:     $($m.latest.beta)"
Write-Host "  latest verified: $($m.latest.verified)"
Write-Host ""

@($m.releases) | Sort-Object { [version]$_.version } -Descending | ForEach-Object {
  $sizeMB = [math]::Round($_.installer.size / 1MB, 1)
  [pscustomobject]@{
    Version   = $_.version
    Channel   = $_.channel
    Files     = $_.fileCount
    'Size MB' = $sizeMB
    Built     = ([datetime]$_.builtUtc).ToString("yyyy-MM-dd HH:mm")
    Installer = $_.installer.file
  }
} | Format-Table -AutoSize

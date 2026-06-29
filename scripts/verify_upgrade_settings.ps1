[CmdletBinding()]
param(
    [ValidateSet("capture", "verify")]
    [string]$Mode = "capture",
    [string]$SettingsPath = "",
    [string]$SnapshotPath = ""
)

$ErrorActionPreference = "Stop"

function Get-DefaultSettingsPath {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $env:USERPROFILE "AppData\Local" }
    return Join-Path $base "RenderFarm\app-role.json"
}

function Get-DefaultSnapshotPath {
    $base = if ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
    return Join-Path $base "renderfarm-settings-upgrade-snapshot.json"
}

if ([string]::IsNullOrWhiteSpace($SettingsPath)) { $SettingsPath = Get-DefaultSettingsPath }
if ([string]::IsNullOrWhiteSpace($SnapshotPath)) { $SnapshotPath = Get-DefaultSnapshotPath }

if ($Mode -eq "capture") {
    if (-not (Test-Path -LiteralPath $SettingsPath)) {
        throw "Settings file was not found at $SettingsPath. Open the launcher and save settings before capturing upgrade state."
    }

    $hash = Get-FileHash -LiteralPath $SettingsPath -Algorithm SHA256
    $snapshot = [ordered]@{
        settingsPath = $SettingsPath
        hash = $hash.Hash
        capturedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $directory = Split-Path -Parent $SnapshotPath
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $snapshot | ConvertTo-Json -Depth 4 | Set-Content -Path $SnapshotPath
    Write-Host "Captured RenderFarm settings snapshot to $SnapshotPath"
    return
}

if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    throw "Snapshot file was not found at $SnapshotPath. Run with -Mode capture before the upgrade."
}

$snapshotData = Get-Content -Raw -Path $SnapshotPath | ConvertFrom-Json
$settingsToCheck = if ($snapshotData.settingsPath) { [string]$snapshotData.settingsPath } else { $SettingsPath }
if (-not (Test-Path -LiteralPath $settingsToCheck)) {
    throw "Settings file was not found after upgrade at $settingsToCheck."
}

$currentHash = (Get-FileHash -LiteralPath $settingsToCheck -Algorithm SHA256).Hash
if ($currentHash -ne [string]$snapshotData.hash) {
    throw "Settings file changed during upgrade. Expected $($snapshotData.hash), got $currentHash."
}

Write-Host "RenderFarm settings survived upgrade unchanged: $settingsToCheck"

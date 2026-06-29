param(
    [string]$InstallRoot = "",
    [switch]$RemoveSettings,
    [switch]$RemoveShortcuts
)

$ErrorActionPreference = "Stop"

function Get-DefaultInstallRoot {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $env:USERPROFILE "AppData\Local" }
    return Join-Path $base "RenderFarm\Product"
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-DefaultInstallRoot }

if (Test-Path -LiteralPath $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    Write-Host "Removed RenderFarm install folder: $InstallRoot"
}
else {
    Write-Host "RenderFarm install folder was not present: $InstallRoot"
}

if ($RemoveShortcuts) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "RenderFarm Launcher.lnk"
    $menuShortcut = Join-Path (Join-Path ([Environment]::GetFolderPath("Programs")) "RenderFarm") "RenderFarm Launcher.lnk"
    foreach ($shortcut in @($desktopShortcut, $menuShortcut)) {
        if (Test-Path -LiteralPath $shortcut) {
            Remove-Item -LiteralPath $shortcut -Force
            Write-Host "Removed shortcut: $shortcut"
        }
    }
}

if ($RemoveSettings) {
    $settingsBase = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Split-Path -Parent $InstallRoot }
    $settingsPath = Join-Path $settingsBase "RenderFarm\app-role.json"
    if (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
        Write-Host "Removed role settings: $settingsPath"
    }
}
else {
    Write-Host "Saved role/settings were preserved. Pass -RemoveSettings to remove them."
}

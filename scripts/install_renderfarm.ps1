param(
    [string]$SourcePath = "",
    [string]$InstallRoot = "",
    [ValidateSet("", "controller", "worker")]
    [string]$Role = "",
    [string]$ControllerUrl = "",
    [string]$WorkerId = "",
    [string]$DisplayName = "",
    [string]$ApiToken = "",
    [string]$UnrealSearchRoot = "",
    [string]$ProjectPath = "",
    [string]$SharedOutputRoot = "",
    [switch]$DiscoveryEnabled,
    [string]$HostName = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$Port = 9200,
    [switch]$CreateShortcuts
)

$ErrorActionPreference = "Stop"

function Resolve-DefaultSourcePath {
    $packageParent = Resolve-Path (Join-Path $PSScriptRoot "..") -ErrorAction SilentlyContinue
    if ($packageParent -and (Test-Path -LiteralPath (Join-Path $packageParent.Path "RenderFarm.Launcher.exe"))) {
        return $packageParent.Path
    }

    $repoPackage = Resolve-Path (Join-Path $PSScriptRoot "..\publish\RenderFarm") -ErrorAction SilentlyContinue
    if ($repoPackage) { return $repoPackage.Path }

    throw "Could not find a RenderFarm package. Run scripts\publish_apps.ps1 first, or pass -SourcePath."
}

function Get-DefaultInstallRoot {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $env:USERPROFILE "AppData\Local" }
    return Join-Path $base "RenderFarm\Product"
}

function Write-RoleSettings {
    param([string]$RoleSettingsPath)
    $settings = [ordered]@{
        role = $Role
        hostName = $HostName
        port = $Port
        controllerUrl = $ControllerUrl
        discoveryEnabled = [bool]$DiscoveryEnabled
        discoverySeconds = 5
        discoveryPort = 39200
        workerId = $WorkerId
        displayName = $DisplayName
        apiToken = $ApiToken
        unrealSearchRoot = $UnrealSearchRoot
        projectPath = $ProjectPath
        sharedOutputRoot = $SharedOutputRoot
        updatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $directory = Split-Path -Parent $RoleSettingsPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $settings | ConvertTo-Json -Depth 5 | Set-Content -Path $RoleSettingsPath
}

function New-Shortcut {
    param([string]$Path, [string]$TargetPath, [string]$Arguments, [string]$WorkingDirectory)
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $TargetPath
    $shortcut.Save()
}

if ([string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath = Resolve-DefaultSourcePath }
$SourcePath = (Resolve-Path -Path $SourcePath).Path
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-DefaultInstallRoot }

$launcherSource = Join-Path $SourcePath "RenderFarm.Launcher.exe"
if (-not (Test-Path -LiteralPath $launcherSource)) {
    throw "RenderFarm.Launcher.exe was not found in $SourcePath. Publish a package before installing."
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item -Path (Join-Path $SourcePath "*") -Destination $InstallRoot -Recurse -Force

$launcher = Join-Path $InstallRoot "RenderFarm.Launcher.exe"
$settingsBase = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Split-Path -Parent $InstallRoot }
$roleSettings = Join-Path $settingsBase "RenderFarm\app-role.json"
if ($Role) {
    Write-RoleSettings -RoleSettingsPath $roleSettings
}

if ($CreateShortcuts) {
    $desktop = [Environment]::GetFolderPath("DesktopDirectory")
    $programs = [Environment]::GetFolderPath("Programs")
    $menuFolder = Join-Path $programs "RenderFarm"
    New-Item -ItemType Directory -Path $menuFolder -Force | Out-Null
    New-Shortcut -Path (Join-Path $desktop "RenderFarm Launcher.lnk") -TargetPath $launcher -Arguments "" -WorkingDirectory $InstallRoot
    New-Shortcut -Path (Join-Path $menuFolder "RenderFarm Launcher.lnk") -TargetPath $launcher -Arguments "" -WorkingDirectory $InstallRoot
}

Write-Host "RenderFarm installed to $InstallRoot"
if ($Role) { Write-Host "Saved role '$Role' to $roleSettings" }
Write-Host "Start RenderFarm with:"
Write-Host "  & '$launcher'"
if (-not $Role) {
    Write-Host "No role was saved. First run opens the launcher role/settings screen."
}

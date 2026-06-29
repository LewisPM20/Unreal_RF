param(
    [ValidateSet("", "controller", "worker")]
    [string]$Role = "",
    [switch]$SaveRole,
    [switch]$ShowConfig,
    [string]$SettingsPath = "",
    [string]$HostName = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$Port = 9200,
    [string]$ControllerUrl = "",
    [string]$WorkerId = "",
    [string]$DisplayName = "",
    [string]$ApiToken = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

function Get-DefaultSettingsPath {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $PSScriptRoot "..\.renderfarm" }
    return Join-Path $base "RenderFarm\app-role.json"
}

function Read-RoleSettings {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-Content -Raw -Path $Path | ConvertFrom-Json
}

function Write-RoleSettings {
    param([string]$Path, [object]$Settings)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $Settings | ConvertTo-Json -Depth 5 | Set-Content -Path $Path
}

if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
    $SettingsPath = Get-DefaultSettingsPath
}

$stored = Read-RoleSettings -Path $SettingsPath
if ($ShowConfig) {
    Write-Host "RenderFarm role settings: $SettingsPath"
    if ($stored) { $stored | ConvertTo-Json -Depth 5 | Write-Host } else { Write-Host "No role settings have been saved yet." }
    if (-not $Role) { return }
}

if (-not $Role -and $stored -and $stored.role) { $Role = [string]$stored.role }
if (-not $ControllerUrl -and $stored -and $stored.controllerUrl) { $ControllerUrl = [string]$stored.controllerUrl }
if (-not $WorkerId -and $stored -and $stored.workerId) { $WorkerId = [string]$stored.workerId }
if (-not $DisplayName -and $stored -and $stored.displayName) { $DisplayName = [string]$stored.displayName }
if (-not $ApiToken -and $stored -and $stored.apiToken) { $ApiToken = [string]$stored.apiToken }
if (-not $PSBoundParameters.ContainsKey("HostName") -and $stored -and $stored.hostName) { $HostName = [string]$stored.hostName }
if (-not $PSBoundParameters.ContainsKey("Port") -and $stored -and $stored.port) { $Port = [int]$stored.port }

if (-not $Role) {
    Write-Host "RenderFarm needs a machine role before it can start."
    Write-Host "  Controller: central dashboard, queue, SQLite database, and scheduler."
    Write-Host "  Worker: render machine that heartbeats to the controller and runs assigned Unreal jobs."
    Write-Host ""
    Write-Host "Choose and save a role with one of these commands:"
    Write-Host "  .\scripts\start-renderfarm.ps1 -Role controller -SaveRole"
    Write-Host "  .\scripts\start-renderfarm.ps1 -Role worker -ControllerUrl http://CONTROLLER_IP:9200 -SaveRole"
    throw "No RenderFarm role was supplied or saved."
}

if ($SaveRole) {
    $settings = [ordered]@{
        role = $Role
        hostName = $HostName
        port = $Port
        controllerUrl = $ControllerUrl
        workerId = $WorkerId
        displayName = $DisplayName
        apiToken = $ApiToken
        updatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Write-RoleSettings -Path $SettingsPath -Settings $settings
    Write-Host "Saved RenderFarm role settings to $SettingsPath"
}

switch ($Role) {
    "controller" {
        $script = Join-Path $PSScriptRoot "start_controller.ps1"
        Write-Host "Launching this machine as the RenderFarm controller."
        if ($ApiToken) {
            & $script -HostName $HostName -Port $Port -Configuration $Configuration -ApiToken $ApiToken
        }
        else {
            & $script -HostName $HostName -Port $Port -Configuration $Configuration
        }
        exit $LASTEXITCODE
    }
    "worker" {
        $script = Join-Path $PSScriptRoot "start_worker.ps1"
        Write-Host "Launching this machine as a RenderFarm worker."
        & $script -Configuration $Configuration -ControllerUrl $ControllerUrl -WorkerId $WorkerId -DisplayName $DisplayName -ApiToken $ApiToken
        exit $LASTEXITCODE
    }
    default {
        throw "Unsupported RenderFarm role '$Role'. Valid roles are controller and worker."
    }
}



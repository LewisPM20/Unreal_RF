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
    [switch]$DiscoveryEnabled,
    [ValidateRange(1, 30)]
    [int]$DiscoverySeconds = 5,
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 39200,
    [switch]$LanScanEnabled,
    [ValidateRange(1, 30)]
    [int]$LanScanTimeoutSeconds = 4,
    [ValidateRange(1, 4096)]
    [int]$LanScanMaxHosts = 254,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("LanScanEnabled")) { $LanScanEnabled = $true }

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
if (-not $PSBoundParameters.ContainsKey("DiscoveryEnabled") -and $stored -and $null -ne $stored.discoveryEnabled) { if ([bool]$stored.discoveryEnabled) { $DiscoveryEnabled = $true } }
if (-not $PSBoundParameters.ContainsKey("DiscoverySeconds") -and $stored -and $stored.discoverySeconds) { $DiscoverySeconds = [int]$stored.discoverySeconds }
if (-not $PSBoundParameters.ContainsKey("DiscoveryPort") -and $stored -and $stored.discoveryPort) { $DiscoveryPort = [int]$stored.discoveryPort }
if (-not $PSBoundParameters.ContainsKey("LanScanEnabled") -and $stored -and $null -ne $stored.lanScanEnabled) { if ([bool]$stored.lanScanEnabled) { $LanScanEnabled = $true } }
if (-not $PSBoundParameters.ContainsKey("LanScanTimeoutSeconds") -and $stored -and $stored.lanScanTimeoutSeconds) { $LanScanTimeoutSeconds = [int]$stored.lanScanTimeoutSeconds }
if (-not $PSBoundParameters.ContainsKey("LanScanMaxHosts") -and $stored -and $stored.lanScanMaxHosts) { $LanScanMaxHosts = [int]$stored.lanScanMaxHosts }

if (-not $Role) {
    Write-Host "RenderFarm needs a machine role before it can start."
    Write-Host "  Controller: central dashboard, queue, SQLite database, and scheduler."
    Write-Host "  Worker: render machine that heartbeats to the controller and runs assigned Unreal jobs."
    Write-Host ""
    Write-Host "Choose and save a role with one of these commands:"
    Write-Host "  .\scripts\start-renderfarm.ps1 -Role controller -SaveRole"
    Write-Host "  .\scripts\start-renderfarm.ps1 -Role controller -HostName 0.0.0.0 -DiscoveryEnabled -SaveRole"
    Write-Host "  .\scripts\start-renderfarm.ps1 -Role worker -DiscoveryEnabled -SaveRole"
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
        discoveryEnabled = [bool]$DiscoveryEnabled
        discoverySeconds = $DiscoverySeconds
        discoveryPort = $DiscoveryPort
        lanScanEnabled = [bool]$LanScanEnabled
        lanScanTimeoutSeconds = $LanScanTimeoutSeconds
        lanScanMaxHosts = $LanScanMaxHosts
        updatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Write-RoleSettings -Path $SettingsPath -Settings $settings
    Write-Host "Saved RenderFarm role settings to $SettingsPath"
}

switch ($Role) {
    "controller" {
        $script = Join-Path $PSScriptRoot "start_controller.ps1"
        Write-Host "Launching this machine as the RenderFarm controller."
        $controllerArgs = @('-HostName', $HostName, '-Port', $Port, '-Configuration', $Configuration, '-DiscoveryPort', $DiscoveryPort)
        if ($ApiToken) { $controllerArgs += @('-ApiToken', $ApiToken) }
        if ($DiscoveryEnabled) { $controllerArgs += '-DiscoveryEnabled' }
        & $script @controllerArgs
        exit $LASTEXITCODE
    }
    "worker" {
        $script = Join-Path $PSScriptRoot "start_worker.ps1"
        Write-Host "Launching this machine as a RenderFarm worker."
        $workerArgs = @('-Configuration', $Configuration, '-ControllerUrl', $ControllerUrl, '-WorkerId', $WorkerId, '-DisplayName', $DisplayName, '-ApiToken', $ApiToken, '-DiscoverySeconds', $DiscoverySeconds, '-DiscoveryPort', $DiscoveryPort, '-ControllerPort', $Port, '-LanScanTimeoutSeconds', $LanScanTimeoutSeconds, '-LanScanMaxHosts', $LanScanMaxHosts)
        if ($DiscoveryEnabled) { $workerArgs += '-DiscoveryEnabled' }
        if ($LanScanEnabled) { $workerArgs += '-LanScanEnabled' }
        & $script @workerArgs
        exit $LASTEXITCODE
    }
    default {
        throw "Unsupported RenderFarm role '$Role'. Valid roles are controller and worker."
    }
}



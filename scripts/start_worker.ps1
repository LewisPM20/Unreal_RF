param(
    [string]$ControllerUrl = "",
    [string]$WorkerId = "",
    [string]$DisplayName = "",
    [string]$IdentityFilePath = "",
    [string]$ApiToken = "",
    [switch]$DiscoveryEnabled,
    [ValidateRange(1, 30)]
    [int]$DiscoverySeconds = 5,
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 39200,
    [string]$ServiceUrl = "http://127.0.0.1:9100",
    [ValidateRange(1, 3600)]
    [int]$HeartbeatSeconds = 5,
    [ValidateRange(1, 3600)]
    [int]$JobPollingSeconds = 5,
    [ValidateRange(1, 3600)]
    [int]$LeaseRenewalSeconds = 30,
    [ValidateRange(0, 604800)]
    [int]$RenderTimeoutSeconds = 0,
    [string]$AttemptLogRoot = "",
    [string]$WorkerStateFilePath = "",
    [string[]]$ProjectPaths = @(),
    [string[]]$SharedOutputRoots = @(),
    [Alias("UnrealEngineRoots", "UnrealEnginePaths", "UnrealExecutablePaths")]
    [string[]]$UnrealSearchRoots = @(),
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical")]
    [string]$LogLevel = "Information",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

function Assert-DotNetCli {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found. Install .NET 8 SDK, then rerun this script from the repository root."
    }
}

function Set-OptionalEnvironmentValue {
    param([string]$Name, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        Remove-Item -Path "Env:$Name" -ErrorAction SilentlyContinue
    }
    else {
        Set-Item -Path "Env:$Name" -Value $Value
    }
}

function Set-IndexedEnvironmentValues {
    param([string]$Prefix, [string[]]$Values)
    Get-ChildItem Env: | Where-Object { $_.Name -like "$Prefix*" } | ForEach-Object { Remove-Item -Path "Env:$($_.Name)" -ErrorAction SilentlyContinue }
    for ($i = 0; $i -lt $Values.Count; $i++) {
        if (-not [string]::IsNullOrWhiteSpace($Values[$i])) {
            Set-Item -Path "Env:${Prefix}${i}" -Value $Values[$i]
        }
    }
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project = Join-Path $RepoRoot "src\RenderFarm.Worker.Agent\RenderFarm.Worker.Agent.csproj"
if (-not (Test-Path -LiteralPath $Project)) {
    throw "Worker project was not found at $Project. Run this script from the checked-out RenderFarm repository."
}

Assert-DotNetCli

Set-OptionalEnvironmentValue -Name "RenderFarm__ControllerUrl" -Value $ControllerUrl
Set-OptionalEnvironmentValue -Name "RenderFarm__WorkerId" -Value $WorkerId
Set-OptionalEnvironmentValue -Name "RenderFarm__DisplayName" -Value $DisplayName
Set-OptionalEnvironmentValue -Name "RenderFarm__IdentityFilePath" -Value $IdentityFilePath
Set-OptionalEnvironmentValue -Name "RenderFarm__ApiToken" -Value $ApiToken
Set-OptionalEnvironmentValue -Name "RenderFarm__AttemptLogRoot" -Value $AttemptLogRoot
Set-OptionalEnvironmentValue -Name "RenderFarm__WorkerStateFilePath" -Value $WorkerStateFilePath
Set-OptionalEnvironmentValue -Name "Logging__LogLevel__Default" -Value $LogLevel
$env:RenderFarm__DiscoveryEnabled = [string][bool]$DiscoveryEnabled
$env:RenderFarm__DiscoverySeconds = [string]$DiscoverySeconds
$env:RenderFarm__DiscoveryPort = [string]$DiscoveryPort
$env:RenderFarm__ServiceUrl = $ServiceUrl
$env:RenderFarm__HeartbeatSeconds = [string]$HeartbeatSeconds
$env:RenderFarm__JobPollingSeconds = [string]$JobPollingSeconds
$env:RenderFarm__LeaseRenewalSeconds = [string]$LeaseRenewalSeconds
$env:RenderFarm__RenderTimeoutSeconds = [string]$RenderTimeoutSeconds
Set-IndexedEnvironmentValues -Prefix "RenderFarm__ProjectPaths__" -Values $ProjectPaths
Set-IndexedEnvironmentValues -Prefix "RenderFarm__SharedOutputRoots__" -Values $SharedOutputRoots
Set-IndexedEnvironmentValues -Prefix "RenderFarm__UnrealSearchRoots__" -Values $UnrealSearchRoots

$controllerLabel = if ($ControllerUrl) { $ControllerUrl } elseif ($DiscoveryEnabled) { "LAN discovery, then localhost fallback" } else { "localhost fallback http://127.0.0.1:9200" }
Write-Host "Starting RenderFarm worker"
if ($ProjectPaths.Count -gt 0 -or $SharedOutputRoots.Count -gt 0 -or $UnrealSearchRoots.Count -gt 0) {
    Write-Host "Legacy worker-side render path options were supplied. New production jobs receive Unreal, project, and output settings from the controller assignment payload." -ForegroundColor Yellow
}
Write-Host "  Controller: $controllerLabel"
Write-Host "  Service URL: $ServiceUrl"
Write-Host "  Project: $Project"
Write-Host "  Configuration: $Configuration"
Write-Host "  Log level: $LogLevel"
if ($WorkerId) { Write-Host "  Worker ID: $WorkerId" } else { Write-Host "  Worker ID: stable local identity file" }
if ($DisplayName) { Write-Host "  Display name: $DisplayName" }
if ($ApiToken) { Write-Host "  API token: configured" }
if ($ProjectPaths.Count -gt 0) { Write-Host "  Project paths: $($ProjectPaths -join ', ')" }
if ($SharedOutputRoots.Count -gt 0) { Write-Host "  Shared output roots: $($SharedOutputRoots -join ', ')" }
if ($UnrealSearchRoots.Count -gt 0) { Write-Host "  Unreal search roots/executables: $($UnrealSearchRoots -join ', ')" }
if ($RenderTimeoutSeconds -gt 0) { Write-Host "  Render timeout: $RenderTimeoutSeconds seconds" }

& dotnet run --project $Project --configuration $Configuration
exit $LASTEXITCODE

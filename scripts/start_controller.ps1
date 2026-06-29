param(
    [string]$HostName = "0.0.0.0",
    [ValidateRange(1, 65535)]
    [int]$Port = 9200,
    [switch]$DiscoveryEnabled,
    [string]$DiscoveryUrl = "",
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 39200,
    [string]$ApiToken = "",
    [string]$DatabasePath = "",
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

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project = Join-Path $RepoRoot "src\RenderFarm.Controller.Api\RenderFarm.Controller.Api.csproj"
if (-not (Test-Path -LiteralPath $Project)) {
    throw "Controller project was not found at $Project. Run this script from the checked-out RenderFarm repository."
}

Assert-DotNetCli

$Url = "http://${HostName}:${Port}"
$env:RenderFarm__Discovery__Enabled = [string][bool]$DiscoveryEnabled
$env:RenderFarm__Discovery__Port = [string]$DiscoveryPort
Set-OptionalEnvironmentValue -Name "RenderFarm__Discovery__ControllerUrl" -Value $DiscoveryUrl
Set-OptionalEnvironmentValue -Name "Logging__LogLevel__Default" -Value $LogLevel

if ($PSBoundParameters.ContainsKey("ApiToken")) {
    Set-OptionalEnvironmentValue -Name "RenderFarm__Security__ApiToken" -Value $ApiToken
}

if ($PSBoundParameters.ContainsKey("DatabasePath")) {
    Set-OptionalEnvironmentValue -Name "RenderFarm__Database__Path" -Value $DatabasePath
}

Write-Host "Starting RenderFarm controller"
Write-Host "  URL: $Url"
Write-Host "  Project: $Project"
Write-Host "  Configuration: $Configuration"
Write-Host "  Log level: $LogLevel"
if ($DiscoveryEnabled) { Write-Host "  Discovery: enabled on UDP $DiscoveryPort" }
if ($DiscoveryUrl) { Write-Host "  Discovery URL: $DiscoveryUrl" }
if ($ApiToken) { Write-Host "  API token protection: configured" }
if ($DatabasePath) { Write-Host "  Database: $DatabasePath" }

& dotnet run --project $Project --configuration $Configuration --urls $Url
exit $LASTEXITCODE

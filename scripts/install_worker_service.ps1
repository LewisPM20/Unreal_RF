param(
    [string]$InstallRoot = "",
    [string]$ServiceName = "RenderFarmWorker",
    [string]$ControllerUrl = "",
    [string]$WorkerId = "",
    [string]$DisplayName = "",
    [string]$ApiToken = "",
    [switch]$DiscoveryEnabled,
    [string]$UnrealSearchRoot = "",
    [string]$ProjectPath = "",
    [string]$SharedOutputRoot = "",
    [switch]$Start
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DefaultInstallRoot {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $env:USERPROFILE "AppData\Local" }
    return Join-Path $base "RenderFarm\Product"
}

function Escape-SingleQuotedPowerShell {
    param([string]$Value)
    if ($null -eq $Value) { return "" }
    return $Value.Replace("'", "''")
}

if (-not (Test-IsAdministrator)) {
    throw "Installing a Windows Service requires an elevated PowerShell session. Run as Administrator and try again."
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-DefaultInstallRoot }
$workerExe = Join-Path $InstallRoot "worker\RenderFarm.Worker.Agent.exe"
if (-not (Test-Path -LiteralPath $workerExe)) {
    throw "Worker executable was not found at $workerExe. Install or publish RenderFarm before installing the service."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Uninstall it first or choose a different -ServiceName."
}

$runner = Join-Path $InstallRoot "run-worker-service.ps1"
$workerDir = Split-Path -Parent $workerExe
$discoveryValue = [string][bool]$DiscoveryEnabled
$runnerContent = @"
`$ErrorActionPreference = "Stop"
Set-Location -LiteralPath '$(Escape-SingleQuotedPowerShell $workerDir)'
`$env:RenderFarm__ControllerUrl = '$(Escape-SingleQuotedPowerShell $ControllerUrl)'
`$env:RenderFarm__WorkerId = '$(Escape-SingleQuotedPowerShell $WorkerId)'
`$env:RenderFarm__DisplayName = '$(Escape-SingleQuotedPowerShell $DisplayName)'
`$env:RenderFarm__ApiToken = '$(Escape-SingleQuotedPowerShell $ApiToken)'
`$env:RenderFarm__DiscoveryEnabled = '$discoveryValue'
`$env:RenderFarm__UnrealSearchRoots__0 = '$(Escape-SingleQuotedPowerShell $UnrealSearchRoot)'
`$env:RenderFarm__ProjectPaths__0 = '$(Escape-SingleQuotedPowerShell $ProjectPath)'
`$env:RenderFarm__SharedOutputRoots__0 = '$(Escape-SingleQuotedPowerShell $SharedOutputRoot)'
& '$(Escape-SingleQuotedPowerShell $workerExe)'
exit `$LASTEXITCODE
"@
$runnerContent | Set-Content -Path $runner

$powerShell = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source
if (-not $powerShell) { $powerShell = (Get-Command pwsh.exe -ErrorAction Stop).Source }
$binaryPath = "`"$powerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$runner`""

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName "RenderFarm Worker" -Description "RenderFarm worker agent for Unreal render jobs." -StartupType Automatic | Out-Null
Write-Host "Installed worker service '$ServiceName'."
Write-Host "Runner script: $runner"
if ($Start) {
    Start-Service -Name $ServiceName
    Write-Host "Started worker service '$ServiceName'."
}
else {
    Write-Host "Start it with: Start-Service -Name $ServiceName"
}

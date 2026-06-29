param(
    [string]$ServiceName = "RenderFarmWorker",
    [switch]$RemoveRunner,
    [string]$InstallRoot = ""
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

if (-not (Test-IsAdministrator)) {
    throw "Removing a Windows Service requires an elevated PowerShell session. Run as Administrator and try again."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' was not installed."
}
else {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Write-Host "Stopped service '$ServiceName'."
    }

    & sc.exe delete $ServiceName | Out-Host
    Write-Host "Removed service '$ServiceName'."
}

if ($RemoveRunner) {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Get-DefaultInstallRoot }
    $runner = Join-Path $InstallRoot "run-worker-service.ps1"
    if (Test-Path -LiteralPath $runner) {
        Remove-Item -LiteralPath $runner -Force
        Write-Host "Removed worker service runner: $runner"
    }
}

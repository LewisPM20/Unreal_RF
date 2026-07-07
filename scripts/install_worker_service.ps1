param(
    [string]$InstallRoot = "",
    [string]$ServiceName = "RenderFarmWorker",
    [string]$ControllerUrl = "",
    [string]$WorkerId = "",
    [string]$DisplayName = "",
    [string]$ApiToken = "",
    [switch]$DiscoveryEnabled,
    [ValidateRange(1, 30)]
    [int]$DiscoverySeconds = 5,
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 39200,
    [ValidateRange(1, 65535)]
    [int]$ControllerPort = 9200,
    [switch]$LanScanEnabled,
    [ValidateRange(1, 30)]
    [int]$LanScanTimeoutSeconds = 4,
    [ValidateRange(1, 4096)]
    [int]$LanScanMaxHosts = 254,
    [string]$UnrealSearchRoot = "",
    [string]$ProjectPath = "",
    [string]$SharedOutputRoot = "",
    [switch]$Start
)

$ErrorActionPreference = "Stop"
if (-not $PSBoundParameters.ContainsKey("LanScanEnabled")) { $LanScanEnabled = $true }

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


$runner = Join-Path $InstallRoot "run-worker-service.ps1"
$workerDir = Split-Path -Parent $workerExe
$discoveryValue = [string][bool]$DiscoveryEnabled
$lanScanValue = [string][bool]$LanScanEnabled
$runnerContent = @"
`$ErrorActionPreference = "Stop"
Set-Location -LiteralPath '$(Escape-SingleQuotedPowerShell $workerDir)'
`$env:RenderFarm__ControllerUrl = '$(Escape-SingleQuotedPowerShell $ControllerUrl)'
`$env:RenderFarm__WorkerId = '$(Escape-SingleQuotedPowerShell $WorkerId)'
`$env:RenderFarm__DisplayName = '$(Escape-SingleQuotedPowerShell $DisplayName)'
`$env:RenderFarm__ApiToken = '$(Escape-SingleQuotedPowerShell $ApiToken)'
`$env:RenderFarm__DiscoveryEnabled = '$discoveryValue'
`$env:RenderFarm__DiscoverySeconds = '$DiscoverySeconds'
`$env:RenderFarm__DiscoveryPort = '$DiscoveryPort'
`$env:RenderFarm__ControllerPort = '$ControllerPort'
`$env:RenderFarm__LanScanEnabled = '$lanScanValue'
`$env:RenderFarm__LanScanTimeoutSeconds = '$LanScanTimeoutSeconds'
`$env:RenderFarm__LanScanMaxHosts = '$LanScanMaxHosts'
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

$existingService = Get-CimInstance Win32_Service -Filter "Name='$($ServiceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Existing worker service '$ServiceName' found."
    Write-Host "Current service path: $($existingService.PathName)"
    Write-Host "Reinstalling service so it points at: $binaryPath"
    if ($existingService.State -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 500
            $state = (Get-CimInstance Win32_Service -Filter "Name='$($ServiceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue).State
        } while ($state -and $state -ne "Stopped" -and (Get-Date) -lt $deadline)
    }

    & sc.exe delete $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $stillExists = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    } while ($stillExists -and (Get-Date) -lt $deadline)

    if ($stillExists) {
        throw "Service '$ServiceName' could not be removed cleanly. Reboot or stop the service manually, then rerun this script."
    }
}
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




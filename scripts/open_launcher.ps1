param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 9200,
    [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$ControllerScript = Join-Path $PSScriptRoot "start_controller.ps1"
$Url = "http://${HostName}:${Port}"
Start-Process powershell.exe -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-File", $ControllerScript, "-HostName", $HostName, "-Port", $Port, "-Configuration", $Configuration)
Start-Sleep -Seconds 3
Start-Process $Url
Write-Host "Started C# controller launcher for $Url"

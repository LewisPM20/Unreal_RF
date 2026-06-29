[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 9200,
    [string]$RuleName = "RenderFarm Controller HTTP 9200",
    [switch]$Remove,
    [switch]$Accept
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Changing Windows Firewall requires an elevated PowerShell session. Run as Administrator and try again."
}

if (-not (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
    throw "Windows Firewall PowerShell cmdlets were not found on this machine."
}

if (-not $Accept) {
    Write-Host "RenderFarm firewall helper is opt-in."
    Write-Host "It will allow inbound TCP traffic to the controller on port $Port for trusted LAN workers."
    Write-Host "Run again with -Accept to apply the change, or -Remove -Accept to remove it."
    exit 2
}

$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
if ($Remove) {
    if ($existing) {
        if ($PSCmdlet.ShouldProcess($RuleName, "Remove firewall rule")) {
            $existing | Remove-NetFirewallRule
            Write-Host "Removed firewall rule '$RuleName'."
        }
    }
    else {
        Write-Host "Firewall rule '$RuleName' was not present."
    }
    return
}

if ($existing) {
    Write-Host "Firewall rule '$RuleName' already exists."
    return
}

if ($PSCmdlet.ShouldProcess("TCP port $Port", "Allow RenderFarm controller inbound traffic")) {
    New-NetFirewallRule `
        -DisplayName $RuleName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Private `
        -Description "Allows trusted LAN workers to reach the RenderFarm controller dashboard/API." | Out-Null
    Write-Host "Created firewall rule '$RuleName' for inbound TCP port $Port on Private networks."
}

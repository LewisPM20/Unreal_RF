[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [ValidateSet("Controller", "Worker", "Both")]
    [string]$Role = "Controller",
    [ValidateRange(1, 65535)]
    [int]$Port = 9200,
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 39200,
    [ValidateSet("Domain", "Private", "Public", "Any")]
    [string[]]$Profile = @("Domain", "Private"),
    [switch]$Remove,
    [switch]$Accept
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ActiveProfileWarning {
    if (-not (Get-Command Get-NetConnectionProfile -ErrorAction SilentlyContinue)) { return }
    $publicProfiles = Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" }
    if ($publicProfiles) {
        Write-Warning "One or more active Windows network profiles are Public. RenderFarm LAN discovery and worker connections are intended for trusted Private or Domain networks."
    }
}

function Ensure-FirewallRule {
    param(
        [string]$DisplayName,
        [ValidateSet("Inbound", "Outbound")]
        [string]$Direction,
        [ValidateSet("TCP", "UDP")]
        [string]$Protocol,
        [int]$LocalPort = 0,
        [int]$RemotePort = 0,
        [string]$Description
    )

    $existing = Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue
    if ($Remove) {
        if ($existing) {
            if ($PSCmdlet.ShouldProcess($DisplayName, "Remove firewall rule")) {
                $existing | Remove-NetFirewallRule
                Write-Host "Removed firewall rule '$DisplayName'."
            }
        }
        else {
            Write-Host "Firewall rule '$DisplayName' was not present."
        }
        return
    }

    if ($existing) {
        Write-Host "Firewall rule '$DisplayName' already exists."
        return
    }

    $ruleArgs = @{
        DisplayName = $DisplayName
        Direction = $Direction
        Action = "Allow"
        Protocol = $Protocol
        Profile = ($Profile -join ",")
        Description = $Description
    }
    if ($LocalPort -gt 0) { $ruleArgs.LocalPort = $LocalPort }
    if ($RemotePort -gt 0) { $ruleArgs.RemotePort = $RemotePort }

    if ($PSCmdlet.ShouldProcess($DisplayName, "Create RenderFarm firewall rule")) {
        New-NetFirewallRule @ruleArgs | Out-Null
        Write-Host "Created firewall rule '$DisplayName'."
    }
}

if (-not (Test-IsAdministrator)) {
    throw "Changing Windows Firewall requires an elevated PowerShell session. Run as Administrator and try again."
}

if (-not (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
    throw "Windows Firewall PowerShell cmdlets were not found on this machine."
}

if (-not $Accept) {
    Write-Host "RenderFarm firewall helper is opt-in."
    Write-Host "Controller role: allow inbound TCP $Port and outbound UDP discovery $DiscoveryPort."
    Write-Host "Worker role: allow inbound UDP discovery $DiscoveryPort and outbound TCP $Port."
    Write-Host "Profiles: $($Profile -join ', '). Private/Domain are recommended; Public networks are not recommended."
    Write-Host "Run again with -Accept to apply changes, or -Remove -Accept to remove RenderFarm rules."
    exit 2
}

Get-ActiveProfileWarning

if ($Role -in @("Controller", "Both")) {
    Ensure-FirewallRule -DisplayName "RenderFarm Controller HTTP $Port" -Direction Inbound -Protocol TCP -LocalPort $Port -Description "Allows trusted LAN workers to reach the RenderFarm controller dashboard/API."
    Ensure-FirewallRule -DisplayName "RenderFarm Controller Discovery UDP $DiscoveryPort" -Direction Outbound -Protocol UDP -RemotePort $DiscoveryPort -Description "Allows the RenderFarm controller to broadcast LAN discovery announcements."
}

if ($Role -in @("Worker", "Both")) {
    Ensure-FirewallRule -DisplayName "RenderFarm Worker Discovery UDP $DiscoveryPort" -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort -Description "Allows this worker to receive RenderFarm controller discovery broadcasts."
    Ensure-FirewallRule -DisplayName "RenderFarm Worker Controller HTTP $Port" -Direction Outbound -Protocol TCP -RemotePort $Port -Description "Allows this worker to connect to the RenderFarm controller API."
}

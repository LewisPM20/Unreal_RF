[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$PackageRoot = ".\publish\RenderFarm",
    [string]$CertificateThumbprint = "",
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $direct = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($direct) { return $direct.Source }

    $windowsKits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $windowsKits) {
        $candidate = Get-ChildItem -LiteralPath $windowsKits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "signtool.exe was not found. Install the Windows SDK or add signtool to PATH."
}

$root = Resolve-Path -Path $PackageRoot -ErrorAction SilentlyContinue
if (-not $root) { throw "Package root was not found at $PackageRoot." }
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -and [string]::IsNullOrWhiteSpace($PfxPath)) {
    throw "Provide -CertificateThumbprint or -PfxPath. RenderFarm will not fake a code signature."
}

$signtool = Find-SignTool
$patterns = @('*.exe', '*.dll')
$files = foreach ($pattern in $patterns) { Get-ChildItem -LiteralPath $root.Path -Recurse -File -Filter $pattern }
if ($files.Count -eq 0) { throw "No signable files were found under $($root.Path)." }

foreach ($file in $files) {
    $args = @('sign', '/fd', 'SHA256', '/tr', $TimestampServer, '/td', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $resolvedPfx = Resolve-Path -Path $PfxPath -ErrorAction Stop
        $args += @('/f', $resolvedPfx.Path)
        if (-not [string]::IsNullOrWhiteSpace($PfxPassword)) { $args += @('/p', $PfxPassword) }
    }
    else {
        $args += @('/sha1', $CertificateThumbprint)
    }
    $args += $file.FullName

    if ($PSCmdlet.ShouldProcess($file.FullName, "Sign binary")) {
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

Write-Host "Signed $($files.Count) file(s)."
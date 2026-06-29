[CmdletBinding(PositionalBinding=$false)]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ProductVersion = "0.1.0",
    [string]$PublishRoot = "",
    [string]$DistRoot = "",
    [string]$InnoSetupCompilerPath = "",
    [switch]$NoSingleFile,
    [switch]$IncludeDocs,
    [switch]$Zip,
    [switch]$AllowBuildOutputFallback,
    [switch]$AllowMissingRuntimeInstaller,
    [switch]$UseSetupIcon,
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$RemainingArguments
)

$ErrorActionPreference = "Stop"

function Apply-LongOptions {
    param([string[]]$Arguments)

    $valueOptions = @("configuration", "runtime", "productversion", "publishroot", "distroot", "innosetupcompilerpath")

    $index = 0
    while ($index -lt $Arguments.Count) {
        $arg = $Arguments[$index]
        if (-not $arg.StartsWith("--", [System.StringComparison]::Ordinal)) {
            throw "Unrecognized argument: $arg. Use -Name value or --Name value."
        }

        $raw = $arg.Substring(2)
        $inlineValue = $null
        $equals = $raw.IndexOf('=')
        if ($equals -ge 0) {
            $inlineValue = $raw.Substring($equals + 1)
            $raw = $raw.Substring(0, $equals)
        }

        $name = $raw.ToLowerInvariant()
        $value = $inlineValue
        if ($valueOptions -contains $name) {
            if ($null -eq $value) {
                if ($index + 1 -ge $Arguments.Count) { throw "Option --$raw requires a value." }
                $index += 1
                $value = $Arguments[$index]
            }
        }
        elseif ($null -ne $value) {
            throw "Option --$raw does not take a value."
        }

        switch ($name) {
            "configuration" { $script:Configuration = $value }
            "runtime" { $script:Runtime = $value }
            "productversion" { $script:ProductVersion = $value }
            "publishroot" { $script:PublishRoot = $value }
            "distroot" { $script:DistRoot = $value }
            "innosetupcompilerpath" { $script:InnoSetupCompilerPath = $value }
            "nosinglefile" { $script:NoSingleFile = $true }
            "includedocs" { $script:IncludeDocs = $true }
            "zip" { $script:Zip = $true }
            "allowbuildoutputfallback" { $script:AllowBuildOutputFallback = $true }
            "allowmissingruntimeinstaller" { $script:AllowMissingRuntimeInstaller = $true }
            "usesetupicon" { $script:UseSetupIcon = $true }
            default { throw "Unrecognized option --$raw." }
        }

        $index += 1
    }
}
function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Find-InnoSetupCompiler {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = Resolve-Path -Path $ExplicitPath -ErrorAction SilentlyContinue
        if ($resolved) { return $resolved.Path }
        throw "ISCC.exe was not found at $ExplicitPath."
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 or pass -InnoSetupCompilerPath."
}

function Find-RuntimeInstaller {
    param([string]$RepoRoot)

    $redist = Join-Path $RepoRoot "installer\redist"
    if (-not (Test-Path -LiteralPath $redist)) { return $null }

    return Get-ChildItem -LiteralPath $redist -File -Filter "windowsdesktop-runtime-8.*-win-x64.exe" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
}

function Get-DirectorySize {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0L }
    return [long]((Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum)
}

function Format-ByteSize {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

if ($RemainingArguments.Count -gt 0) {
    Apply-LongOptions -Arguments $RemainingArguments
}

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($PublishRoot)) { $PublishRoot = Join-Path $repoRoot "publish" }
if ([string]::IsNullOrWhiteSpace($DistRoot)) { $DistRoot = Join-Path $repoRoot "dist" }
$PublishRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishRoot)
$DistRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DistRoot)
New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null

$publishScript = Join-Path $repoRoot "scripts\publish_apps.ps1"
Write-Host "Publishing framework-dependent RenderFarm package for $Runtime..."
Write-Host "Installer publish settings: SelfContained=false, PublishSingleFile=$(-not $NoSingleFile)"
& $publishScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Mode framework-dependent `
    -PublishRoot $PublishRoot `
    -SingleFile "true" `
    -NoSingleFile:$NoSingleFile `
    -IncludeDocs:$IncludeDocs `
    -Zip:$Zip `
    -AllowBuildOutputFallback:$AllowBuildOutputFallback
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packageRoot = Join-Path $PublishRoot "RenderFarm"
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot "RenderFarm.Launcher.exe"))) {
    throw "Published package is missing RenderFarm.Launcher.exe at $packageRoot."
}

$runtimeInstaller = Find-RuntimeInstaller -RepoRoot $repoRoot
if (-not $runtimeInstaller -and -not $AllowMissingRuntimeInstaller) {
    throw "The .NET 8 Desktop Runtime installer was not found. Place windowsdesktop-runtime-8.x.x-win-x64.exe under installer\redist, then rerun this script. Use -AllowMissingRuntimeInstaller only for internal compiler smoke tests."
}

$iscc = Find-InnoSetupCompiler -ExplicitPath $InnoSetupCompilerPath
$issPath = Join-Path $repoRoot "installer\RenderFarm.iss"
if (-not (Test-Path -LiteralPath $issPath)) { throw "Inno Setup script was not found at $issPath." }

$innoArgs = @(
    "/Qp",
    "/DSourceDir=$packageRoot",
    "/DMyAppVersion=$ProductVersion",
    "/DOutputDir=$DistRoot"
)
if (-not $UseSetupIcon) { $innoArgs += "/DSkipSetupIcon=1" }
if ($runtimeInstaller) {
    $innoArgs += "/DRuntimeInstallerName=$($runtimeInstaller.Name)"
    Write-Host "Bundling runtime installer: $($runtimeInstaller.FullName) ($(Format-ByteSize $runtimeInstaller.Length))"
}
else {
    Write-Warning "Building setup without bundled .NET Desktop Runtime. Target machines must install it manually."
}

Write-Host "Compiling installer with $iscc"
& $iscc @innoArgs $issPath
if ($LASTEXITCODE -ne 0) {
    if ($UseSetupIcon) {
        Write-Warning "Inno Setup failed while applying the custom setup icon. Retrying with the default setup icon while keeping the installed RenderFarm launcher icon."
        $retryArgs = @($innoArgs) + "/DSkipSetupIcon=1"
        & $iscc @retryArgs $issPath
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    else {
        exit $LASTEXITCODE
    }
}

$setupPath = Join-Path $DistRoot "RenderFarmSetup-$ProductVersion-$Runtime.exe"
Write-Host "Package output: $packageRoot ($(Format-ByteSize (Get-DirectorySize $packageRoot)))"
if (Test-Path -LiteralPath $setupPath) {
    $setupItem = Get-Item -LiteralPath $setupPath
    Write-Host "Installer output: $($setupItem.FullName) ($(Format-ByteSize $setupItem.Length))"
}
else {
    Write-Warning "Expected installer output was not found at $setupPath. Check Inno Setup output above."
}
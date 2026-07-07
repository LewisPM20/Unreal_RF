[CmdletBinding(PositionalBinding=$false)]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [ValidateSet("framework-dependent", "self-contained")]
    [string]$Mode = "framework-dependent",
    [switch]$SelfContained,
    [switch]$Portable,
    [string]$SingleFile = "",
    [switch]$NoSingleFile,
    [string]$PublishRoot = "",
    [switch]$Zip,
    [switch]$IncludeDocs,
    [switch]$IncludeSymbols,
    [switch]$AllowBuildOutputFallback,
    [switch]$Sign,
    [string]$CertificateThumbprint = "",
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [Parameter(ValueFromRemainingArguments=$true)]
    [string[]]$RemainingArguments
)

$ErrorActionPreference = "Stop"

function Assert-DotNetCli {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found. Install the .NET 8 SDK before publishing RenderFarm."
    }
}

function Apply-LongOptions {
    param([string[]]$Arguments)

    $valueOptions = @(
        "configuration", "runtime", "mode", "singlefile", "publishroot",
        "certificatethumbprint", "pfxpath", "pfxpassword", "timestampserver"
    )

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
            "mode" { $script:Mode = $value }
            "selfcontained" { $script:SelfContained = $true }
            "portable" { $script:Portable = $true }
            "singlefile" { $script:SingleFile = $value }
            "nosinglefile" { $script:NoSingleFile = $true }
            "publishroot" { $script:PublishRoot = $value }
            "zip" { $script:Zip = $true }
            "includedocs" { $script:IncludeDocs = $true }
            "includesymbols" { $script:IncludeSymbols = $true }
            "allowbuildoutputfallback" { $script:AllowBuildOutputFallback = $true }
            "sign" { $script:Sign = $true }
            "certificatethumbprint" { $script:CertificateThumbprint = $value }
            "pfxpath" { $script:PfxPath = $value }
            "pfxpassword" { $script:PfxPassword = $value }
            "timestampserver" { $script:TimestampServer = $value }
            default { throw "Unrecognized option --$raw." }
        }

        $index += 1
    }
}
function Convert-ToBooleanOption {
    param([string]$Value, [bool]$Default)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Default }

    switch ($Value.Trim().ToLowerInvariant()) {
        "true" { return $true }
        "1" { return $true }
        "yes" { return $true }
        "false" { return $false }
        "0" { return $false }
        "no" { return $false }
        default { throw "SingleFile must be true/false, 1/0, yes/no, or omitted. Use -NoSingleFile to disable single-file publish." }
    }
}

function Test-RestoredRuntimeTarget {
    param(
        [string]$ProjectPath,
        [string]$TargetFramework,
        [string]$Runtime
    )

    $assetsPath = Join-Path (Split-Path -Parent $ProjectPath) "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath)) { return $false }

    $target = if ([string]::IsNullOrWhiteSpace($Runtime)) { $TargetFramework } else { "$TargetFramework/$Runtime" }
    try {
        $assets = Get-Content -Raw -Path $assetsPath
        return $assets.Contains('"' + $target + '"')
    }
    catch {
        return $false
    }
}
function Reset-Directory {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "Could not fully clean $Path. Close running RenderFarm processes or choose a fresh -PublishRoot. $($_.Exception.Message)"
    }
}

function Copy-DirectoryContents {
    param(
        [string]$Source,
        [string]$Destination,
        [switch]$IncludeSymbols
    )

    if (-not (Test-Path -LiteralPath $Source)) { return }
    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        $target = Join-Path $Destination $relative

        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            return
        }

        if (-not $IncludeSymbols -and $_.Extension -ieq ".pdb") { return }
        $targetDirectory = Split-Path -Parent $target
        if ($targetDirectory) { New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
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

function Publish-RenderFarmProject {
    param(
        [string]$ProjectPath,
        [string]$TargetFramework,
        [string]$OutputPath,
        [string]$Configuration,
        [string]$Runtime,
        [string]$SelfContainedValue,
        [string]$SingleFileValue,
        [switch]$AllowBuildOutputFallback
    )

    Reset-Directory -Path $OutputPath
    $publishArgs = @(
        "publish", $ProjectPath,
        "-c", $Configuration,
        "-f", $TargetFramework,
        "-o", $OutputPath,
        "--no-restore",
        "-m:1",
        "-nr:false",
        "-p:PublishSingleFile=$SingleFileValue",
        "-p:SelfContained=$SelfContainedValue",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishReadyToRun=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )

    if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
        $publishArgs += @("-r", $Runtime, "--self-contained", $SelfContainedValue)
    }

    if ($SelfContainedValue -eq "true" -and $SingleFileValue -eq "true") {
        $publishArgs += "-p:EnableCompressionInSingleFile=true"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -eq 0) { return }

    if (-not $AllowBuildOutputFallback) {
        exit $LASTEXITCODE
    }

    Write-Warning "dotnet publish failed for $ProjectPath. Falling back to existing build output for internal verification. Run dotnet build first if this output is missing."

    $projectDirectory = Split-Path -Parent $ProjectPath
    $buildOutput = Join-Path $projectDirectory "bin\$Configuration\$TargetFramework"
    if (-not [string]::IsNullOrWhiteSpace($Runtime) -and (Test-Path -LiteralPath (Join-Path $buildOutput $Runtime))) {
        $buildOutput = Join-Path $buildOutput $Runtime
    }

    if (-not (Test-Path -LiteralPath $buildOutput)) {
        throw "Build output was not found at $buildOutput."
    }

    Copy-DirectoryContents -Source $buildOutput -Destination $OutputPath -IncludeSymbols:$IncludeSymbols
}

if ($RemainingArguments.Count -gt 0) {
    Apply-LongOptions -Arguments $RemainingArguments
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $RepoRoot "publish"
}
else {
    $PublishRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishRoot)
}

Assert-DotNetCli

$useSingleFile = Convert-ToBooleanOption -Value $SingleFile -Default $true
if ($Portable) {
    $Runtime = ""
    $Mode = "framework-dependent"
    $useSingleFile = $false
}

if ($NoSingleFile) { $useSingleFile = $false }
if (($SelfContained -or $Mode -eq "self-contained") -and [string]::IsNullOrWhiteSpace($Runtime)) {
    throw "Self-contained publishing requires -Runtime, for example -Runtime win-x64."
}

$selfContainedValue = if ($SelfContained -or $Mode -eq "self-contained") { "true" } else { "false" }
$singleFileValue = if ($useSingleFile) { "true" } else { "false" }
$runtimeLabel = if ($Runtime) { $Runtime } else { "portable" }
$ControllerOut = Join-Path $PublishRoot "controller"
$WorkerOut = Join-Path $PublishRoot "worker"
$LauncherOut = Join-Path $PublishRoot "launcher"
$PackageRoot = Join-Path $PublishRoot "RenderFarm"

New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

$projectsToRestore = @(
    @{ Path = (Join-Path $RepoRoot "src\RenderFarm.Controller.Api\RenderFarm.Controller.Api.csproj"); Framework = "net8.0" },
    @{ Path = (Join-Path $RepoRoot "src\RenderFarm.Worker.Agent\RenderFarm.Worker.Agent.csproj"); Framework = "net8.0" },
    @{ Path = (Join-Path $RepoRoot "src\RenderFarm.Launcher\RenderFarm.Launcher.csproj"); Framework = "net8.0-windows" }
)
Write-Host "Restoring RenderFarm package assets ($runtimeLabel, self-contained=$selfContainedValue)..."
foreach ($projectToRestore in $projectsToRestore) {
    if (Test-RestoredRuntimeTarget -ProjectPath $projectToRestore.Path -TargetFramework $projectToRestore.Framework -Runtime $Runtime) {
        Write-Host "Restore already has target $($projectToRestore.Framework)/$runtimeLabel for $([System.IO.Path]::GetFileName($projectToRestore.Path)); skipping."
        continue
    }

    $restoreArgs = @("restore", $projectToRestore.Path, "-m:1", "-nr:false", "-p:SelfContained=$selfContainedValue")
    if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
        $restoreArgs += @("-r", $Runtime)
    }

    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host "Publishing RenderFarm ($Configuration, $runtimeLabel, self-contained=$selfContainedValue, single-file=$singleFileValue)"
Publish-RenderFarmProject -ProjectPath (Join-Path $RepoRoot "src\RenderFarm.Controller.Api\RenderFarm.Controller.Api.csproj") -TargetFramework "net8.0" -Configuration $Configuration -Runtime $Runtime -SelfContainedValue $selfContainedValue -SingleFileValue $singleFileValue -OutputPath $ControllerOut -AllowBuildOutputFallback:$AllowBuildOutputFallback
Publish-RenderFarmProject -ProjectPath (Join-Path $RepoRoot "src\RenderFarm.Worker.Agent\RenderFarm.Worker.Agent.csproj") -TargetFramework "net8.0" -Configuration $Configuration -Runtime $Runtime -SelfContainedValue $selfContainedValue -SingleFileValue $singleFileValue -OutputPath $WorkerOut -AllowBuildOutputFallback:$AllowBuildOutputFallback
Publish-RenderFarmProject -ProjectPath (Join-Path $RepoRoot "src\RenderFarm.Launcher\RenderFarm.Launcher.csproj") -TargetFramework "net8.0-windows" -Configuration $Configuration -Runtime $Runtime -SelfContainedValue $selfContainedValue -SingleFileValue $singleFileValue -OutputPath $LauncherOut -AllowBuildOutputFallback:$AllowBuildOutputFallback

Reset-Directory -Path $PackageRoot
New-Item -ItemType Directory -Path $PackageRoot -Force | Out-Null
Copy-DirectoryContents -Source $LauncherOut -Destination $PackageRoot -IncludeSymbols:$IncludeSymbols
Copy-DirectoryContents -Source $ControllerOut -Destination (Join-Path $PackageRoot "controller") -IncludeSymbols:$IncludeSymbols
Copy-DirectoryContents -Source $WorkerOut -Destination (Join-Path $PackageRoot "worker") -IncludeSymbols:$IncludeSymbols
Copy-DirectoryContents -Source (Join-Path $RepoRoot "packaging\config") -Destination (Join-Path $PackageRoot "config")
Copy-DirectoryContents -Source (Join-Path $RepoRoot "packaging\assets") -Destination (Join-Path $PackageRoot "assets")
if ($IncludeDocs) {
    Copy-DirectoryContents -Source (Join-Path $RepoRoot "docs") -Destination (Join-Path $PackageRoot "docs")
}

$InstallerRoot = Join-Path $PackageRoot "installer"
New-Item -ItemType Directory -Path $InstallerRoot -Force | Out-Null
$installerScripts = @(
    "install_renderfarm.ps1",
    "uninstall_renderfarm.ps1",
    "install_worker_service.ps1",
    "uninstall_worker_service.ps1",
    "configure_controller_firewall.ps1",
    "verify_upgrade_settings.ps1",
    "sign_release.ps1"
)
foreach ($script in $installerScripts) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot "scripts\$script") -Destination $InstallerRoot -Force
}

@"
# RenderFarm Package

This is the framework-dependent Windows package for RenderFarm. Install the Microsoft .NET 8 Desktop Runtime on target PCs, or build the Inno Setup installer from the repo so the runtime installer can be bundled.

Launch the operator UI:

~~~powershell
.\RenderFarm.Launcher.exe
~~~

Install for the current user:

~~~powershell
.\installer\install_renderfarm.ps1 -Role controller -CreateShortcuts
~~~

Worker example:

~~~powershell
.\installer\install_renderfarm.ps1 -Role worker -ControllerUrl http://<controller-lan-ip>:9200 -WorkerId worker-pc-01 -CreateShortcuts
~~~

Uninstall the package from Windows Apps, or use the packaged script for a manual cleanup:

~~~powershell
.\installer\uninstall_renderfarm.ps1 -RemoveShortcuts
~~~

Render outputs are never removed by default. Installed settings live under `%LOCALAPPDATA%\RenderFarm` and survive package replacement. Configure Unreal, project, profile, and shared-output settings from the controller dashboard; workers only need connection identity.
"@ | Set-Content -Path (Join-Path $PackageRoot "README_PACKAGE.md")

if ($Sign) {
    $signScript = Join-Path $RepoRoot "scripts\sign_release.ps1"
    $signArgs = @("-PackageRoot", $PackageRoot, "-TimestampServer", $TimestampServer)
    if ($CertificateThumbprint) { $signArgs += @("-CertificateThumbprint", $CertificateThumbprint) }
    if ($PfxPath) { $signArgs += @("-PfxPath", $PfxPath) }
    if ($PfxPassword) { $signArgs += @("-PfxPassword", $PfxPassword) }
    & $signScript @signArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Zip) {
    $zipPath = Join-Path $PublishRoot "RenderFarm-$runtimeLabel-$Configuration.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $PackageRoot "*") -DestinationPath $zipPath -Force
    Write-Host "Created package archive: $zipPath ($(Format-ByteSize ((Get-Item -LiteralPath $zipPath).Length)))"
}

Write-Host "Published controller to $ControllerOut"
Write-Host "Published worker to $WorkerOut"
Write-Host "Published launcher to $LauncherOut"
Write-Host "Created distributable package at $PackageRoot ($(Format-ByteSize (Get-DirectorySize $PackageRoot)))"
if ($selfContainedValue -eq "false") { Write-Host "Framework-dependent build: target PCs need the .NET 8 Desktop Runtime." }
$global:LASTEXITCODE = 0






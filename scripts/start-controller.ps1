$ErrorActionPreference = "Stop"
$Target = Join-Path $PSScriptRoot "start_controller.ps1"
& $Target @args
exit $LASTEXITCODE

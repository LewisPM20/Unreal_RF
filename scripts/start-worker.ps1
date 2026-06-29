$ErrorActionPreference = "Stop"
$Target = Join-Path $PSScriptRoot "start_worker.ps1"
& $Target @args
exit $LASTEXITCODE

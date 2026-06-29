$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Write-Host "Restoring RenderFarm .NET dependencies..."
& dotnet restore (Join-Path $RepoRoot "RenderFarm.sln")
Write-Host "RenderFarm .NET dependencies restored. Python runtime dependencies are retired."

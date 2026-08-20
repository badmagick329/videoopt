[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDirectory = Join-Path $repositoryRoot 'artifacts\local'
$project = Join-Path $repositoryRoot 'src\VideoOptimiser.Cli'

& dotnet publish $project `
    '-p:PublishProfile=win-x64-aot' `
    '-p:Version=0.0.0-local' `
    '-p:InformationalVersion=0.0.0-local' `
    '-o' $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Native AOT publish failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $publishDirectory 'video-optimiser.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Publish completed without producing '$executable'."
}

& $executable --version
if ($LASTEXITCODE -ne 0) {
    throw "The published executable failed its version check with exit code $LASTEXITCODE."
}

Write-Host "Built executable: $executable"

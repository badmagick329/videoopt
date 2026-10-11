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
& (Join-Path $PSScriptRoot 'smoke-test.ps1') -Executable $executable -ExpectedVersion '0.0.0-local'

Write-Host "Built executable: $executable"

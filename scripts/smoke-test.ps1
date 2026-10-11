[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable,

    # When set, --version must report this version, so a release cannot ship a binary stamped with another one.
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# release.ps1 -WhatIf would otherwise turn the temp-directory setup and clean-up into no-ops.
$WhatIfPreference = $false

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Executable '$Executable' does not exist."
}

$versionOutput = (& $Executable --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "--version failed with exit code $LASTEXITCODE."
}

if ($ExpectedVersion -and $versionOutput -notmatch "^video-optimiser $([regex]::Escape($ExpectedVersion))(\+|$)") {
    throw "Expected --version to report $ExpectedVersion; got '$versionOutput'."
}

Write-Host $versionOutput

# A fresh template is deliberately invalid until its roots are filled in, so validate must return 3.
$configDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "video-optimiser-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $configDirectory | Out-Null
try {
    $config = Join-Path $configDirectory 'video-optimiser.yaml'
    & $Executable config init --config $config
    if ($LASTEXITCODE -ne 0) {
        throw "config init failed with exit code $LASTEXITCODE."
    }

    & $Executable config validate --config $config
    if ($LASTEXITCODE -ne 3) {
        throw "A fresh template should return exit code 3 from config validate; got $LASTEXITCODE."
    }

    # The expected 3 would otherwise leak to callers: GitHub Actions ends a pwsh step with exit $LASTEXITCODE.
    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item -LiteralPath $configDirectory -Recurse -Force
}

Write-Host 'Smoke test passed.'

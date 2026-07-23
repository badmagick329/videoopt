[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repositoryRoot

function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

$tag = "v$Version"
$status = @(git status --porcelain)
foreach ($entry in $status) {
    if ($entry -notmatch '^.M CHANGELOG\.md$' -and $entry -notmatch '^M. CHANGELOG\.md$') {
        throw "Worktree must be clean before release. Only CHANGELOG.md may be modified."
    }
}

if ((git tag --list $tag) -contains $tag) {
    throw "Tag '$tag' already exists."
}

$changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'
$changelog = Get-Content -LiteralPath $changelogPath -Raw
$unreleased = [regex]::Match($changelog, '(?ms)^## \[Unreleased\]\s*\r?\n(?<notes>.*?)(?=^## |\z)')
if (-not $unreleased.Success -or [string]::IsNullOrWhiteSpace($unreleased.Groups['notes'].Value) -or $unreleased.Groups['notes'].Value -notmatch '(?m)^- ') {
    throw 'CHANGELOG.md must contain a non-empty Unreleased section.'
}

$date = (Get-Date).ToString('yyyy-MM-dd')
$releasedChangelog = $changelog.Substring(0, $unreleased.Index) +
    "## [Unreleased]`r`n`r`n## [$Version] - $date`r`n" +
    $unreleased.Groups['notes'].Value +
    $changelog.Substring($unreleased.Index + $unreleased.Length)

if ($PSCmdlet.ShouldProcess($changelogPath, "Promote Unreleased to $Version")) {
    Set-Content -LiteralPath $changelogPath -Value $releasedChangelog -NoNewline
}

Invoke-Checked dotnet @('test', 'VideoOptimiser.sln', '-c', 'Release')

$publishDirectory = Join-Path $repositoryRoot 'artifacts\release-smoke'
Invoke-Checked dotnet @(
    'publish', 'src\VideoOptimiser.Cli', '-p:PublishProfile=win-x64-aot',
    "-p:Version=$Version", "-p:AssemblyVersion=$Version", "-p:FileVersion=$Version.0",
    "-p:InformationalVersion=$Version+$((git rev-parse --short HEAD).Trim())",
    '-o', $publishDirectory)

$executable = Join-Path $publishDirectory 'video-optimiser.exe'
Invoke-Checked $executable @('--version')

$smokeConfig = Join-Path $publishDirectory 'smoke-config.yaml'
Invoke-Checked $executable @('config', 'init', '--config', $smokeConfig)
& $executable config validate --config $smokeConfig
if ($LASTEXITCODE -ne 3) {
    throw "A fresh template should return exit code 3 from config validate; got $LASTEXITCODE."
}
Remove-Item -LiteralPath $smokeConfig -Force

if ($PSCmdlet.ShouldProcess($tag, 'Commit, tag, and push release')) {
    Invoke-Checked git @('add', 'CHANGELOG.md')
    Invoke-Checked git @('commit', '-m', "chore: release $tag")
    Invoke-Checked git @('tag', '-a', $tag, '-m', "Release $tag")
    Invoke-Checked git @('push', 'origin', 'HEAD')
    Invoke-Checked git @('push', 'origin', $tag)
}

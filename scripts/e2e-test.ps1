[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable
)

# Runs discover -> queue run -> validate -> finalise against real ab-av1, ffmpeg and ffprobe, which must be on PATH
# (ab-av1 itself calls ffmpeg from PATH). Unit tests fake every tool, so this is the only check of their real output.

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$WhatIfPreference = $false

$Executable = (Resolve-Path -LiteralPath $Executable).Path
foreach ($tool in 'ab-av1', 'ffmpeg', 'ffprobe') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on PATH." }
}

function Invoke-Cli([int] $ExpectedExitCode, [string[]] $Arguments, [string] $InputText) {
    Write-Host "> video-optimiser $($Arguments -join ' ')"
    $output = if ($PSBoundParameters.ContainsKey('InputText')) {
        $InputText | & $Executable @Arguments --config $config 2>&1 | Out-String
    } else {
        & $Executable @Arguments --config $config 2>&1 | Out-String
    }
    Write-Host $output
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        throw "video-optimiser $($Arguments -join ' ') returned exit code $LASTEXITCODE; expected $ExpectedExitCode."
    }
    $output
}

function Get-Jobs([string] $Command) {
    $json = & $Executable @($Command -split ' ') --json --config $config | Out-String
    if ($LASTEXITCODE -ne 0) { throw "$Command --json failed with exit code $LASTEXITCODE." }
    @($json | ConvertFrom-Json)
}

function Get-VideoCodec([string] $Path) {
    $codec = (& ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 $Path | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed on $Path." }
    $codec
}

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "video-optimiser-e2e-$([guid]::NewGuid().ToString('N'))"
$watchRoot = Join-Path $workDirectory 'videos'
New-Item -ItemType Directory -Path $watchRoot | Out-Null
try {
    # Two 1080p H.264 clips: 1080p is the lowest resolution band, and near-lossless H.264 clears any bitrate floor.
    # Clip a is finalised; clip b's output is damaged to prove validation rejects it.
    foreach ($name in 'a', 'b') {
        & ffmpeg -hide_banner -v error -f lavfi -i 'testsrc2=size=1920x1080:rate=30:duration=3' `
            -f lavfi -i 'sine=frequency=440:duration=3:sample_rate=48000' `
            -c:v libx264 -preset ultrafast -qp 5 -pix_fmt yuv420p -c:a aac -shortest (Join-Path $watchRoot "$name.mp4")
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed to generate clip $name." }
    }

    $config = Join-Path $workDirectory 'video-optimiser.yaml'
    $yamlPath = { param($path) $path.Replace('\', '\\') }
    @"
version: 1
tools:
  abAv1Path: "ab-av1"
  ffmpegPath: "ffmpeg"
  ffprobePath: "ffprobe"
processing:
  mode: "local"
database:
  path: "$(& $yamlPath (Join-Path $workDirectory 'jobs.db'))"
watch:
  roots:
    - path: "$(& $yamlPath $watchRoot)"
      recursive: false
eligibility:
  rules:
    - codecs: ["h264"]
      resolution: "1080p-1440p"
      minimumVideoBitrate: "1Mbps"
      minimumFileSize: "1MiB"
original:
  action: "delete"
quality:
  minimumVmaf: 90
  preset: 12
  encoder: "libsvtav1"
  pixelFormat: "yuv420p10le"
  crfSearch:
    minCrf: 20
    maxCrf: 55
    sampleCount: 1
    sampleDuration: "2s"
savings:
  requireSmallerOutput: true
  minimumBytesSaved: "1KiB"
  minimumPercentageSaved: 5
validation:
  decodeTest: "sampled"
  durationToleranceSeconds: 1
"@ | Set-Content -LiteralPath $config -Encoding utf8

    Invoke-Cli 0 @('config', 'validate') | Out-Null
    Invoke-Cli 0 @('queue', 'discover') | Out-Null
    $queued = Get-Jobs 'queue list'
    Assert ($queued.Count -eq 2) "discover should queue both clips; queued $($queued.Count)."

    Invoke-Cli 0 @('queue', 'run') | Out-Null
    $ready = Get-Jobs 'queue list'
    Assert (@($ready | Where-Object status -eq 'ReadyToFinalize').Count -eq 2) "both jobs should be ReadyToFinalize; got $(($ready | ForEach-Object status) -join ', ')."
    $jobA = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'a.mp4' }
    $jobB = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'b.mp4' }
    Assert ((Get-VideoCodec $jobA.outputPath) -eq 'av1') 'clip a output should be AV1.'

    # Overwrite bytes mid-file: the container index and packet counts stay intact, so only the decode test can catch it.
    $bytes = [System.IO.File]::ReadAllBytes($jobB.outputPath)
    $garbage = [byte[]]::new(64KB)
    [System.Random]::new(42).NextBytes($garbage)
    [System.Array]::Copy($garbage, 0, $bytes, [int]($bytes.Length / 2), [System.Math]::Min($garbage.Length, $bytes.Length / 4))
    [System.IO.File]::WriteAllBytes($jobB.outputPath, $bytes)
    $corrupt = Invoke-Cli 7 @('validate', $jobB.id)
    Assert ($corrupt -match 'Output failed the decode test') 'a corrupted output should fail the decode test.'

    # A truncated output, as left by an interrupted transfer, must fail validation too.
    $stream = [System.IO.File]::Open($jobB.outputPath, 'Open', 'ReadWrite')
    try { $stream.SetLength([long]($stream.Length / 2)) } finally { $stream.Dispose() }
    Invoke-Cli 7 @('validate', $jobB.id) | Out-Null

    Invoke-Cli 0 @('finalize', '--ready') -InputText 'y' | Out-Null

    $history = Get-Jobs 'history'
    $finalA = $history | Where-Object id -eq $jobA.id
    $finalB = $history | Where-Object id -eq $jobB.id
    Assert ($finalA.status -eq 'Completed') "clip a should be Completed; got $($finalA.status)."
    Assert ($finalB.status -eq 'Failed' -and $finalB.failureCategory -eq 'ValidationFailed') "clip b should be Failed/ValidationFailed; got $($finalB.status)/$($finalB.failureCategory)."
    Assert ((Get-VideoCodec $jobA.sourcePath) -eq 'av1') 'the finalised original path should now hold AV1.'
    Assert (-not (Test-Path -LiteralPath $jobA.outputPath)) 'the temporary output should be gone after finalisation.'
    Assert ((Get-VideoCodec $jobB.sourcePath) -eq 'h264') 'the original of a failed job must be untouched.'

    $decodeErrors = (& ffmpeg -hide_banner -nostdin -v error -xerror -i $jobA.sourcePath -f null - 2>&1 | Out-String).Trim()
    Assert ($LASTEXITCODE -eq 0 -and -not $decodeErrors) "the finalised file should decode cleanly: $decodeErrors"

    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'End-to-end test passed.'

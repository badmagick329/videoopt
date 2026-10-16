[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable
)

# Runs queue add -> run -> validate -> finalise (archive and keep) -> rediscover -> retry -> cleanup against real ab-av1, ffmpeg and ffprobe, which must be on PATH
# (ab-av1 itself calls ffmpeg from PATH). Unit tests fake every tool, so this is the only check of their real output. It also checks that the output keeps capture metadata.

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

function Get-AudioCodec([string] $Path) {
    $codec = (& ffprobe -v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 $Path | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed on $Path." }
    $codec
}

function Get-FormatTag([string] $Path, [string] $Tag) {
    $value = (& ffprobe -v error -show_entries "format_tags=$Tag" -of csv=p=0 $Path | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed on $Path." }
    # use_metadata_tags stores creation_time both in the mvhd box and as a key, so ffprobe can list it twice.
    ($value -split ';')[0]
}

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "video-optimiser-e2e-$([guid]::NewGuid().ToString('N'))"
$watchRoot = Join-Path $workDirectory 'videos'
New-Item -ItemType Directory -Path $watchRoot | Out-Null
try {
    # Three 1080p H.264 clips: 1080p is the lowest resolution band, and near-lossless H.264 clears any bitrate floor.
    # Clip a is finalised with archive and clip c with keep; clip b's output is damaged to prove validation rejects it, and
    # clip e's is truncated: validate refuses a job that already failed, so each damage check needs its own ready clip.
    # Each carries a phone-style capture date, GPS point and com.android tag, which the output must keep.
    # Clip d is a .mov with PCM audio, as a camera or phone writes: AV1 cannot go in a .mov, so its output must be an .mp4
    # with the PCM re-encoded to FLAC, and it is finalised first, in delete mode.
    foreach ($name in 'a', 'b', 'c', 'e') {
        & ffmpeg -hide_banner -v error -f lavfi -i 'testsrc2=size=1920x1080:rate=30:duration=3' `
            -f lavfi -i 'sine=frequency=440:duration=3:sample_rate=48000' `
            -c:v libx264 -preset ultrafast -qp 5 -pix_fmt yuv420p -c:a aac -shortest `
            -metadata 'creation_time=2025-05-28T23:51:45.000000Z' -metadata 'location=+51.5074-000.1278/' `
            -metadata 'com.android.model=Pixel 9 Pro' -movflags +use_metadata_tags (Join-Path $watchRoot "$name.mp4")
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed to generate clip $name." }
    }

    & ffmpeg -hide_banner -v error -f lavfi -i 'testsrc2=size=1920x1080:rate=30:duration=3' `
        -f lavfi -i 'sine=frequency=440:duration=3:sample_rate=48000' `
        -c:v libx264 -preset ultrafast -qp 5 -pix_fmt yuv420p -c:a pcm_s16le -shortest `
        -metadata 'creation_time=2025-05-28T23:51:45.000000Z' -movflags +use_metadata_tags (Join-Path $watchRoot 'd.mov')
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed to generate clip d.' }

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
      recursive: true
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
validation:
  decodeTest: "sampled"
  durationToleranceSeconds: 1
"@ | Set-Content -LiteralPath $config -Encoding utf8

    Invoke-Cli 0 @('config', 'validate') | Out-Null
    Invoke-Cli 0 @('queue', 'add') | Out-Null
    $queued = Get-Jobs 'queue list'
    Assert ($queued.Count -eq 5) "queue add should queue all five clips; queued $($queued.Count)."

    Invoke-Cli 0 @('run') | Out-Null
    $ready = Get-Jobs 'queue list'
    Assert (@($ready | Where-Object status -eq 'ReadyToFinalize').Count -eq 5) "every job should be ReadyToFinalize; got $(($ready | ForEach-Object status) -join ', ')."
    $jobA = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'a.mp4' }
    $jobB = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'b.mp4' }
    $jobC = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'c.mp4' }
    $jobD = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'd.mov' }
    $jobE = $ready | Where-Object { [System.IO.Path]::GetFileName($_.sourcePath) -eq 'e.mp4' }
    Assert ($jobD.outputPath -like '*.encoding.mp4') "clip d's output should be an .mp4 because AV1 cannot go in a .mov: $($jobD.outputPath)"
    Assert ((Get-VideoCodec $jobD.outputPath) -eq 'av1') 'clip d output should be AV1.'
    Assert ((Get-AudioCodec $jobD.outputPath) -eq 'flac') 'clip d output should carry its PCM audio as FLAC.'
    Assert ((Get-VideoCodec $jobA.outputPath) -eq 'av1') 'clip a output should be AV1.'
    Assert ((Get-FormatTag $jobA.outputPath 'creation_time') -eq '2025-05-28T23:51:45.000000Z') 'the output should keep the capture date.'
    Assert ((Get-FormatTag $jobA.outputPath 'location') -eq '+51.5074-000.1278/') 'the output should keep the GPS location.'
    Assert ((Get-FormatTag $jobA.outputPath 'com.android.model') -eq 'Pixel 9 Pro') 'the output should keep the phone model.'

    # Overwrite bytes mid-file: the container index and packet counts stay intact, so only the decode test can catch it.
    $bytes = [System.IO.File]::ReadAllBytes($jobB.outputPath)
    $garbage = [byte[]]::new(64KB)
    [System.Random]::new(42).NextBytes($garbage)
    [System.Array]::Copy($garbage, 0, $bytes, [int]($bytes.Length / 2), [System.Math]::Min($garbage.Length, $bytes.Length / 4))
    [System.IO.File]::WriteAllBytes($jobB.outputPath, $bytes)
    $corrupt = Invoke-Cli 7 @('validate', $jobB.id)
    Assert ($corrupt -match 'Output failed the decode test') 'a corrupted output should fail the decode test.'

    # A truncated output, as left by an interrupted transfer, must fail validation too.
    $stream = [System.IO.File]::Open($jobE.outputPath, 'Open', 'ReadWrite')
    try { $stream.SetLength([long]($stream.Length / 2)) } finally { $stream.Dispose() }
    Invoke-Cli 7 @('validate', $jobE.id) | Out-Null
    # A job that failed is not checked again: validate names its status and exits 6.
    $refused = Invoke-Cli 6 @('validate', $jobE.id)
    Assert ($refused -match 'is Failed\.') "validate should refuse a failed job: $refused"
    # Clip e has done its job: remove its original and files so the counts below cover clips a to d only.
    Remove-Item -LiteralPath $jobE.sourcePath, $jobE.outputPath, "$($jobE.outputPath).manifest.json" -Force -ErrorAction SilentlyContinue

    # Delete mode (the configured action) must remove the .mov and leave the AV1 under the .mp4 name.
    $deleted = Invoke-Cli 0 @('finalize', $jobD.id)
    $installedD = Join-Path $watchRoot 'd.mp4'
    Assert ($deleted -match 'Original deleted; the AV1 is ') "finalising a .mov in delete mode should name the new AV1 path: $deleted"
    Assert (-not (Test-Path -LiteralPath $jobD.sourcePath)) 'delete should remove the .mov original.'
    Assert ((Get-VideoCodec $installedD) -eq 'av1') 'delete should install the AV1 as d.mp4.'
    Assert ((Get-AudioCodec $installedD) -eq 'flac') 'the installed d.mp4 should carry FLAC audio.'

    # finalize reads original.action when it runs, so switching it between runs finalises each clip differently.
    function Set-OriginalAction([string] $Action) {
        (Get-Content -LiteralPath $config) -replace '^  action: ".*"$', "  action: `"$Action`"" | Set-Content -LiteralPath $config -Encoding utf8
    }
    Set-OriginalAction 'archive'
    Invoke-Cli 0 @('finalize', $jobA.id) | Out-Null
    Set-OriginalAction 'keep'
    $finalised = Invoke-Cli 0 @('finalize', '--ready') -InputText 'y'
    Assert ($finalised -match 'Each original stays') "finalize --ready should say what happens to originals: $finalised"

    $history = Get-Jobs 'history'
    $finalA = $history | Where-Object id -eq $jobA.id
    $finalB = $history | Where-Object id -eq $jobB.id
    $finalC = $history | Where-Object id -eq $jobC.id
    $finalD = $history | Where-Object id -eq $jobD.id
    Assert ($finalD.status -eq 'Completed') "clip d should be Completed; got $($finalD.status)."
    Assert ($finalA.status -eq 'Completed' -and $finalC.status -eq 'Completed') "clips a and c should be Completed; got $($finalA.status), $($finalC.status)."
    Assert ($finalB.status -eq 'Failed' -and $finalB.failureCategory -eq 'ValidationFailed') "clip b should be Failed/ValidationFailed; got $($finalB.status)/$($finalB.failureCategory)."
    Assert ((Get-VideoCodec $jobA.sourcePath) -eq 'av1') 'the archived original path should now hold AV1.'
    Assert ((Get-VideoCodec (Join-Path $watchRoot 'Archive\a.mp4')) -eq 'h264') 'archive should move the original to the Archive folder beside it.'
    Assert (-not (Test-Path -LiteralPath $jobA.outputPath)) 'the temporary output should be gone after finalisation.'
    Assert ((Get-VideoCodec $jobC.sourcePath) -eq 'h264') 'keep must leave the original in place.'
    $keptAv1 = Join-Path $watchRoot 'c.av1.mp4'
    Assert ((Get-VideoCodec $keptAv1) -eq 'av1') 'keep should install the AV1 beside the original.'
    Assert ((Get-VideoCodec $jobB.sourcePath) -eq 'h264') 'the original of a failed job must be untouched.'

    foreach ($installed in $jobA.sourcePath, $keptAv1, $installedD) {
        $decodeErrors = (& ffmpeg -hide_banner -nostdin -v error -xerror -i $installed -f null - 2>&1 | Out-String).Trim()
        Assert ($LASTEXITCODE -eq 0 -and -not $decodeErrors) "the installed AV1 $installed should decode cleanly: $decodeErrors"
    }

    # The archived and kept originals and failed clip b are all still eligible H.264; none may be queued again.
    $rediscovered = Invoke-Cli 0 @('queue', 'add')
    Assert ($rediscovered -cmatch '(?m)^Queued: 0\.' -and $rediscovered -match 'Already converted: 1\.' -and $rediscovered -match 'Previously failed or cancelled: 1\.') "rediscovery should queue nothing: $rediscovered"

    # calibrate reads the finished jobs and the folder without writing: the three conversions count as history because each
    # job stored the quality settings that chose its CRF, failed clip b does not, and the three AV1 files are summed apart.
    $databasePath = Join-Path $workDirectory 'jobs.db'
    $databaseHash = (Get-FileHash -LiteralPath $databasePath).Hash
    $calibration = & $Executable calibrate --json --config $config | Out-String
    Assert ($LASTEXITCODE -eq 0) "calibrate --json failed with exit code $LASTEXITCODE."
    $calibration = $calibration | ConvertFrom-Json
    Assert ((Get-FileHash -LiteralPath $databasePath).Hash -eq $databaseHash) 'calibrate must leave the job database unchanged.'
    Assert ($calibration.rules[0].history.converted -eq 3 -and $calibration.rules[0].history.notWorthIt -eq 0) "calibrate should count three conversions: $($calibration.rules[0].history | ConvertTo-Json -Compress)"
    Assert ($calibration.rules[0].videos -eq 2) "calibrate should find kept original c and failed clip b admitted; got $($calibration.rules[0].videos)."
    Assert ($calibration.alreadyAv1.videos -eq 3) "calibrate should count three AV1 files; got $($calibration.alreadyAv1.videos)."
    Assert ($calibration.historyLeftOut.otherSettings -eq 0) "every conversion ran at the current settings; got $($calibration.historyLeftOut.otherSettings) left out."

    # queue retry opts clip b back in; dropping that job leaves it out of the queue again.
    $retried = Invoke-Cli 0 @('queue', 'retry', $jobB.id)
    Assert ($retried -match 'again as job ([0-9a-f]{32}).') "queue retry should create a job for clip b: $retried"
    $retryId = $Matches[1]
    $whileRetried = Invoke-Cli 0 @('queue', 'add')
    Assert ($whileRetried -match 'Already queued: 1\.') "discovery should count the retried clip b as queued: $whileRetried"
    Invoke-Cli 0 @('queue', 'drop', $retryId) | Out-Null
    $afterCancel = Invoke-Cli 0 @('queue', 'add')
    Assert ($afterCancel -cmatch '(?m)^Queued: 0\.' -and $afterCancel -match 'Previously failed or cancelled: 1\.') "a cancelled clip b should stay out of the queue: $afterCancel"

    # Left behind: the manifests of clips a, c and d (completed) and clip b's output and manifest (failed).
    $outputFolder = Join-Path $watchRoot '.video-optimiser'
    $preview = Invoke-Cli 0 @('cleanup', '--dry-run')
    Assert ($preview -match 'Abandoned temporary outputs: 5\.') "cleanup --dry-run should list 5 abandoned files: $preview"
    Assert (@(Get-ChildItem -LiteralPath $outputFolder -File).Count -eq 5) 'cleanup --dry-run must not delete anything.'
    Invoke-Cli 0 @('cleanup') -InputText 'y' | Out-Null
    Assert (@(Get-ChildItem -LiteralPath $outputFolder -File).Count -eq 0) 'cleanup should delete every abandoned file.'
    Assert ((Get-VideoCodec $jobB.sourcePath) -eq 'h264') 'cleanup must leave the original of a failed job untouched.'

    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'End-to-end test passed.'

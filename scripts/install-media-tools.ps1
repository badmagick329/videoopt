[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

# Pinned ab-av1 and FFmpeg for scripts/e2e-test.ps1. The Gyan full build is used because ab-av1 needs both
# libsvtav1 and libvmaf, and the essentials build lacks libsvtav1. Bump a version together with its SHA-256.
# Prints the directories to put on PATH. Downloads are skipped when Destination already holds them, so CI can cache it.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$tools = @(
    @{
        Name = 'ab-av1-0.11.7'
        Url = 'https://github.com/alexheretic/ab-av1/releases/download/v0.11.7/ab-av1.exe'
        Sha256 = 'a3a9534d2adecabc4861acf632d0f64741f56e2050d52032a5307bebb4fbdcc9'
        File = 'ab-av1.exe'
        BinDirectory = '.'
    },
    @{
        Name = 'ffmpeg-9.0.2'
        Url = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build-shared.7z'
        Sha256 = '4d2060a8b34a940aa47d785142055bb92a63053781e55f2ace4546edd519a8f5'
        File = 'ffmpeg.7z'
        BinDirectory = 'ffmpeg-9.0.2-full_build-shared\bin'
    }
)

foreach ($tool in $tools) {
    $directory = Join-Path $Destination $tool.Name
    $bin = Join-Path $directory $tool.BinDirectory
    if (-not (Test-Path -LiteralPath $directory)) {
        $staging = "$directory.partial"
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $staging | Out-Null
        $download = Join-Path $staging $tool.File
        Invoke-WebRequest -Uri $tool.Url -OutFile $download
        $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
        if ($actual -ne $tool.Sha256) { throw "$($tool.Name): expected SHA-256 $($tool.Sha256), got $actual." }
        if ($tool.File.EndsWith('.7z')) {
            & 7z x -y "-o$staging" $download | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7z failed to extract $($tool.Name)." }
            Remove-Item -LiteralPath $download
        }
        # Rename last, so an interrupted run never leaves a directory that looks complete.
        Move-Item -LiteralPath $staging -Destination $directory
    }
    (Resolve-Path -LiteralPath $bin).Path
}

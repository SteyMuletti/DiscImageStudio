# One step: songs folder -> burn-ready mixtape CD (music + disc art).
# Usage:  .\make-mixtape.ps1 -Songs "D:\Mixtape\songs" [-Art art\money-ring.png] [-Out disc] [-Title "My Mixtape"] [-Artist "Me"]
# Needs ffmpeg on PATH. Then burn disc\mixtape.cue with ImgBurn ("Write image file to disc").
param(
    [Parameter(Mandatory)][string]$Songs,
    [string]$Art = "$PSScriptRoot\art\money-ring.png",
    [string]$Out = "$PSScriptRoot\disc",
    [string]$Title = "Mixtape",
    [string]$Artist = "",
    [string]$Disc = "verbatim-azo",
    [double]$Gap = 0
)
$ErrorActionPreference = 'Stop'
$wav = Join-Path $env:TEMP "mixtape-wav-$([guid]::NewGuid().ToString('N'))"
& "$PSScriptRoot\tools\mixtape\convert-to-cd-wav.ps1" -Source $Songs -Output $wav
$exe = Join-Path $PSScriptRoot 'mixtape-cd.exe'
$cliArgs = @('cd-mixtape', '--input', $Art, '--music-dir', $wav, '--output-dir', $Out, '--disc', $Disc,
          '--title', $Title, '--performer', $Artist, '--gap', $Gap, '--tone-dither', '0.75')
try {
    if (Test-Path $exe) { & $exe @cliArgs }
    else { & dotnet run --project "$PSScriptRoot\src\DiscImageStudio.Cli" -c Release -- @cliArgs }
    if ($LASTEXITCODE) { throw "mixtape-cd failed" }
} finally { Remove-Item -Recurse -Force $wav -ErrorAction SilentlyContinue }
Write-Host "`nDone. Check $Out\mixtape-preview.png, then burn $Out\mixtape.cue (disc-at-once)."

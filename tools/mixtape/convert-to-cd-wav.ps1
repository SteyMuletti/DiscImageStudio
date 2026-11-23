# Windows version: convert songs to CD-audio WAV (44.1 kHz, 16-bit, stereo). Requires ffmpeg on PATH.
# Usage: .\convert-to-cd-wav.ps1 -Source "D:\Mixtape\songs" -Output "D:\Mixtape\wav"
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$ext = '.mp3','.flac','.m4a','.aac','.ogg','.opus','.wav','.aif','.aiff'
Get-ChildItem -LiteralPath $Source -File | Where-Object { $ext -contains $_.Extension.ToLower() } | Sort-Object Name | ForEach-Object {
    Write-Host "-> $($_.BaseName)"
    & ffmpeg -nostdin -loglevel error -y -i $_.FullName -map 0:a:0 -ar 44100 -ac 2 -c:a pcm_s16le (Join-Path $Output "$($_.BaseName).wav")
    if ($LASTEXITCODE) { throw "ffmpeg failed on $($_.Name)" }
}

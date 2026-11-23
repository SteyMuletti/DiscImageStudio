#!/usr/bin/env bash
# Convert a folder of songs (MP3, FLAC, M4A, OGG, WAV...) into CD-audio WAVs
# (44.1 kHz, 16-bit, stereo) for `disc-image-studio cd-mixtape`.
# Usage: convert-to-cd-wav.sh <songs-folder> <output-folder>
# Name the source files "01 ...", "02 ..." so they sort in track order.
set -euo pipefail
src="${1:?songs folder}"; out="${2:?output folder}"
command -v ffmpeg >/dev/null || { echo "ffmpeg is required" >&2; exit 1; }
mkdir -p "$out"
shopt -s nullglob nocaseglob
for f in "$src"/*.{mp3,flac,m4a,aac,ogg,opus,wav,aif,aiff}; do
  name="$(basename "${f%.*}")"
  echo "-> $name"
  ffmpeg -nostdin -loglevel error -y -i "$f" -map 0:a:0 -ar 44100 -ac 2 -c:a pcm_s16le "$out/$name.wav"
done

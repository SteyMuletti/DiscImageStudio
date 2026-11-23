#!/usr/bin/env bash
# One step: songs folder -> burn-ready mixtape CD (music + disc art).
# Usage: ./make-mixtape.sh SONGS_DIR [ART=art/money-ring.png] [OUT=disc] [TITLE=Mixtape]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
songs="${1:?songs folder}"; art="${2:-$here/art/money-ring.png}"; out="${3:-$here/disc}"; title="${4:-Mixtape}"
wav="$(mktemp -d)"; trap 'rm -rf "$wav"' EXIT
"$here/tools/mixtape/convert-to-cd-wav.sh" "$songs" "$wav"
run=(dotnet run --project "$here/src/DiscImageStudio.Cli" -c Release --)
[ -x "$here/mixtape-cd" ] && run=("$here/mixtape-cd")
"${run[@]}" cd-mixtape --input "$art" --music-dir "$wav" --output-dir "$out" \
  --disc "${DISC:-verbatim-azo}" --title "$title" --gap "${GAP:-0}" --tone-dither 0.75
echo; echo "Done. Check $out/mixtape-preview.png, then burn $out/mixtape.cue (disc-at-once)."

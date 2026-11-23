# Mixtape CD: music + disc art on one disc

`cd-mixtape` burns your songs as normal, playable CD audio tracks on the **inner ring**
and draws your image on the **outer ring** with a final "disc art" track.

```
 hub ── [ songs 1..N : normal music ] ── [ last track : picture ] ── edge
```

## How it works

1. The whole disc is designed exactly like `cd-generate` (full-disc image coordinates).
2. Your songs replace the first part of the disc, bit-for-bit (padded with silence to whole
   CD sectors and to the 4-second Red Book minimum).
3. The remainder of the full-disc picture becomes the last track. Burned **disc-at-once**,
   the drive's encoder runs continuously across track boundaries, so the outer ring is
   physically identical to a picture-only disc.

The trade-off is area: **more music = thinner picture ring.** Approximate on an 80-min CD-R
(default geometry 24.5–56.8 mm):

| Music length | Artwork visible from | 
| --- | --- |
| 20 min | ~35 mm |
| 30 min | ~40 mm |
| 40 min | ~44 mm |
| 50 min | ~47 mm |
| 60 min | ~51 mm |

The preview PNG shows the exact split for your songs. Put the important parts of your art
in the outer ring.

## Steps

```sh
# 1. Convert songs to CD WAV (name them "01 ...", "02 ..." for order)
tools/mixtape/convert-to-cd-wav.sh ./songs ./songs-wav

# 2. Build the disc (same geometry/tone options as cd-generate, use your calibrated values)
disc-image-studio cd-mixtape --input art.png --music-dir ./songs-wav --output-dir ./disc \
  --title "My Mixtape" --performer "Me" --tone-dither 0.75

# 3. Check ./disc/mixtape-preview.png (blue = music ring), then burn ./disc/mixtape.cue
```

Burning:

- **ImgBurn**: *Write image file to disc* → open `mixtape.cue`. Uses DAO automatically. Enable
  CD-Text if your drive supports it so players show titles.
- **cdrdao**: `cdrdao write --device /dev/sr0 mixtape.cue`.
- Do **not** burn track-at-once (TAO) — the gaps/link blocks shift the picture.
- Do **not** let the burner add 2-second pregaps between tracks or normalise/convert audio.

## Things to know

- The last track is the picture: it plays as loud noise. It is titled
  "Disc Art (noise - do not play)". Turn the volume down if you skip to it.
- `--gap 2` adds 2 s of silence after each song (shrinks the picture slightly).
- Car/home players read it like any audio CD; ripping software will see N+1 tracks.
- Calibrate your disc brand first (see CALIBRATION.en.md) for sharp, straight artwork.

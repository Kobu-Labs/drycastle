# Rozhlas (village loudspeaker)

`source/` holds the clean recordings the broadcast is baked from; `rozhlas_<n>.ogg` is the output, listed in `godot/shooter/data/rozhlas.json`.

| File in `source/` | What | Licence / provenance |
|---|---|---|
| `song.*` | "Slovenské mamičky", downloaded from https://www.youtube.com/watch?v=rXocYjrtryk on 2026-11-12; the first 20 s are used (`--song-start`, `--song-seconds`) | Public domain according to the uploader and the project owner; **unverified**: confirm that the recording itself (not only the composition) is free before release |
| `announcement.*` | the spoken announcement, dry and close to the microphone | own recording |
| `opakujem.*` | the word OPAKUJEM | own recording |

Bake with `make rozhlas` (or `python3 -I scripts/make_rozhlas.py --help`). `--keep-wavs` keeps the degraded parts to listen to one by one.
The tuning (band limit, bit crushing, clipping, echo, hiss, dropouts) is at the top of the script.

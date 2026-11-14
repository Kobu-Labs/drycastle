# 09 Audio

Voice lines, van music, the speaker.

## Facts
- Recordings: `~/downloads/New Recording.mov`, `New Recording 2.mov` … `15.mov` (15, a few seconds each).
- Template: `scripts/make_rozhlas.py` (stdlib + ffmpeg, `make rozhlas`), chain as data in `godot/shooter/data/rozhlas_voice.json`.
- Private audio loader to copy: `godot/shooter/ChurchChant.cs` (private folder first, a missing file logged once). `sounds.json` must not name private files.
- Moving loops: `LoopVoice` (`Sfx.cs`) has 10 shared voices: do not use it here; make own `AudioStreamPlayer3D`s as `ChurchChant` / `DemonAudio`.
- Long synced audio precedent: `RozhlasState` (variant + offset, sent to joiners).

## Do
- `scripts/make_thief_voices.py` + `make thief-voices`: every recording through one raspy "heavy smoker" chain (pitch down a little, saturation, band-limit, breath noise), peak-normalised mono `.ogg`, into `assets-private/audio/thieves/voice/line_NN.ogg`. Chain in `godot/shooter/data/thief_voice.json` so it is tuned without touching code; `--keep-wavs`, `--only N` for quick listening. Expect several rounds of tuning by ear.
- Voice lines, server-decided: a living member (not swinging, not Down) with a player within `voice_near_m` (8) rolls `voice_chance` every `voice_roll_s`; cooldown per member; one line at a time per Raid. Fx event (member id, line index); clients play it at the member, following it, to `voice_range_m` (25). Line count comes from the folder at load on the client; the server sends an index modulo.
- Van music: each van gets a song index at spawn; clients loop it at the van (~60 m), in sync by the server clock offset, stopped when Destroyed or gone.
- Speaker: each member rolls `speaker_chance` (0.05) at spawn; flag in its wire state; clients draw the speaker and loop another song at it (~30 m). On death the music stops and a ground Item `speaker` drops (append to `items.json`; no equip slot: Bag only, silent).
- Songs: every `.ogg` in `assets-private/audio/thieves/songs/`; empty folder = silence. We put the files there ourselves (picked: youtube `vUXzn-xTeA0`, `odp6sdJNUtg`, `D5F6tjfx-nY`).
- README provenance table for the voices next to the output, as `assets/drycastle-rozhlas/README.md`.

## Accept
- `make thief-voices` writes 15 files; rerun is idempotent.
- Tests: voice roll rules (range, cooldown, one per Raid); speaker roll seeded; codec round trip.
- No private folder: silent, no error beyond one log line.

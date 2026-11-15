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

## Done: notes
Member-side half only (voice lines, speaker, private-folder loader). Not done here: the voice bake (done earlier, `make thief-voices`), the van's song (03 calls the API below). Protocol 44, event kind 236 `ThiefVoice` (237 stays free).

Code: pure `ShooterCore/Thieves/GangVoice.cs` (`ThievesAudioConfig`, `GangVoiceCodec`, `GangAudio`), server `NetServer.Thieves.Audio.cs`, client `ThiefAudio.cs` + `NetClient.Thieves.Audio.cs`, tests `GangAudioTests.cs`. Numbers: new last section `audio` in `thieves.json`.

### Van song API (client, for 03)
`NetClient.ThiefSounds` (made on first use) -> `ThiefAudio`:
- `AudioStreamPlayer3D? PlayVanSong(Node3D van, int songIndex, double startOffsetSeconds)`: loops song `songIndex mod songs found` (sorted by file name) at `van`, a child of it (follows it, goes with it when freed), heard to `audio.van_range_m` (60), bus Music. Offset is taken modulo the song length: pass the server clock since the van's song began. Null when no songs (silence). Calling it again on the same node restarts.
- `void StopVanSong(Node3D van)`: destroyed or gone.
- `SongCount`, `VoiceCount`: files found.

### Wire
- Event 236 `ThiefVoice`: u16 world entity id of the member, u8 line number (`GangVoiceCodec`). Broadcast; client plays `files[line mod count]` as a child of the member's view (follows it), bus SFX, `voice_range_m`. Server picks `0..voice_lines-1` (15), never the line the Raid said last.
- Gang member hp byte bit 6 = speaker (`GangRules.SpeakerOf`, `PackHp(..., speaker)`); only while alive.

### Decisions
- Roll rules are in `GangAudio` (pure, own clock, seeded): a member alive and not in Windup/Swing with a human within `voice_near_m` (3D distance) rolls `voice_chance` (0.3) every `voice_roll_s` (2, the clock runs only while one is near, start staggered); `voice_cooldown_s` 15 per member; `voice_busy_s` 3 per Raid. Bots do not count as listeners.
- Speaker roll (`speaker_chance` 0.05) happens in `GangAudio.Update` the first tick a member is seen (so members the van adds later are rolled too, no hook in `ThiefGang.Add`). Flag in `GangMember.Speaker`.
- Speaker on the client: a small box (dark body, grille, orange stripe) child of the member view at the hip; its song = `songs[entity id mod count]` (same on every client), looped from a random point in the first half, bus Music, `speaker_range_m` 30. Flag gone (death) removes both. Joiners need nothing extra (the flag is in the entity state).
- Kill drops a ground Item `speaker` at the member's feet (`_corpses.Drop`, dropper 0) from `OnGangKilled`; not dropped when the member was removed otherwise (event end). Items.json: `speaker` appended; new item flag `bag_only` (no slot): `ItemRules.Place` puts it in the bag and never the slot or a swap (`NoRoom`), `Equip` refuses it, corpse and ground pickups return `NoRoom` before counting when the bag is full (also corpse pickup now says "bag full"), `DressCorpse` does not draw it as headgear.
- Equipment screen: `EquipmentWishes.Activate/Drag/CanDrop` take an optional `plain` predicate; a bag-only item has no put-on wish (click, Enter, drag to the avatar or the slot do nothing), it can still be dropped. Icon `ui/icons/items/speaker.png` drawn by script (the `.import` file is made by Godot on the next editor open).
- Private audio: `assets-private/audio/thieves/voice/*.ogg` and `songs/*.ogg`, scanned once on first use, sorted by name; missing or empty folder = one log line each, silence. Not in sounds.json, not the `LoopVoice` pool.

### Open
- Merge: items.json must have `speaker` AFTER `gold_chain` (ticket 10 appends it); renumber the protocol (mine is 44) and the Wire.cs comment line.
- UI standard: the Equipment screen change is logic only (no new control or visual); no screenshots or keyboard walkthrough (brief forbids running the game). A bag-only item's Enter/click does nothing and the hint row still says "put on": a hint per item kind is not done.
- Not run in the game: voice levels (`VoiceDb` 4 dB, speaker 0 dB, van 2 dB are guesses), the inverse-distance cut at the range edge, the speaker's place on the body.
- Song sync of speakers across clients is not attempted (each starts from a random point).
- No file named `line_NN.ogg` is required: any `.ogg` in the folder is a line (sorted by name).
- Van song: done in ticket 04 (`NetClient.StartVanSong`, number in the van's owner field bits 10-14, started from the server clock; stopped when the van is destroyed or gone).

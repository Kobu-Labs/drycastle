#!/usr/bin/env python3
"""Bakes the village loudspeaker (rozhlas) broadcast: assets/drycastle-rozhlas/rozhlas_<n>.ogg.

    song (bad but musical) -> announcement -> OPAKUJEM (same degradation as the announcement) -> the announcement again

Inputs are clean, dry recordings in assets/drycastle-rozhlas/source/ (mono or stereo, any format ffmpeg reads):

    song.*          a public-domain song (rhythm and melody stay audible)
    announcement.*  the spoken announcement, recorded close to the microphone with no room sound
    opakujem.*      the word OPAKUJEM, recorded separately

Every badness comes from here (band limit, low sample rate, bit crushing, clipping, echo, hiss, hum, wobble),
so it can be tuned without re-recording. Needs ffmpeg and ffprobe; standard library only:

    python3 -I scripts/sounds/make_rozhlas.py [--src DIR] [--out DIR] [--seed N] [--variants N] [--song-start S] [--song-seconds S] [--keep-wavs]

Each variant uses a different seed (different hiss and echo taps), so one recording gives several broadcasts;
list them all in godot/shooter/data/rozhlas.json. The baked lengths are written to its "lengths_s" (the server, which has no audio engine, schedules
with them); after hand-editing the file list run with --lengths-only. The levels are the SONG dict below and godot/shooter/data/rozhlas_voice.json (the announcement voice, shared with the live feed).
"""
import argparse
import json
import random
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from audio import RATE, duration, load_voice, run

ROOT = Path(__file__).resolve().parents[2]

# Song: tinny and a little distorted, but the beat and the tune carry.
SONG = dict(low=300, high=4000, rate=11025, bits=10, crush_mix=0.4, wobble_hz=4.0, wobble_depth=0.04,
            echo="0.8:0.6:90:0.3", noise=0.0, level_db=-6)
# Announcement: the voice chain (cheap horn bandwidth, hard clipping, an echo off the houses, pumping; no dropouts: they sounded like cuts) lives in
# godot/shooter/data/rozhlas_voice.json, shared with the server's live feed (godot/ShooterCore/RozhlasFeed/RozhlasVoice.cs), so the two cannot drift.
VOICE_FILE = ROOT / "godot/shooter/data/rozhlas_voice.json"
SONG_START_S, SONG_SECONDS, SONG_FADE_S = 0.0, 60.0, 3.0  # the part of the song that is played (the intro, not the whole song)
PEAK_DB = -1.0  # the baked file's loudest peak: the sounds of the game peak near 0 dBFS, and the distance in rozhlas.json does the quieting
FADE_S, BED_NOISE = 0.5, 0.0  # every part fades in and out; BED_NOISE is a faint hiss under the whole broadcast (0 = none)
GAP_SONG_S, GAP_VOICE_S, GAP_CLEAR_S = 0.6, 1.0, 0.0  # silence after the song, after the first announcement (before OPAKUJEM), after OPAKUJEM


def find(src, stem):
    for p in sorted(src.glob(stem + ".*")):
        return p
    sys.exit(f"missing input: {src}/{stem}.* (see the module docstring)")


def degrade(inp, out, chain, noise, seed):
    """inp through the filter chain, then pink hiss mixed in at the level `noise`, resampled to RATE mono."""
    d = duration(inp)
    graph = f"[0:a]aformat=channel_layouts=mono,{chain}[s];[1:a]volume={noise}[n];[s][n]amix=inputs=2:duration=first:normalize=0,aresample={RATE}[o]"
    run(["ffmpeg", "-y", "-v", "error", "-i", str(inp), "-f", "lavfi", "-i", f"anoisesrc=d={d + 1:.3f}:c=pink:a=1:seed={seed}",
         "-filter_complex", graph, "-map", "[o]", "-ar", str(RATE), "-ac", "1", str(out)])


def song_chain(p):
    return (f"highpass=f={p['low']},lowpass=f={p['high']},aresample={p['rate']},acrusher=bits={p['bits']}:mix={p['crush_mix']},"
            f"vibrato=f={p['wobble_hz']}:d={p['wobble_depth']},aecho={p['echo']},alimiter=limit=0.9:level=0,volume={p['level_db']}dB")


def voice_chain(v, rng):
    """The voice filter chain for one variant: the file's filters in order, {name} -> params[name], {taps} -> three echo delays (ms), one picked per range.
    Must stay the same algorithm as RozhlasVoice.Chain in godot/ShooterCore/RozhlasFeed/RozhlasVoice.cs."""
    taps = "|".join(str(rng.randint(*r)) for r in v["echo_taps_ms"])
    out = []
    for tpl in v["chain"]:
        f = tpl.replace("{taps}", taps)
        for k, val in v["params"].items():
            f = f.replace("{" + k + "}", val)
        if re.search(r"\{[a-z_]+\}", f):
            sys.exit(f"{VOICE_FILE}: unresolved placeholder in {tpl!r}")
        out.append(f)
    return ",".join(out)


def trim(inp, out, start, seconds, fade):
    """The part of inp from `start` for `seconds` (0 = to the end), faded out at the end."""
    length = min(seconds, duration(inp) - start) if seconds > 0 else duration(inp) - start
    cmd = ["ffmpeg", "-y", "-v", "error", "-ss", str(start), "-i", str(inp)] + (["-t", str(seconds)] if seconds > 0 else [])
    run(cmd + ["-af", f"afade=t=out:st={max(0.0, length - fade):.2f}:d={fade}", str(out)])


def bake(src, out, seed, tmp, song_start=SONG_START_S, song_seconds=SONG_SECONDS):
    rng = random.Random(seed)
    VOICE = load_voice(VOICE_FILE)
    VOICE_NOISE = float(VOICE["params"]["noise"])
    song, ann, opa = find(src, "song"), find(src, "announcement"), find(src, "opakujem")
    trim(song, tmp / "song_cut.wav", song_start, song_seconds, SONG_FADE_S)
    song = tmp / "song_cut.wav"
    parts = [(tmp / "song.wav", song_chain(SONG), SONG["noise"]),
             (tmp / "voice.wav", voice_chain(VOICE, rng), VOICE_NOISE),
             (tmp / "opakujem.wav", voice_chain(VOICE, rng), VOICE_NOISE)]
    for (wav, chain, noise), inp in zip(parts, (song, ann, opa)):
        degrade(inp, wav, chain, noise, rng.randint(1, 10**6))
    # the announcement is degraded twice with different echo taps, like a repeated reading over the same bad line
    degrade(ann, tmp / "voice2.wav", voice_chain(VOICE, rng), VOICE_NOISE, rng.randint(1, 10**6))
    order = [("song.wav", GAP_SONG_S), ("voice.wav", GAP_VOICE_S), ("opakujem.wav", GAP_CLEAR_S), ("voice2.wav", 0.5)]
    cmd = ["ffmpeg", "-y", "-v", "error"]
    for name, _ in order:
        cmd += ["-i", str(tmp / name)]
    total = sum(duration(tmp / n) + g for n, g in order)
    cmd += ["-f", "lavfi", "-i", f"anoisesrc=d={total:.3f}:c=pink:a=1:seed={rng.randint(1, 10**6)}"]
    labels = ""
    for i, (name, gap) in enumerate(order):
        d = duration(tmp / name)
        labels += f"[{i}:a]afade=t=in:d={FADE_S},afade=t=out:st={max(0.0, d - FADE_S):.3f}:d={FADE_S},apad=pad_dur={gap}[p{i}];"
    joined = "".join(f"[p{i}]" for i in range(len(order)))
    graph = (labels + f"{joined}concat=n={len(order)}:v=0:a=1[c];[{len(order)}:a]volume={BED_NOISE}[b];"
             f"[c][b]amix=inputs=2:duration=first:normalize=0,afade=t=out:st={max(0.0, total - 1.0):.3f}:d=1.0[o]")
    mixed = tmp / "mixed.wav"
    cmd += ["-filter_complex", graph, "-map", "[o]", "-ar", str(RATE), "-ac", "1", str(mixed)]
    run(cmd)
    # one gain over the whole broadcast (the balance between the parts stays as tuned) up to PEAK_DB, then to ogg
    peak = float(re.search(r"max_volume: (-?[\d.]+) dB", subprocess.run(["ffmpeg", "-hide_banner", "-i", str(mixed), "-af", "volumedetect", "-f", "null", "-"], capture_output=True, text=True).stderr).group(1))
    run(["ffmpeg", "-y", "-v", "error", "-i", str(mixed), "-af", f"volume={PEAK_DB - peak:.2f}dB", "-c:a", "libvorbis", "-q:a", "4", str(out)])
    return duration(out)


def record_lengths(json_path, assets):
    """Writes "lengths_s" (one ffprobe length per entry of "files") into rozhlas.json, in place, keeping the rest of the file as it is."""
    text = json_path.read_text()
    files = json.loads(text)["files"]
    lengths = []
    for rel in files:
        f = assets / rel
        if not f.exists():
            print(f"{f} is missing: lengths_s not updated (the server falls back to default_length_s)")
            return
        lengths.append(round(duration(f), 2))
    line = f'"lengths_s": {json.dumps(lengths)}'
    if re.search(r'"lengths_s":\s*\[[^\]]*\]', text):
        text = re.sub(r'"lengths_s":\s*\[[^\]]*\]', line, text, count=1)  # the comma after it, if any, stays as it is
    else:
        # after "files": that entry ends in a comma now, and the new one takes over whatever followed it (a comma, or nothing when "files" was the last key)
        text = re.sub(r'("files":\s*\[[^\]]*\])', lambda m: m.group(1) + ",\n  " + line, text, count=1)
    json.loads(text)  # never write a file the server cannot read
    json_path.write_text(text)
    print(f"{json_path}: lengths_s = {lengths}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", type=Path, default=ROOT / "assets/drycastle-rozhlas/source")
    ap.add_argument("--out", type=Path, default=ROOT / "assets/drycastle-rozhlas")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--variants", type=int, default=2)
    ap.add_argument("--song-start", type=float, default=SONG_START_S, help="seconds into the song where the jingle starts")
    ap.add_argument("--song-seconds", type=float, default=SONG_SECONDS, help="length of the jingle in seconds (0 = to the end)")
    ap.add_argument("--json", type=Path, default=ROOT / "godot/shooter/data/rozhlas.json", help="the data file that gets the baked lengths")
    ap.add_argument("--lengths-only", action="store_true", help="only record the lengths of the listed files in the json (no baking)")
    ap.add_argument("--keep-wavs", action="store_true", help="keep the degraded parts (in OUT/parts) for listening to them one by one")
    a = ap.parse_args()
    for tool in ("ffmpeg", "ffprobe"):
        if not shutil.which(tool):
            sys.exit(f"{tool} not found")
    assets = ROOT / "assets"
    if a.lengths_only:
        record_lengths(a.json, assets)
        return
    a.out.mkdir(parents=True, exist_ok=True)
    for n in range(1, a.variants + 1):
        with tempfile.TemporaryDirectory() as t:
            tmp = Path(t)
            out = a.out / f"rozhlas_{n}.ogg"
            secs = bake(a.src, out, a.seed * 1000 + n, tmp, a.song_start, a.song_seconds)
            if a.keep_wavs:
                parts = a.out / "parts" / str(n)
                parts.mkdir(parents=True, exist_ok=True)
                for w in tmp.glob("*.wav"):
                    shutil.copy(w, parts / w.name)
        print(f"{out}  {secs:.1f} s")
    record_lengths(a.json, assets)


if __name__ == "__main__":
    main()

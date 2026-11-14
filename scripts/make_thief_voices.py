#!/usr/bin/env python3
"""Bakes the thieves' voice lines: assets-private/audio/thieves/voice/line_NN.ogg.

    New Recording.mov, New Recording 2.mov ... (audio stream only, natural order) -> line_01.ogg, line_02.ogg ...

Each recording: trimmed of silence at both ends, peak-normalised, run through ONE raspy "heavy smoker" chain
(slightly lower pitch, saturation, band limit, a touch of breath noise), peak-normalised again, mono 44.1 kHz ogg.
The chain is data in godot/shooter/data/thief_voice.json (ffmpeg filter list + parameters), tuned by ear without touching this file.
Needs ffmpeg and ffprobe (ffmpeg with rubberband); standard library only:

    python3 -I scripts/make_thief_voices.py [--src DIR] [--out DIR] [--only N] [--keep-wavs]

--only N bakes just line N (the Nth recording) for quick listening; --keep-wavs keeps the dry and the processed wavs in OUT/parts/.
A README with the provenance table is written next to the output. The output is private (assets-private/ is gitignored).
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RATE = 44100
VOICE_FILE = ROOT / "godot/shooter/data/thief_voice.json"
OGG_Q = "4"  # libvorbis quality, as the rozhlas bake


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"{' '.join(cmd[:3])} failed:\n{r.stderr[-1500:]}")
    return r


def duration(path):
    return float(run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)]).stdout)


def peak_db(path):
    err = run(["ffmpeg", "-hide_banner", "-i", str(path), "-af", "volumedetect", "-f", "null", "-"]).stderr
    return float(re.search(r"max_volume: (-?[\d.]+) dB", err).group(1))


def has_audio(path):
    r = run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type", "-of", "csv=p=0", str(path)])
    return "audio" in r.stdout


def natural_key(p):
    return [int(s) if s.isdigit() else s.lower() for s in re.split(r"(\d+)", p.name)]


def recordings(src):
    """The recordings in natural order: 'New Recording.mov' first (it has no number), then 2, 3 ... 15."""
    files = sorted(src.glob("New Recording*.mov"), key=lambda p: ([0] if p.stem == "New Recording" else [1] + natural_key(p)))
    if not files:
        sys.exit(f"no 'New Recording*.mov' in {src}")
    return files


def load_voice(path=VOICE_FILE):
    """The voice-chain file: params keep the literal text written in it (floats as str), so every value is spelled as written."""
    text = Path(path).read_text()
    v = json.loads(text)
    if v.get("version") != 1:
        sys.exit(f"{path}: unsupported version {v.get('version')}")
    v["params"] = json.loads(text, parse_float=str, parse_int=str)["params"]
    return v


def fill(templates, params, where):
    out = []
    for tpl in templates:
        f = tpl
        for k, val in params.items():
            f = f.replace("{" + k + "}", val)
        if re.search(r"\{[a-z_]+\}", f):
            sys.exit(f"{where}: unresolved placeholder in {tpl!r}")
        out.append(f)
    return ",".join(out)


def prepare(inp, out, v):
    """The audio stream of inp as a mono RATE wav, silence trimmed at both ends, peak-normalised to dry_peak_db."""
    p = v["params"]
    trim = (f"silenceremove=start_periods=1:start_threshold={p['silence_db']}dB:start_silence={p['silence_pad_s']}")
    raw = out.with_name(out.stem + "_raw.wav")
    run(["ffmpeg", "-y", "-v", "error", "-i", str(inp), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", str(RATE),
         "-af", f"{trim},areverse,{trim},areverse", str(raw)])
    run(["ffmpeg", "-y", "-v", "error", "-i", str(raw), "-af", f"volume={float(p['dry_peak_db']) - peak_db(raw):.2f}dB", str(out)])
    raw.unlink()


def degrade(dry, out, v, seed):
    """dry through the voice chain, a pink breath noise bed (noise_chain, level params.noise) mixed under it."""
    p = v["params"]
    chain = fill(v["chain"], p, f"{VOICE_FILE} chain")
    noise = fill(v["noise_chain"], p, f"{VOICE_FILE} noise_chain")
    d = duration(dry)
    graph = (f"[0:a]{chain}[s];[1:a]{noise},volume={p['noise']}[n];"
             f"[s][n]amix=inputs=2:duration=first:normalize=0,aresample={RATE}[o]")
    run(["ffmpeg", "-y", "-v", "error", "-i", str(dry), "-f", "lavfi", "-i", f"anoisesrc=d={d + 1:.3f}:c=pink:a=1:seed={seed}:r={RATE}",
         "-filter_complex", graph, "-map", "[o]", "-ar", str(RATE), "-ac", "1", str(out)])


def bake(src_file, out, seed, tmp, v, keep=None):
    dry, wet = tmp / "dry.wav", tmp / "wet.wav"
    prepare(src_file, dry, v)
    degrade(dry, wet, v, seed)
    # peak-normalise the sum to out_peak_db, then a 10 ms fade at both ends (no click on the trimmed edges), then ogg
    target = float(v["params"]["out_peak_db"])
    d = duration(wet)
    fade = min(0.01, d / 4)
    gain = f"volume={target - peak_db(wet):.2f}dB,afade=t=in:d={fade},afade=t=out:st={d - fade:.4f}:d={fade}"
    run(["ffmpeg", "-y", "-v", "error", "-i", str(wet), "-af", gain, "-ac", "1", "-ar", str(RATE), "-c:a", "libvorbis", "-q:a", OGG_Q,
         "-fflags", "+bitexact", "-flags:a", "+bitexact", str(out)])
    if keep is not None:
        keep.mkdir(parents=True, exist_ok=True)
        for w in (dry, wet):
            shutil.copy(w, keep / f"{out.stem}_{w.name}")
    return duration(out), peak_db(out)


def write_readme(out, rows):
    table = "\n".join(f"| `{name}` | `{src}` | {dur:.2f} s | {pk:.1f} dB | recorded by the project owners, processed by `scripts/make_thief_voices.py` |" for name, src, dur, pk in rows)
    (out / "README.md").write_text(f"""# Thieves' voice lines

`line_NN.ogg` are the lines the thieves say to a player who walks near them (docs/events/thieves/09-audio.md).
Written by `make thief-voices` ({Path(__file__).name}); do not edit by hand.

| File | Source recording | Length | Peak | Provenance |
|---|---|---|---|---|
{table}

All 15 go through one raspy "heavy smoker" chain (lower pitch, saturation, band limit, breath noise) defined in
`godot/shooter/data/thief_voice.json`: silence trimmed at both ends, mono 44.1 kHz ogg, peak-normalised.
The recordings (`New Recording*.mov`) are the project owners' own and stay outside the repo; this folder is gitignored (`assets-private/`).

Rebuild with `make thief-voices` (`--only N` for one line, `--keep-wavs` to keep the dry and the processed wavs in `parts/`).
""")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", type=Path, default=Path.home() / "downloads", help="folder with 'New Recording*.mov'")
    ap.add_argument("--out", type=Path, default=ROOT / "assets-private/audio/thieves/voice")
    ap.add_argument("--only", type=int, default=0, help="bake only line N (1 = 'New Recording.mov')")
    ap.add_argument("--voice", type=Path, default=VOICE_FILE, help="another chain file to try (default: the game's thief_voice.json)")
    ap.add_argument("--keep-wavs", action="store_true", help="keep the dry and processed wavs (in OUT/parts) for listening")
    a = ap.parse_args()
    for tool in ("ffmpeg", "ffprobe"):
        if not shutil.which(tool):
            sys.exit(f"{tool} not found")
    files = recordings(a.src.expanduser())
    if a.only and not 1 <= a.only <= len(files):
        sys.exit(f"--only {a.only}: there are {len(files)} recordings")
    v = load_voice(a.voice)
    a.out.mkdir(parents=True, exist_ok=True)
    failed = []
    for n, f in enumerate(files, 1):
        if a.only and n != a.only:
            continue
        out = a.out / f"line_{n:02d}.ogg"
        try:
            if not has_audio(f):
                raise RuntimeError("no audio stream")
            with tempfile.TemporaryDirectory() as t:
                secs, pk = bake(f, out, n, Path(t), v, a.out / "parts" if a.keep_wavs else None)
        except (RuntimeError, SystemExit) as e:
            print(f"FAILED {f.name}: {e}", file=sys.stderr)
            failed.append(f.name)
            continue
        print(f"{out.name}  {secs:.2f} s  peak {pk:.1f} dB  <- {f.name}")
    # the README always lists every line that exists (an --only run must not shrink it)
    rows = []
    for n, f in enumerate(files, 1):
        o = a.out / f"line_{n:02d}.ogg"
        if o.exists():
            rows.append((o.name, f.name, duration(o), peak_db(o)))
    write_readme(a.out, rows)
    if failed:
        sys.exit(f"{len(failed)} failed: {', '.join(failed)}")


if __name__ == "__main__":
    main()

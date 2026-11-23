#!/usr/bin/env python3
"""Synthesises the reversing beep of the bigger vehicles (issue #432) into assets/drycastle-generated-sounds/ (mono, CC0):

  reverse_beep.ogg  one beep of a truck's backup alarm: 0.55 s at 780 Hz, a soft square-ish tone with a trace of the 3rd harmonic
                    (a firm, not piercing alarm), short attack and release so it never clicks. The game repeats it
                    about every 1.4 s while the vehicle backs up (beep, gap, beep).

Deterministic. Needs numpy and ffmpeg:

    python3 scripts/make_reverse_beep.py [--out DIR] [--preview FILE.wav]   # preview: four beeps on the game's cycle
"""
import argparse
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
RATE = 44100
FREQ = 780.0
LENGTH = 0.55
CYCLE = 1.4


def beep() -> np.ndarray:
    n = int(LENGTH * RATE)
    t = np.arange(n) / RATE
    w = np.sin(2 * np.pi * FREQ * t)
    w = np.tanh(w * 2.5) / np.tanh(2.5)  # squared off
    w += 0.04 * np.sin(2 * np.pi * FREQ * 3 * t)
    env = np.minimum(1.0, t / 0.008) * np.clip((LENGTH - t) / 0.02, 0, 1)
    return w * env


def pcm16(mix: np.ndarray, peak_db: float) -> np.ndarray:
    return (mix * 10 ** (peak_db / 20) / np.max(np.abs(mix)) * 32767).astype("<i2")


def write_wav(path: Path, pcm: np.ndarray) -> None:
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "assets/drycastle-generated-sounds"))
    ap.add_argument("--preview")
    args = ap.parse_args()

    pcm = pcm16(beep(), -6)
    if args.preview:
        cycle = np.zeros(int(CYCLE * RATE), dtype="<i2")
        cycle[: len(pcm)] = pcm
        write_wav(Path(args.preview), np.tile(cycle, 4))
        print(f"wrote {args.preview}")
        return
    out = Path(args.out) / "reverse_beep.ogg"
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as d:
        wav = Path(d) / "b.wav"
        write_wav(wav, pcm)
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-c:a", "libvorbis", "-q:a", "5", str(out)], check=True)
    print(f"wrote {out} ({len(pcm) / RATE:.2f} s)")


if __name__ == "__main__":
    main()

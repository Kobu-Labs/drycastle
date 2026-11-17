#!/usr/bin/env python3
"""Synthesises the Lock-on launcher's tones (issue #347) into assets/drycastle-generated-sounds/ (mono, CC0):

  lock_beep.ogg  the seeker's beep, 80 ms at 1.5 kHz: slow while it searches, faster as the Lock builds (the game sets the rate)
  lock_tone.ogg  the solid tone of a complete Lock, 0.5 s at 2.1 kHz, flat so that plays laid end to end sound as one tone
  lock_warn.ogg  the warning in the Aircraft owner's ears, a 140 ms two-note chirp (880 Hz then 660 Hz), a square-ish cockpit sound

Deterministic. Needs numpy and ffmpeg:

    python3 scripts/make_lock_sounds.py [--out DIR]
"""
import argparse
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
RATE = 44100


def tone(freq: float, seconds: float, attack: float, release: float, square: float = 0.0) -> np.ndarray:
    """A sine (pushed toward a square by `square`) with a linear attack and release, so it never clicks."""
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    wave_ = np.sin(2 * np.pi * freq * t)
    if square > 0:
        wave_ = np.tanh(wave_ * (1 + 8 * square)) / np.tanh(1 + 8 * square)
    env = np.minimum(1.0, t / attack) * np.clip((seconds - t) / release, 0, 1)
    return wave_ * env


def write(path: Path, mix: np.ndarray, peak_db: float) -> None:
    mix = mix * 10 ** (peak_db / 20) / np.max(np.abs(mix))
    pcm = (mix * 32767).astype("<i2")
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        wav = Path(tmp.name)
    with wave.open(str(wav), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-c:a", "libvorbis", "-q:a", "5", str(path)], check=True)
    wav.unlink()
    print(f"wrote {path} ({len(pcm) / RATE:.2f} s)")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "assets/drycastle-generated-sounds"))
    out = Path(ap.parse_args().out)

    write(out / "lock_beep.ogg", tone(1500, 0.08, 0.004, 0.02), -6)
    write(out / "lock_tone.ogg", tone(2100, 0.5, 0.004, 0.004), -8)
    warn = np.concatenate([tone(880, 0.07, 0.003, 0.01, square=0.5), tone(660, 0.07, 0.003, 0.02, square=0.5)])
    write(out / "lock_warn.ogg", warn, -6)


if __name__ == "__main__":
    main()

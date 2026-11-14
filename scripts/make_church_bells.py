#!/usr/bin/env python3
"""Synthesises the church bells of the Holy Mass: assets/drycastle-generated-sounds/church_bells.ogg (mono, ~12 s, CC0).

Additive synthesis of inharmonic bell partials (hum, prime, tierce, quint, nominal and up), each with its own exponential decay and a slow
beat from a detuned twin; a few strokes about 2 s apart on two bells (a lower and a higher), a short strike click, a little echo. Deterministic.
Needs numpy and ffmpeg:

    python3 scripts/make_church_bells.py [--out PATH] [--seconds 12] [--stroke-gap 2.0]
"""
import argparse
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
RATE = 44100

# (frequency ratio to the strike note, level, decay time constant in seconds): the classic church bell, minor-third tierce included.
PARTIALS = [
    (0.50, 0.55, 6.0),   # hum
    (1.00, 1.00, 4.5),   # prime
    (1.20, 0.70, 3.6),   # tierce (minor third: the bell's sad colour)
    (1.50, 0.45, 3.0),   # quint
    (2.00, 0.75, 2.4),   # nominal
    (2.50, 0.30, 1.8),
    (3.00, 0.28, 1.5),
    (4.20, 0.18, 1.0),
    (5.40, 0.12, 0.7),
    (6.80, 0.07, 0.5),
]


def stroke(freq: float, seconds: float, rng: np.random.Generator) -> np.ndarray:
    t = np.arange(int(seconds * RATE)) / RATE
    out = np.zeros_like(t)
    for ratio, level, tau in PARTIALS:
        f = freq * ratio * (1 + rng.uniform(-0.002, 0.002))
        beat = rng.uniform(0.4, 1.6)  # the twin of each partial is a little off: the bell shimmers
        phase = rng.uniform(0, 2 * np.pi)
        env = np.exp(-t / tau)
        out += level * env * (np.sin(2 * np.pi * f * t + phase) + 0.6 * np.sin(2 * np.pi * (f + beat) * t + phase * 0.5))
    # the strike: a few ms of band-limited noise, loudest at the start
    click = rng.standard_normal(len(t)) * np.exp(-t / 0.004) * 0.25
    click = np.convolve(click, np.ones(6) / 6, mode="same")
    attack = np.minimum(1.0, t / 0.002)  # no click at the very first sample
    return (out + click * 4) * attack


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "assets/drycastle-generated-sounds/church_bells.ogg"))
    ap.add_argument("--seconds", type=float, default=12.0)
    ap.add_argument("--stroke-gap", type=float, default=2.0)
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()

    rng = np.random.default_rng(a.seed)
    n = int(a.seconds * RATE)
    mix = np.zeros(n)
    # two bells alternate: the big one (low) and the small one (a fourth above)
    notes = [196.0, 261.6, 196.0, 261.6, 196.0]
    for i, f in enumerate(notes):
        start = int(i * a.stroke_gap * RATE)
        if start >= n:
            break
        s = stroke(f, a.seconds - i * a.stroke_gap, rng) * (1.0 if i != len(notes) - 1 else 0.85)
        mix[start:start + len(s)] += s[: n - start]
    # echo off the houses: a few soft taps
    echo = mix.copy()
    for delay, gain in ((0.23, 0.30), (0.41, 0.18), (0.67, 0.10)):
        d = int(delay * RATE)
        echo[d:] += gain * mix[:-d]
    mix = echo
    fade = int(1.5 * RATE)
    mix[-fade:] *= np.linspace(1, 0, fade) ** 2
    mix *= 10 ** (-3 / 20) / np.max(np.abs(mix))  # peak -3 dBFS

    pcm = (mix * 32767).astype("<i2")
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        wav = Path(tmp) / "bells.wav"
        with wave.open(str(wav), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
            w.writeframes(pcm.tobytes())
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-ac", "1", "-c:a", "libvorbis", "-q:a", "5", str(out)], check=True)
    print(f"{out}: {a.seconds:.1f} s, peak {20 * np.log10(np.max(np.abs(mix))):.1f} dBFS")


if __name__ == "__main__":
    main()

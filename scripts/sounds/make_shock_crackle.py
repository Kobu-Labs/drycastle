#!/usr/bin/env python3
"""Synthesises the electrocution of a Thief on a pole: assets/drycastle-generated-sounds/shock_crackle.ogg (mono, ~2.2 s, CC0).

An electric arc: a mains hum (50 Hz and harmonics, buzzing), a rising and falling whizz (a fast filtered sweep), and a crackle (random sharp clicks of
band-limited noise, dense in the middle and thinning out at the end), with a loud snap at the start. Deterministic. Needs numpy and ffmpeg:

    python3 scripts/sounds/make_shock_crackle.py [--out PATH] [--seconds 2.2] [--seed 11]
"""
import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from audio import RATE, encode_ogg

ROOT = Path(__file__).resolve().parents[2]


def bandpass_noise(n: int, lo: float, hi: float, rng: np.random.Generator) -> np.ndarray:
    """White noise filtered to lo..hi Hz in the frequency domain."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / RATE)
    spec[(f < lo) | (f > hi)] = 0
    out = np.fft.irfft(spec, n)
    return out / (np.max(np.abs(out)) + 1e-9)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "assets/drycastle-generated-sounds/shock_crackle.ogg"))
    ap.add_argument("--seconds", type=float, default=2.2)
    ap.add_argument("--seed", type=int, default=11)
    a = ap.parse_args()

    rng = np.random.default_rng(a.seed)
    n = int(a.seconds * RATE)
    t = np.arange(n) / RATE
    # the shape of the whole: in at once, a long body, out in the last third of a second
    body = np.minimum(1.0, t / 0.01) * np.clip((a.seconds - t) / 0.35, 0, 1)

    # the mains hum, buzzing: 50 Hz and its harmonics, pushed through a soft clip, the level wobbling
    wobble = 1 + 0.25 * np.sin(2 * np.pi * 7.3 * t) + 0.15 * np.sin(2 * np.pi * 19 * t)
    hum = sum(np.sin(2 * np.pi * 50 * k * t + rng.uniform(0, 6.28)) / k for k in range(1, 9))
    hum = np.tanh(2.2 * hum * wobble) * 0.5

    # the whizz: a sweep that rises to ~3 kHz and falls, a second one a little later (the beams jumping from wire to wire)
    def whizz(start: float, length: float, f0: float, f1: float) -> np.ndarray:
        m = (t >= start) & (t < start + length)
        u = np.clip((t - start) / length, 0, 1)
        f = f0 + (f1 - f0) * np.sin(np.pi * u)            # up and back down
        phase = 2 * np.pi * np.cumsum(f) / RATE
        env = np.where(m, np.sin(np.pi * u) ** 2, 0.0)
        return np.sin(phase) * env * 0.45

    sweep = whizz(0.05, 0.9, 600, 3400) + whizz(0.7, 1.1, 900, 2800) * 0.8

    # the crackle: sharp clicks of band-limited noise; the rate of them falls from 70 to 8 a second
    crackle = np.zeros(n)
    click_len = int(0.012 * RATE)
    click_noise = bandpass_noise(click_len * 8, 1500, 9000, rng)
    tt = 0.0
    while tt < a.seconds:
        rate = 70 * (1 - tt / a.seconds) + 8
        tt += rng.exponential(1 / rate)
        i = int(tt * RATE)
        if i + click_len >= n:
            break
        k = rng.integers(0, len(click_noise) - click_len)
        env = np.exp(-np.arange(click_len) / (click_len * rng.uniform(0.15, 0.4)))
        crackle[i:i + click_len] += click_noise[k:k + click_len] * env * rng.uniform(0.2, 1.0)

    # the snap at the start: a loud broadband burst, 60 ms
    snap = bandpass_noise(n, 300, 12000, rng) * np.exp(-t / 0.03) * np.minimum(1.0, t / 0.001)

    mix = (hum * 0.55 + sweep * 0.6 + crackle * 0.9) * body + snap * 1.2
    mix *= 10 ** (-3 / 20) / np.max(np.abs(mix))  # peak -3 dBFS

    out = Path(a.out)
    encode_ogg((mix * 32767).astype("<i2"), out)
    print(f"{out}: {a.seconds:.1f} s, peak {20 * np.log10(np.max(np.abs(mix))):.1f} dBFS")


if __name__ == "__main__":
    main()

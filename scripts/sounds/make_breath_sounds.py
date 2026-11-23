#!/usr/bin/env python3
"""Synthesises the sniper's breath hold sounds into assets/drycastle-generated-sounds/ (mono, CC0):

    heartbeat.ogg   one beat, "lub-dub": two low thumps 0.14 s apart (the game repeats it, faster as the breath runs out)
    breath_in.ogg   a short sharp inhale (the hold begins)
    breath_out.ogg  a long exhale (the breath is let go, or forced out)

A thump is a sine that falls in pitch (70 to 40 Hz) under a fast decay, with a little low noise for the body. A breath is band-limited noise
whose band moves (an inhale rises, an exhale falls) under a smooth swell. Deterministic. Needs numpy and ffmpeg:

    python3 scripts/sounds/make_breath_sounds.py [--out-dir DIR] [--seed 5]
"""
import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from audio import RATE, encode_ogg

ROOT = Path(__file__).resolve().parents[2]


def band_noise(n: int, lo: float, hi: float, rng: np.random.Generator) -> np.ndarray:
    """White noise filtered to lo..hi Hz in the frequency domain, with soft edges."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / RATE)
    gain = np.clip((f - lo * 0.7) / (lo * 0.3 + 1e-9), 0, 1) * np.clip((hi * 1.4 - f) / (hi * 0.4), 0, 1)
    out = np.fft.irfft(spec * gain, n)
    return out / (np.max(np.abs(out)) + 1e-9)


def thump(t: np.ndarray, at: float, f0: float, f1: float, decay: float, level: float) -> np.ndarray:
    u = np.clip(t - at, 0, None)
    on = t >= at
    f = f1 + (f0 - f1) * np.exp(-u / 0.03)
    phase = 2 * np.pi * np.cumsum(np.where(on, f, 0)) / RATE
    env = np.where(on, np.minimum(1.0, u / 0.004) * np.exp(-u / decay), 0.0)
    return np.sin(phase) * env * level


def heartbeat(rng: np.random.Generator) -> np.ndarray:
    n = int(0.42 * RATE)
    t = np.arange(n) / RATE
    beat = thump(t, 0.005, 75, 42, 0.055, 1.0) + thump(t, 0.145, 95, 50, 0.04, 0.7)
    body = band_noise(n, 30, 160, rng) * (np.exp(-np.clip(t - 0.005, 0, None) / 0.03) + 0.6 * np.where(t >= 0.145, np.exp(-(t - 0.145) / 0.025), 0))
    mix = np.tanh(1.6 * (beat + 0.25 * body))
    return mix * np.clip((0.42 - t) / 0.05, 0, 1)


def breath(seconds: float, rise: float, f_from: tuple, f_to: tuple, rng: np.random.Generator) -> np.ndarray:
    """Noise whose band glides from f_from (lo, hi) to f_to; the level swells to its peak at `rise` of the length and falls away."""
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    u = t / seconds
    a = band_noise(n, f_from[0], f_from[1], rng)
    b = band_noise(n, f_to[0], f_to[1], rng)
    mix = a * (1 - u) + b * u
    env = np.where(u < rise, np.sin(0.5 * np.pi * u / rise) ** 2, np.clip(np.cos(0.5 * np.pi * (u - rise) / (1 - rise)), 0, None) ** 1.5)
    flutter = 1 + 0.08 * np.sin(2 * np.pi * 23 * t) + 0.05 * np.sin(2 * np.pi * 37 * t)   # the air is never even
    return mix * env * flutter


def write(out: Path, mix: np.ndarray, peak_db: float) -> None:
    mix = mix * 10 ** (peak_db / 20) / np.max(np.abs(mix))
    encode_ogg((mix * 32767).astype("<i2"), out)
    print(f"{out}: {len(mix) / RATE:.2f} s, peak {peak_db:.1f} dBFS")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(ROOT / "assets/drycastle-generated-sounds"))
    ap.add_argument("--seed", type=int, default=5)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    out = Path(a.out_dir)
    write(out / "heartbeat.ogg", heartbeat(rng), -3)
    write(out / "breath_in.ogg", breath(0.55, 0.7, (500, 1800), (1200, 4500), rng), -6)
    write(out / "breath_out.ogg", breath(1.1, 0.18, (900, 4200), (300, 1400), rng), -4)


if __name__ == "__main__":
    main()

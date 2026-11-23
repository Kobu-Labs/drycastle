#!/usr/bin/env python3
"""Synthesises the clink of a spent casing landing on a hard floor: assets/drycastle-generated-sounds/casing_tink_1..4.ogg (mono, ~0.2 s, CC0).

A bright metal tink (#307): three inharmonic sine modes (about 6.0, 10.4 and 15.2 kHz) with fast exponential decays and a 0.4 ms attack. Each variant detunes the modes a
little, so a burst does not repeat one sample. One landing is one file (the game plays one per bounce, pitch-varied). Deterministic. Needs numpy and ffmpeg:

    python3 scripts/sounds/make_casing_clinks.py [--out-dir DIR] [--variants 4] [--seed 307]
"""
import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from audio import RATE, encode_ogg

ROOT = Path(__file__).resolve().parents[2]
MODES = [(6000.0, 0.030, 1.0), (10400.0, 0.018, 0.6), (15200.0, 0.009, 0.4)]  # Hz, decay time constant in s, level


def tink(rng: np.random.Generator, seconds: float = 0.2) -> np.ndarray:
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    y = np.zeros(n)
    detune = rng.uniform(0.94, 1.06)
    for f, decay, amp in MODES:
        y += amp * np.sin(2 * np.pi * f * detune * rng.uniform(0.99, 1.01) * t + rng.uniform(0, 6.28)) * np.exp(-t / decay)
    y *= np.minimum(1.0, t / 0.0004)
    y *= np.clip((seconds - t) / 0.02, 0, 1)  # no click at the end
    return y * 10 ** (-3 / 20) / np.max(np.abs(y))  # peak -3 dBFS


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(ROOT / "assets/drycastle-generated-sounds"))
    ap.add_argument("--variants", type=int, default=4)
    ap.add_argument("--seed", type=int, default=307)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    out_dir = Path(a.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    for i in range(1, a.variants + 1):
        out = out_dir / f"casing_tink_{i}.ogg"
        encode_ogg((tink(rng) * 32767).astype("<i2"), out)
        print(out)


if __name__ == "__main__":
    main()

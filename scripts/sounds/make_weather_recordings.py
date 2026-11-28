#!/usr/bin/env python3
"""Cuts the weather sounds of assets/public-domain-weather/ from free recordings (rain loops, thunder, an electric crackle).

The sources are downloaded by hand (their URLs, authors and licences are in assets/public-domain-weather/LICENSE.txt) into one folder, SRC, under these names:

    amb_rain2.ogg, amb_rain_loop_1.ogg                                     OpenGameArt "AMB Rain Loop 2 / 1" (CC0)
    thunderstorm_01.ogg .. thunderstorm_04.ogg                             Wikimedia Commons "Thunderstorm after hot summer day 17 minutes 01..04 of 04" (public domain)
    crackleelectricityloop.wav                                             OpenGameArt "Electricity Game Sound Pack" (CC0)

    python3 scripts/sounds/make_weather_recordings.py SRC [--out-dir DIR]

Rain: a quiet 30 s stretch of each recording, made seamless by cross-fading its tail into its head (equal power over 3 s), level-matched by RMS. Thunder: the loudest events of the
storm recording; the far rumbles are low-passed (900 Hz: the rain's hiss goes, what is left is the roll) with a slow fade in and out, the near cracks keep every frequency and start on the
crack itself with the roll after it. Needs numpy and ffmpeg. Deterministic.
"""
import argparse
import subprocess
import sys
from pathlib import Path

import numpy as np

SR = 44100
ROOT = Path(__file__).resolve().parents[2]

# (source file, start seconds, length seconds, target RMS dBFS): the steadiest 30 s of each recording (no birds, no thunder, no cars)
RAIN = {
    "rain_heavy": ("amb_rain2.ogg", 652.0, 30.0, -21.0),
    "rain_light": ("amb_rain_loop_1.ogg", 15.0, 30.0, -25.0),
}
XFADE = 3.0

# (storm part 1..4, seconds into it of the event's loudest moment): far rumbles and near cracks
FAR = [(1, 33.5), (2, 103.8), (3, 184.7), (4, 148.4), (4, 218.3)]
NEAR = [(2, 22.2), (2, 228.1), (3, 194.0), (4, 5.6)]


def load(path, af="anull"):
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(SR), "-af", af, "-f", "f32le", "-"], capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).copy()


def load_stereo(path):
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "2", "-ar", str(SR), "-f", "f32le", "-"], capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).reshape(-1, 2).copy()


def encode(x, out, channels):
    out.parent.mkdir(parents=True, exist_ok=True)
    x = np.clip(x, -1, 1).astype(np.float32)
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-f", "f32le", "-ar", str(SR), "-ac", str(channels), "-i", "-", "-c:a", "libvorbis", "-q:a", "4", str(out)], input=x.tobytes(), check=True)
    print(f"{out.name}: {len(x) / SR / channels:.1f} s")


def db(v):
    return 20 * np.log10(max(float(v), 1e-9))


def rms_db(x):
    return db(np.sqrt(np.mean(x.astype(np.float64) ** 2)))


def seamless(seg, xfade):
    """seg is L + xfade samples long: the head is cross-faded with the tail, so the end runs straight into the start."""
    n = len(seg) - xfade
    t = np.linspace(0, 1, xfade, dtype=np.float32)
    fin, fout = np.sin(t * np.pi / 2), np.cos(t * np.pi / 2)
    out = seg[:n].copy()
    out[:xfade] = seg[:xfade] * fin[:, None] + seg[n:n + xfade] * fout[:, None]
    return out


def rain(src, out):
    for name, (file, start, length, target) in RAIN.items():
        x = load_stereo(src / file)
        a = int(start * SR)
        seg = x[a:a + int((length + XFADE) * SR)]
        loop = seamless(seg, int(XFADE * SR))
        loop *= 10 ** ((target - rms_db(loop)) / 20)
        peak = np.abs(loop).max()
        if peak > 0.89:
            loop *= 0.89 / peak
        encode(loop.reshape(-1), out / f"{name}.ogg", 2)
        # a mono copy for the little cloud over the Thundernite (a 3D voice)
        encode(loop.mean(axis=1), out / f"{name}_mono.ogg", 1)


def frames_db(x, fr):
    n = len(x) // fr
    return 20 * np.log10(np.sqrt((x[:n * fr].reshape(n, fr).astype(np.float64) ** 2).mean(1)) + 1e-9)


def thunder(src, out):
    for part in sorted({p for p, _ in FAR + NEAR}):
        f = src / f"thunderstorm_0{part}.ogg"
        full = load(f)
        lp = load(f, "lowpass=f=900:poles=2")
        hp = load(f, "highpass=f=1500")
        hp_frames = frames_db(hp, int(SR * 0.02))
        lp_frames = frames_db(lp, int(SR * 0.1))
        for kind, events in (("far", FAR), ("near", NEAR)):
            for i, (p, t) in enumerate(events):
                if p != part:
                    continue
                if kind == "far":
                    k = int(t * 10)
                    base = np.median(lp_frames[max(0, k - 60):max(1, k - 20)])
                    peak = lp_frames[max(0, k - 5):k + 5].max()
                    on = next(j for j in range(max(0, k - 40), k + 1) if lp_frames[j] >= peak - 14)
                    end = k + 40
                    while end < min(len(lp_frames) - 1, k + 100) and lp_frames[end] > base + 4:
                        end += 1
                    a, b = int((on / 10 - 0.6) * SR), int((end / 10 + 0.8) * SR)
                    seg = lp[a:b].copy()
                    seg *= np.minimum(1, np.arange(len(seg)) / (0.5 * SR))
                    seg *= np.minimum(1, np.arange(len(seg))[::-1] / (1.8 * SR))
                    seg *= 10 ** ((-3 - db(np.abs(seg).max())) / 20)
                    encode(seg, out / f"thunder_far_{[e for e in FAR].index((p, t)) + 1}.ogg", 1)
                else:
                    k = int(t * 50)
                    peak = hp_frames[max(0, k - 25):k + 25].max()
                    on = next(j for j in range(max(0, k - 75), k + 15) if hp_frames[j] >= peak - 10)
                    a, b = int(on / 50 * SR) - int(0.04 * SR), int((on / 50 + 8.5) * SR)
                    seg = highpass(full[a:b])
                    seg *= np.minimum(1, np.arange(len(seg)) / (0.004 * SR))
                    seg *= np.minimum(1, np.arange(len(seg))[::-1] / (2.0 * SR))
                    seg *= 10 ** ((-2 - db(np.abs(seg).max())) / 20)
                    encode(seg, out / f"thunder_near_{[e for e in NEAR].index((p, t)) + 1}.ogg", 1)


def highpass(x, hz=30):
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    spec *= np.clip(f / hz, 0, 1) ** 2
    return np.fft.irfft(spec, len(x)).astype(np.float32)


def crackle(src, out):
    x = load(src / "crackleelectricityloop.wav")
    x = highpass(x, 200)
    x *= 10 ** ((-3 - db(np.abs(x).max())) / 20)
    x *= np.minimum(1, np.arange(len(x))[::-1] / (0.05 * SR))
    encode(x, out / "electric_crackle.ogg", 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src", type=Path)
    ap.add_argument("--out-dir", type=Path, default=ROOT / "assets" / "public-domain-weather")
    a = ap.parse_args()
    for step in (rain, thunder, crackle):
        step(a.src, a.out_dir)


if __name__ == "__main__":
    sys.exit(main())

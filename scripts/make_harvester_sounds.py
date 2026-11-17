#!/usr/bin/env python3
"""Synthesises the sounds of the harvester catching a player (#331), stylised, no screams: assets/drycastle-generated-sounds/harvester_{reel_in,chew,eject,pop}.ogg (mono, CC0).

  reel_in  3.7 s  the reel grabs and spins the body round for HarvesterConfig.DrawSeconds (3.6 s, #351): a whirr rising in pitch with a chopping rhythm that speeds up, the engine straining underneath (its pitch dropping under load), a clank at the start
  chew     2.6 s  inside the machine, muffled (everything under ~900 Hz): a labouring rumble, irregular crunching thuds, the grinding pulsing about 8 times a second, easing off at the end
  eject    0.8 s  the chute pops the bale out: a pneumatic whoomp (a short falling sine and a puff of filtered noise) and a dull clank
  pop      0.4 s  a bale comes out (#352): a hollow "pop", a quick falling sine with a click of noise. The fallback of harvester.pop: when the private clip audio/harvester/pop.ogg is installed it plays instead

Deterministic. Needs numpy and ffmpeg:

    python3 scripts/make_harvester_sounds.py [--out-dir DIR] [--seed 7]
"""
import argparse
import subprocess
import tempfile
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
RATE = 44100


def noise(n: int, lo: float, hi: float, rng: np.random.Generator) -> np.ndarray:
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / RATE)
    spec[(f < lo) | (f > hi)] = 0
    out = np.fft.irfft(spec, n)
    return out / (np.max(np.abs(out)) + 1e-9)


def lowpass(x: np.ndarray, hz: float) -> np.ndarray:
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / RATE)
    spec[f > hz] = 0
    return np.fft.irfft(spec, len(x))


def saw(phase: np.ndarray) -> np.ndarray:
    return 2 * ((phase / (2 * np.pi)) % 1.0) - 1


def thud(n: int, at: float, f0: float, decay: float, amp: float) -> np.ndarray:
    t = np.arange(n) / RATE
    i = int(at * RATE)
    out = np.zeros(n)
    m = t[: n - i]
    out[i:] = np.sin(2 * np.pi * f0 * m * np.exp(-m / (decay * 2))) * np.exp(-m / decay) * amp
    return out


def save(mix: np.ndarray, out: Path) -> None:
    mix = mix * 10 ** (-3 / 20) / (np.max(np.abs(mix)) + 1e-9)  # peak -3 dBFS
    pcm = (mix * 32767).astype("<i2")
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        wav = Path(tmp) / "s.wav"
        with wave.open(str(wav), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
            w.writeframes(pcm.tobytes())
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-ac", "1", "-c:a", "libvorbis", "-q:a", "5", str(out)], check=True)
    print(f"{out}: {len(mix) / RATE:.1f} s")


def reel_in(rng: np.random.Generator) -> np.ndarray:
    secs = 3.7
    n = int(secs * RATE); t = np.arange(n) / RATE; u = t / secs
    env = np.minimum(1, t / 0.03) * np.clip((secs - t) / 0.5, 0, 1)
    # the whirr: a saw rising 140 -> 620 Hz, then thin it with a band so it reads as a spinning blade, not a siren
    f = 140 + 480 * u ** 1.6
    whirr = lowpass(saw(2 * np.pi * np.cumsum(f) / RATE), 2500) * 0.5
    # the chop: the reel's blades, a pulse train from 6 to 16 per second, as amplitude modulation
    rate = 6 + 10 * u
    chop = 0.55 + 0.45 * np.maximum(0, np.sin(2 * np.pi * np.cumsum(rate) / RATE)) ** 2
    # the engine under load: a rumble whose pitch falls 95 -> 62 Hz as it strains, harmonics up to 6
    fe = 95 - 33 * u
    pe = 2 * np.pi * np.cumsum(fe) / RATE
    engine = sum(np.sin(k * pe + k) / k for k in range(1, 7)) * 0.45
    engine = np.tanh(1.8 * engine)
    # the clank at the grab: a short dull metal ring
    clank = np.zeros(n)
    for f0, a in ((420, 0.5), (780, 0.35), (1260, 0.2)):
        clank += np.sin(2 * np.pi * f0 * t) * np.exp(-t / 0.05) * a
    clank += noise(n, 200, 4000, rng) * np.exp(-t / 0.012) * 0.6
    return (whirr * chop * 0.8 + engine * 0.7) * env + clank * 0.9


def chew(rng: np.random.Generator) -> np.ndarray:
    secs = 2.6
    n = int(secs * RATE); t = np.arange(n) / RATE
    env = np.minimum(1, t / 0.08) * np.clip((secs - t) / 0.6, 0, 1)
    # the labouring rumble, a little unsteady
    wob = 1 + 0.12 * np.sin(2 * np.pi * 3.1 * t) + 0.08 * np.sin(2 * np.pi * 8.7 * t)
    rumble = sum(np.sin(2 * np.pi * 48 * k * t * wob + k) / k for k in range(1, 6))
    rumble = np.tanh(1.6 * rumble) * 0.55
    # the grinding: band noise pulsing about 8 times a second
    grind = noise(n, 120, 800, rng) * (0.5 + 0.5 * np.sin(2 * np.pi * 8 * t) ** 2) * 0.7
    # irregular crunches: low thuds and short bursts of dull noise, about 5 a second, thinning at the end
    crunch = np.zeros(n)
    tt = 0.15
    while tt < secs - 0.3:
        tt += rng.exponential(0.2) + 0.05
        i = int(tt * RATE)
        ln = int(rng.uniform(0.03, 0.08) * RATE)
        if i + ln >= n:
            break
        burst = noise(ln * 4, 90, 700, rng)[:ln] * np.exp(-np.arange(ln) / (ln * 0.3))
        crunch[i:i + ln] += burst * rng.uniform(0.5, 1.0)
        crunch += thud(n, tt, rng.uniform(70, 120), 0.05, rng.uniform(0.2, 0.5))
    mix = (rumble * 0.8 + grind * 0.6 + crunch * 0.9) * env
    return lowpass(mix, 900)  # muffled: it is inside the machine


def eject(rng: np.random.Generator) -> np.ndarray:
    secs = 0.8
    n = int(secs * RATE); t = np.arange(n) / RATE
    # the whoomp: a falling sine 150 -> 45 Hz and a puff of air (noise, 150..2500 Hz, quick decay)
    whoomp = np.sin(2 * np.pi * (45 + 105 * np.exp(-t / 0.08)) * t) * np.exp(-t / 0.18) * 0.9
    puff = noise(n, 150, 2500, rng) * np.exp(-t / 0.07) * np.minimum(1, t / 0.004) * 0.6
    # the dull clank of the bale landing a moment later
    land = thud(n, 0.22, 85, 0.09, 0.7) + noise(n, 100, 1200, rng) * np.exp(-np.maximum(0, t - 0.22) / 0.03) * (t > 0.22) * 0.35
    return whoomp + puff + land


def pop(rng: np.random.Generator) -> np.ndarray:
    secs = 0.4
    n = int(secs * RATE); t = np.arange(n) / RATE
    # the pop: a sine falling 520 -> 150 Hz in a few ms, over a short hollow body (a second, softer sine an octave down) and a click of band noise on the first millisecond
    body = np.sin(2 * np.pi * (150 + 370 * np.exp(-t / 0.018)) * t) * np.exp(-t / 0.07) * 0.9
    hollow = np.sin(2 * np.pi * (80 + 120 * np.exp(-t / 0.03)) * t) * np.exp(-t / 0.11) * 0.45
    click = noise(n, 800, 6000, rng) * np.exp(-t / 0.006) * np.minimum(1, t / 0.0008) * 0.7
    return np.tanh(1.3 * (body + hollow + click)) * np.clip((secs - t) / 0.05, 0, 1)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=str(ROOT / "assets/drycastle-generated-sounds"))
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    out = Path(a.out_dir)
    save(reel_in(rng), out / "harvester_reel_in.ogg")
    save(chew(rng), out / "harvester_chew.ogg")
    save(eject(rng), out / "harvester_eject.ogg")
    save(pop(rng), out / "harvester_pop.ogg")


if __name__ == "__main__":
    main()

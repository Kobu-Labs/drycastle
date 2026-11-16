#!/usr/bin/env python3
"""Golf Mk2 engine sounds (vehicles.json golf_mk2 "sounds"): cuts the recorded layers from two source clips and synthesises the background rumble.

Needs numpy and ffmpeg (libvorbis). Not stdlib-only, so do not run it with -I. The source clips are other people's recordings: the output goes to
assets/golf-mk2-sounds/ (gitignored), never into assets/.

  make_golf_mk2_sounds.py --idle idle.wav --drive drive.wav [--out DIR]

  idle  : a steady idle (loops seamlessly).
  drive : a pull through the gears, then off the gas with the turbo blowing off.
Cut times (seconds into the drive clip) are the constants below; change them, rerun, listen.
"""
import argparse, subprocess, sys, wave
from pathlib import Path
import numpy as np

SR = 48000
ACCEL = (2.5, 5.9)     # the pull from where the revs start to climb (before it the clip is still idling), through the first gear shift (about 5.4) and just into second, looped whole
TOP = (9.2, 10.35)      # the top speed engine loop, smaller still
TOP_LOOP = (0.4, 0.6)
LIFT = (11.65, 13.1)    # off the gas: the turbo blow-off and the revs falling
IDLE_XFADE, ACCEL_XFADE, TOP_XFADE = 0.6, 0.25, 0.08


def load(path):
    w = wave.open(str(path))
    a = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32).reshape(-1, w.getnchannels()).mean(1) / 32768
    if w.getframerate() != SR:
        sys.exit(f"{path}: expected {SR} Hz (ffmpeg -ar {SR})")
    return a


def seam(x, xf):
    """A loop from a clip: the tail crossfaded (equal power) over the head, so the end meets the start."""
    n = int(xf * SR)
    t = np.linspace(0, np.pi / 2, n, dtype=np.float32)
    head, tail = x[:n], x[-n:]
    mid = tail * np.cos(t) + head * np.sin(t)
    return np.concatenate([mid, x[n:-n]])


def lowpass(x, hz):
    sp = np.fft.rfft(x)
    sp[np.fft.rfftfreq(len(x), 1 / SR) > hz] = 0
    return np.fft.irfft(sp, len(x)).astype(np.float32)


def best_loop(x, lo, hi, min_len, max_len, ctx=0.1):
    """The loop [a, b) inside x[lo:hi] whose seam is least audible: the signal before b should look like the signal before a (so that continuing
    from b into a is continuous in wave shape, not just level). Returns a and b as sample indexes into x."""
    n = int(ctx * SR)
    best = None
    seg = x[int(lo * SR):int(hi * SR)]
    low = lowpass(seg, 700)  # the firing pattern, not the noise on top of it, has to line up
    for a in range(n, len(seg) - int(min_len * SR), int(0.004 * SR)):
        tpl = low[a - n:a]
        e_t = np.sqrt((tpl ** 2).sum()) + 1e-9
        b0, b1 = a + int(min_len * SR), min(len(seg), a + int(max_len * SR))
        if b1 <= b0: continue
        win = low[b0 - n:b1]
        c = np.correlate(win, tpl, mode="valid")
        cs = np.concatenate([[0], np.cumsum(win ** 2)])
        e_w = np.sqrt(cs[n:n + len(c)] - cs[:len(c)]) + 1e-9
        score = c / (e_t * e_w)
        k = int(score.argmax())
        if best is None or score[k] > best[0]: best = (float(score[k]), a, b0 + k)
    score, a, b = best
    a, b = a + int(lo * SR), b + int(lo * SR)
    print(f"loop {a / SR:.3f}-{b / SR:.3f} s, seam match {score:.2f}")
    return a, b


def loop_clip(x, a, b, xf):
    """x[a:b] as a seamless loop: its end blends into the signal that came just before a, so b flows on into a."""
    n = int(xf * SR)
    t = np.linspace(0, np.pi / 2, n, dtype=np.float32)
    c = x[a:b].copy()
    c[-n:] = x[b - n:b] * np.cos(t) + x[a - n:a] * np.sin(t)
    return c


def fades(x, fin, fout):
    x = x.copy()
    a, b = int(fin * SR), int(fout * SR)
    if a: x[:a] *= np.linspace(0, 1, a, dtype=np.float32)
    if b: x[-b:] *= np.linspace(1, 0, b, dtype=np.float32)
    return x


def noise(seconds, lo, hi, tilt=0.0, seed=0):
    """Band-limited noise that is periodic over its length (FFT-filtered, so it loops with no seam). tilt: spectral slope, 1/f^tilt."""
    n = int(seconds * SR)
    rng = np.random.default_rng(seed)
    sp = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1 / SR)
    m = ((f >= lo) & (f <= hi)).astype(np.float32)
    m *= 1 / np.maximum(f, 1) ** tilt
    m = np.convolve(m, np.ones(9) / 9, mode="same")  # soft band edges
    x = np.fft.irfft(sp * m, n).astype(np.float32)
    return x / np.abs(x).max()


def cycles(seconds, hz):
    """A sine with a whole number of cycles in the loop."""
    n = int(seconds * SR)
    hz = round(hz * seconds) / seconds
    return np.sin(2 * np.pi * hz * np.arange(n) / SR).astype(np.float32)


def background():
    """Low drivetrain and road rumble under everything. 4 s loop."""
    d = 4.0
    return 0.9 * noise(d, 25, 320, 0.6, 4) * (0.9 + 0.1 * cycles(d, 0.5))


def write(path, x, peak=0.8):
    x = x / max(1e-6, np.abs(x).max()) * peak
    p = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "s16le", "-ar", str(SR), "-ac", "1", "-i", "-", "-c:a", "libvorbis", "-q:a", "5", str(path)],
                       input=(x * 32767).astype(np.int16).tobytes())
    if p.returncode: sys.exit("ffmpeg failed")
    print(f"{path.name}: {len(x) / SR:.2f} s")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--idle", required=True)
    ap.add_argument("--drive", required=True)
    ap.add_argument("--out", default=str(Path(__file__).resolve().parent.parent / "assets/golf-mk2-sounds"))
    a = ap.parse_args()
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    idle, drive = load(a.idle), load(a.drive)
    cut = lambda x, r: x[int(r[0] * SR):int(r[1] * SR)]
    write(out / "idle.ogg", seam(idle, IDLE_XFADE))
    write(out / "accel.ogg", seam(cut(drive, ACCEL), ACCEL_XFADE))
    a, b = best_loop(drive, *TOP, *TOP_LOOP)
    write(out / "top.ogg", loop_clip(drive, a, b, TOP_XFADE))
    write(out / "lift.ogg", fades(cut(drive, LIFT), 0.02, 0.35))
    write(out / "background.ogg", background())


main()

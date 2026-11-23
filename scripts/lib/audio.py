"""Shared bits of the sound scripts (scripts/sounds/): 16-bit mono PCM -> .wav -> Ogg Vorbis through ffmpeg, and the ffmpeg/ffprobe helpers
of the two voice bakes."""
import json
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

RATE = 44100


def write_wav(path, pcm, rate=RATE):
    """A mono 16-bit .wav from an int16 numpy array."""
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())


def encode_ogg(pcm, out, rate=RATE):
    """pcm (int16 numpy array, mono) -> libvorbis quality 5 at `out`."""
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        wav = Path(tmp) / "s.wav"
        write_wav(wav, pcm, rate)
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-ac", "1", "-c:a", "libvorbis", "-q:a", "5", str(out)], check=True)


def run(cmd):
    """Runs an ffmpeg/ffprobe command line; exits with its stderr on failure."""
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"{' '.join(cmd[:3])} failed:\n{r.stderr[-1500:]}")
    return r


def duration(path):
    return float(run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)]).stdout)


def load_voice(path):
    """The shared voice-chain file (godot/shooter/data/*_voice.json): params keep the literal text written in it (floats as str), so every builder
    spells each value as written."""
    text = Path(path).read_text()
    v = json.loads(text)
    if v.get("version") != 1:
        sys.exit(f"{path}: unsupported version {v.get('version')}")
    v["params"] = json.loads(text, parse_float=str, parse_int=str)["params"]
    return v

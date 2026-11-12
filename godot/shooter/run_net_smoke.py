#!/usr/bin/env python3
"""Headless netcode smoke: one server + two scripted bot clients on the real map.

Asserts the server saw 2 players moving and each client saw the other move.
Usage: run_net_smoke.py [--map DIR] [--port N] [--seconds N]
"""
import argparse, os, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
ap.add_argument("--port", type=int, default=27931)
ap.add_argument("--seconds", type=float, default=6)
a = ap.parse_args()
godot = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
base = [godot, "--headless", "--path", str(HERE), "--"]

def spawn(extra):
    return subprocess.Popen(base + extra + mapargs, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

procs = {}
procs["server"] = spawn(["--role", "server", "--port", str(a.port), "--quit-after", str(a.seconds + 8)])
time.sleep(4)  # server loads the map and starts listening
for n in ("alice", "bob"):
    procs[n] = spawn(["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{a.port}", "--name", n, "--bot-seconds", str(a.seconds)])
out, codes = {}, {}
for k in ("alice", "bob", "server"):
    try:
        out[k], _ = procs[k].communicate(timeout=a.seconds + 60)
    except subprocess.TimeoutExpired:
        procs[k].kill(); out[k], _ = procs[k].communicate(); out[k] += "\nTIMEOUT"
    codes[k] = procs[k].returncode
ok = True
for k, marker in (("alice", "SHOOTER_NET_CLIENT_OK"), ("bob", "SHOOTER_NET_CLIENT_OK"), ("server", "SHOOTER_NET_SERVER_OK")):
    good = marker in out[k] and codes[k] == 0
    ok &= good
    print(f"--- {k}: {'ok' if good else 'FAIL'} (exit {codes[k]})")
    keep = [l for l in out[k].splitlines() if l.startswith(("client", "server")) or "FAIL" in l or "OK" in l or "rror" in l]
    print("\n".join(keep[-12:]))
print("SHOOTER_NET_SMOKE_OK" if ok else "SHOOTER_NET_SMOKE_FAIL")
sys.exit(0 if ok else 1)

#!/usr/bin/env python3
"""Headless car smoke on the weapons smoke's flat arena (three cars) with two scripted bots.

alice enters a car (E), cannot shoot, drives 20+ m with full throttle into bob (run-over, capped damage, kill credited to the car),
stops with the handbrake, exits to a free side, and the abandoned car returns to its origin after the shortened timer (3 s).
Usage: run_car_smoke.py [--map DIR] [--port N]
"""
import argparse, os, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument("--map", default=os.environ.get("SUCHOHRAD_MAP", ""))
ap.add_argument("--port", type=int, default=27936)
a = ap.parse_args()
godot = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
base = [godot, "--headless", "--path", str(HERE), "--"]
overrides = "score_goal=50,time_limit=600,respawn_delay=1,spawn_protection=0.4,end_screen_time=3,min_spawn_distance=0"
deadline = 50

def spawn(extra):
    return subprocess.Popen(base + extra + mapargs, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

procs = {"server": spawn(["--role", "server", "--port", str(a.port), "--car-smoke", "--match-set", overrides, "--vehicle-set", "abandon_return_time=3", "--quit-after", str(deadline)])}
time.sleep(4)
procs["bob"] = spawn(["--role", "client", "--headless-bot", "--car-bot", "b", "--connect", f"127.0.0.1:{a.port}", "--name", "bob", "--bot-seconds", str(deadline - 3)])
time.sleep(1.5)
procs["alice"] = spawn(["--role", "client", "--headless-bot", "--car-bot", "a", "--connect", f"127.0.0.1:{a.port}", "--name", "alice", "--bot-seconds", str(deadline - 5)])
out, codes = {}, {}
for k in ("alice", "bob", "server"):
    try:
        out[k], _ = procs[k].communicate(timeout=deadline + 60)
    except subprocess.TimeoutExpired:
        procs[k].kill(); out[k], _ = procs[k].communicate(); out[k] += "\nTIMEOUT"
    codes[k] = procs[k].returncode
ok = True
for k, marker in (("alice", "SHOOTER_CAR_CLIENT_OK"), ("bob", "SHOOTER_CAR_CLIENT_OK"), ("server", "SHOOTER_CAR_SERVER_OK")):
    good = marker in out[k] and codes[k] == 0
    ok &= good
    print(f"--- {k}: {'ok' if good else 'FAIL'} (exit {codes[k]})")
    verbose = os.environ.get("VERBOSE")
    keep = out[k].splitlines() if verbose else [l for l in out[k].splitlines() if l.startswith(("client", "server")) or "FAIL" in l or "OK" in l or "rror" in l]
    print("\n".join(keep if (verbose or not good or k == "alice") else keep[-6:]))
print("SHOOTER_CAR_SMOKE_OK" if ok else "SHOOTER_CAR_SMOKE_FAIL")
sys.exit(0 if ok else 1)

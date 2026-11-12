#!/usr/bin/env python3
"""Headless weapons smoke: a server with a flat test arena (floor plus a wall, far from the map) and two scripted bots.

alice shoots bob: AK-47 damage numbers, headshot, switch time, wall blocking, reload timing, knife reach and backstab,
the ammo refill on a kill, a second life with two frontal knife hits. Alice asserts from her events and snapshots, bob
from the hit events he receives, the server from its own counters. Each prints *_OK.
Usage: run_weapons_smoke.py [--map DIR] [--port N]
"""
import argparse, os, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
ap.add_argument("--port", type=int, default=27933)
a = ap.parse_args()
godot = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
base = [godot, "--headless", "--path", str(HERE), "--"]
overrides = "score_goal=50,time_limit=600,respawn_delay=1,spawn_protection=0.4,end_screen_time=3,min_spawn_distance=0"
deadline = 50  # seconds of server time after which the smoke fails

def spawn(extra):
    return subprocess.Popen(base + extra + mapargs, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

procs = {"server": spawn(["--role", "server", "--port", str(a.port), "--weapons-smoke", "--match-set", overrides, "--quit-after", str(deadline)])}
time.sleep(4)  # server loads the map and starts listening
procs["bob"] = spawn(["--role", "client", "--headless-bot", "--weapons-bot", "b", "--connect", f"127.0.0.1:{a.port}", "--name", "bob", "--bot-seconds", str(deadline - 3)])
time.sleep(1.5)
procs["alice"] = spawn(["--role", "client", "--headless-bot", "--weapons-bot", "a", "--connect", f"127.0.0.1:{a.port}", "--name", "alice", "--bot-seconds", str(deadline - 5)])
out, codes = {}, {}
for k in ("alice", "bob", "server"):
    try:
        out[k], _ = procs[k].communicate(timeout=deadline + 60)
    except subprocess.TimeoutExpired:
        procs[k].kill(); out[k], _ = procs[k].communicate(); out[k] += "\nTIMEOUT"
    codes[k] = procs[k].returncode
ok = True
for k, marker in (("alice", "SHOOTER_WEAPONS_CLIENT_OK"), ("bob", "SHOOTER_WEAPONS_CLIENT_OK"), ("server", "SHOOTER_WEAPONS_SERVER_OK")):
    good = marker in out[k] and codes[k] == 0
    ok &= good
    print(f"--- {k}: {'ok' if good else 'FAIL'} (exit {codes[k]})")
    verbose = os.environ.get("VERBOSE")
    keep = out[k].splitlines() if verbose else [l for l in out[k].splitlines() if l.startswith(("client", "server")) or "FAIL" in l or "OK" in l or "rror" in l]
    print("\n".join(keep if (verbose or not good or k == "alice") else keep[-6:]))
print("SHOOTER_WEAPONS_SMOKE_OK" if ok else "SHOOTER_WEAPONS_SMOKE_FAIL")
sys.exit(0 if ok else 1)

#!/usr/bin/env python3
"""Headless vehicle damage and blast smoke (ticket #103) on the weapons smoke's flat arena: three cars (two side by side, one far away), two scripted bots.

alice empties an AK-47 into car 0 (damage stages 1, 2, 3 in order, then the blast), bob stands beside it and dies in the blast (credited to alice),
alice takes her self-reduced share, car 1 takes chain damage, a bazooka rocket damages car 2, car 0 returns undamaged, and finally alice drives car 0 and dies in its blast.
Usage: run_blast_smoke.py [--map DIR] [--port N]
"""
import argparse, os, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
ap.add_argument("--port", type=int, default=27946)
a = ap.parse_args()
godot = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
base = [godot, "--headless", "--path", str(HERE), "--"]
overrides = "score_goal=50,time_limit=600,respawn_delay=1,spawn_protection=0.4,end_screen_time=3,min_spawn_distance=0"
deadline = 70

def spawn(extra):
    return subprocess.Popen(base + extra + mapargs, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

procs = {"server": spawn(["--role", "server", "--port", str(a.port), "--blast-smoke", "--car-kind", "car", "--match-set", overrides, "--vehicle-set", "destroyed_return_time=3", "--quit-after", str(deadline)])}
time.sleep(4)
procs["bob"] = spawn(["--role", "client", "--headless-bot", "--blast-bot", "b", "--connect", f"127.0.0.1:{a.port}", "--name", "bob", "--bot-seconds", str(deadline - 3)])
time.sleep(1.5)
procs["alice"] = spawn(["--role", "client", "--headless-bot", "--blast-bot", "a", "--connect", f"127.0.0.1:{a.port}", "--name", "alice", "--bot-seconds", str(deadline - 5)])
out, codes = {}, {}
for k in ("alice", "bob", "server"):
    try:
        out[k], _ = procs[k].communicate(timeout=deadline + 60)
    except subprocess.TimeoutExpired:
        procs[k].kill(); out[k], _ = procs[k].communicate(); out[k] += "\nTIMEOUT"
    codes[k] = procs[k].returncode
ok = True
for k, marker in (("alice", "SHOOTER_BLAST_CLIENT_OK"), ("bob", "SHOOTER_BLAST_CLIENT_OK"), ("server", "SHOOTER_BLAST_SERVER_OK")):
    good = marker in out[k] and codes[k] == 0
    ok &= good
    print(f"--- {k}: {'ok' if good else 'FAIL'} (exit {codes[k]})")
    verbose = os.environ.get("VERBOSE")
    keep = out[k].splitlines() if verbose else [l for l in out[k].splitlines() if l.startswith(("client", "server")) or "FAIL" in l or "OK" in l or "rror" in l]
    print("\n".join(keep if (verbose or not good or k == "alice") else keep[-8:]))
print("SHOOTER_BLAST_SMOKE_OK" if ok else "SHOOTER_BLAST_SMOKE_FAIL")
sys.exit(0 if ok else 1)

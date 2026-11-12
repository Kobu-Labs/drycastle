#!/usr/bin/env python3
"""Headless match-loop smoke: a server with a low kill goal and short timers plus two scripted bots on the real map.

alice kills bob with the debug command (first while he is spawn protected, which must not count, then once his
protection has ended early because bob fires). After the kill goal the round ends, the end screen runs out and the
match restarts with scores reset. The server and both bots assert their half and print *_OK.
Usage: run_match_smoke.py [--map DIR] [--port N]
"""
import argparse, os, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
ap.add_argument("--port", type=int, default=27932)
ap.add_argument("--goal", type=int, default=3)
a = ap.parse_args()
godot = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
base = [godot, "--headless", "--path", str(HERE), "--"]
overrides = f"score_goal={a.goal},time_limit=120,respawn_delay=1.5,spawn_protection=6,end_screen_time=3"
deadline = 45  # seconds of server time after which the match smoke fails

def spawn(extra):
    return subprocess.Popen(base + extra + mapargs, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

procs = {"server": spawn(["--role", "server", "--port", str(a.port), "--match-smoke", "--match-set", overrides, "--quit-after", str(deadline)])}
time.sleep(4)  # server loads the map and starts listening
procs["bob"] = spawn(["--role", "client", "--headless-bot", "--match-bot", "bob", "--connect", f"127.0.0.1:{a.port}", "--name", "bob", "--bot-seconds", str(deadline - 3)])
time.sleep(1.5)
procs["alice"] = spawn(["--role", "client", "--headless-bot", "--match-bot", "alice", "--connect", f"127.0.0.1:{a.port}", "--name", "alice", "--bot-seconds", str(deadline - 5)])
out, codes = {}, {}
for k in ("alice", "bob", "server"):
    try:
        out[k], _ = procs[k].communicate(timeout=deadline + 60)
    except subprocess.TimeoutExpired:
        procs[k].kill(); out[k], _ = procs[k].communicate(); out[k] += "\nTIMEOUT"
    codes[k] = procs[k].returncode
ok = True
for k, marker in (("alice", "SHOOTER_MATCH_CLIENT_OK"), ("bob", "SHOOTER_MATCH_CLIENT_OK"), ("server", "SHOOTER_MATCH_SERVER_OK")):
    good = marker in out[k] and codes[k] == 0
    ok &= good
    print(f"--- {k}: {'ok' if good else 'FAIL'} (exit {codes[k]})")
    keep = [l for l in out[k].splitlines() if l.startswith(("client", "server")) or "FAIL" in l or "OK" in l or "rror" in l]
    if os.environ.get("VERBOSE"): keep = out[k].splitlines()
    print("\n".join(keep if os.environ.get("VERBOSE") else keep[-(60 if not good else 8):]))
print("SHOOTER_MATCH_SMOKE_OK" if ok else "SHOOTER_MATCH_SMOKE_FAIL")
sys.exit(0 if ok else 1)

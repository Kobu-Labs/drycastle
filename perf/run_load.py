#!/usr/bin/env python3
"""Server load runs with per-phase tick timing (measurement plan N3, N4, N8, N9, N10).

One headless server (pinned to the server cores, `--perf-out`) with --bots server-side bots, plus --walkers plain walking clients and
--streakers sandbox clients that keep activating killstreaks (--streaks) and spawning vehicles (--vehicles). The run lasts --duration seconds
after the last client joined; the result is the per-phase tick analysis (perf/analyze_ticks.py) of the window after --warm seconds, the server's
own `stats` lines (bytes per client, entities), server RSS/CPU, and per-client RSS.

Usage: run_load.py --tag N8-all-streaks --streakers 6 --streaks uav,attack_missile,turret,care_package,attack_helicopter,helicopter_helper,ac130 --duration 90
"""
import argparse, re, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *
import analyze_ticks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=str(REPO / "out/village/map"))
    ap.add_argument("--tag", required=True)
    ap.add_argument("--bots", type=int, default=0, help="server-side bots (no connection)")
    ap.add_argument("--walkers", type=int, default=0, help="walking headless clients (real connections)")
    ap.add_argument("--streakers", type=int, default=0)
    ap.add_argument("--streaks", default="")
    ap.add_argument("--streak-period", type=float, default=2.0)
    ap.add_argument("--rr", action="store_true", help="give streaker i only streak i mod n (so every owner keeps one instance running)")
    ap.add_argument("--vehicles", default="", help="sandbox SpawnVehicle indices, comma separated")
    ap.add_argument("--duration", type=int, default=90)
    ap.add_argument("--warm", type=int, default=15)
    ap.add_argument("--port", type=int, default=27330)
    ap.add_argument("--net-sim", default="")
    ap.add_argument("--no-cluster", action="store_true")
    ap.add_argument("--vehicle-set", default="", help="server --vehicle-set value (override vehicle counts)")
    ap.add_argument("--extra-server", default="", help="extra server args, space separated")
    a = ap.parse_args()
    d = out_dir(a.tag)
    sc, cc = cpus()
    base = [GODOT, "--headless", "--path", str(SHOOTER), "--"]
    ms = "min_players=1,score_goal=100000,time_limit=100000,min_spawn_distance=5,respawn_delay=3,spawn_protection=3"
    sim = ["--net-sim", a.net_sim] if a.net_sim else []
    slog = open(d / "server.log", "w")
    sargs = ["--role", "server", "--port", str(a.port), "--map", a.map, "--allow-sandbox", "--unlock-all", "--bots", str(a.bots), "--perf-out", str(d / "perf"),
             "--match-set", ms, "--quit-after", str(a.duration + 120)] + ([] if a.no_cluster else ["--cluster-spawns"]) + sim
    if a.vehicle_set:
        sargs += ["--vehicle-set", a.vehicle_set]
    sargs += a.extra_server.split()
    srv = subprocess.Popen(pin(sc) + base + sargs, stdout=slog, stderr=subprocess.STDOUT, text=True)
    if wait_for(d / "server.log", "SHOOTER_NET_LISTENING", 120) is None:
        srv.kill(); raise SystemExit("server did not start")
    ssmp = Sampler(srv.pid); ssmp.start()
    clients = []

    def start_client(name, role, extra):
        f = open(d / f"{name}.log", "w")
        args = ["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{a.port}", "--name", name, "--loss-bot", role,
                "--loss-measure", str(a.duration + 600), "--bot-seconds", str(a.duration + 300), "--map", a.map] + extra + sim
        p = subprocess.Popen(pin(cc) + base + args, stdout=f, stderr=subprocess.STDOUT, text=True)
        clients.append((p, f, name))
    for i in range(a.walkers):
        start_client(f"w{i + 1:02d}", "walker", [])
        time.sleep(0.3)
    for i in range(a.streakers):
        start_client(f"s{i + 1:02d}", "streaker", ["--sandbox", "--perf-streaks", (a.streaks.split(",")[i % len(a.streaks.split(","))] if a.rr else a.streaks), "--perf-streak-period", str(a.streak_period)] + (["--perf-vehicles", a.vehicles] if a.vehicles else []))
        time.sleep(0.3)
    t_join = time.time()
    csmp = [(n, Sampler(p.pid)) for p, _, n in clients]
    for _, s in csmp: s.start()
    time.sleep(a.duration)
    ssmp.stop()
    for _, s in csmp: s.stop()
    for p, f, _ in clients:
        p.terminate()
    srv.terminate()
    try: srv.wait(timeout=20)
    except subprocess.TimeoutExpired: srv.kill()
    for p, f, _ in clients:
        try: p.wait(timeout=10)
        except subprocess.TimeoutExpired: p.kill()
        f.close()
    slog.close()
    ssmp.write_csv(d / "server-proc.csv")
    text = (d / "server.log").read_text(errors="replace")
    stat_lines = [l for l in text.splitlines() if l.startswith("server: stats")]
    res = {"machine": machine(), "args": vars(a), "analysis": analyze_ticks.analyze(analyze_ticks.load(d / "perf" / "server-ticks.csv", a.warm)),
           "server_stats_lines_tail": stat_lines[-6:],
           "server_rss_mb": stats([r["rss_kb"] / 1024 for r in ssmp.rows]), "server_cpu_pct": stats([r["cpu_pct"] for r in ssmp.rows[int(a.warm * 10):]]),
           "client_rss_mb": {n: stats([r["rss_kb"] / 1024 for r in s.rows]) for n, s in csmp}}
    errs = [l for l in text.splitlines() if "ERROR" in l or "failed" in l.lower()][:10]
    res["server_error_lines"] = errs
    write_json(d / "result.json", res)
    analyze_ticks.show(res["analysis"])
    print("stats:", stat_lines[-2:])
    print("server rss", {k: round(v, 1) for k, v in res["server_rss_mb"].items()}, "cpu%", {k: round(v, 1) for k, v in res["server_cpu_pct"].items()})
    print("errors:", errs[:3], "->", d)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""W2 / H-03 / A-03 / C1 / C3: real-UDP loss through the userspace proxy (measurement plan group W).

One server (--perf-out), `--bots` server-side bots (to raise the entity count), `--shooters` shooting clients and `--walkers` walking clients,
all connecting through perf/udp_proxy.py (one-way latency rtt/2 each way, loss on both directions, applied to REAL datagrams: ENet retransmits and
head-of-line blocking are in the numbers, unlike the in-app --net-sim, which never drops reliable packets).

Per client it records (client-<name>.csv, 1 Hz): ticks started/completed (the all-or-nothing tick, H-03), reconciliations, longest gap between
completed ticks, ENet RTT, frame times. Reliable events (Shot/Hit/Kill) are stamped with the wall clock when the server queues them and when each
client receives them; the join gives delivery latency per event per client (A-03, head-of-line blocking).

Usage: run_wire.py --tag W2-loss3 --rtt 100 --loss 0.03 [--bots 0] [--shooters 4] [--walkers 12] [--duration 60]
"""
import argparse, csv, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *


def read_events(path):
    out = {}
    try:
        for line in open(path):
            t, kind, hexs = line.rstrip("\n").split(",", 2)
            out.setdefault((kind, hexs), []).append(int(t))
    except FileNotFoundError:
        pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=str(REPO / "out/village/map"))
    ap.add_argument("--tag", required=True)
    ap.add_argument("--rtt", type=float, default=100)
    ap.add_argument("--jitter", type=float, default=0)
    ap.add_argument("--loss", type=float, default=0.0)
    ap.add_argument("--bots", type=int, default=0)
    ap.add_argument("--shooters", type=int, default=4)
    ap.add_argument("--walkers", type=int, default=12)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--warm", type=int, default=10)
    ap.add_argument("--port", type=int, default=27530)
    a = ap.parse_args()
    d = out_dir(a.tag)
    sc, cc = cpus()
    base = [GODOT, "--headless", "--path", str(SHOOTER), "--"]
    sport, pport = a.port, a.port + 1
    ms = "min_players=1,score_goal=100000,time_limit=100000,min_spawn_distance=5,respawn_delay=2,spawn_protection=1"
    slog = open(d / "server.log", "w")
    srv = subprocess.Popen(pin(sc) + base + ["--role", "server", "--port", str(sport), "--map", a.map, "--cluster-spawns", "--bots", str(a.bots), "--perf-out", str(d / "perf"),
                                              "--match-set", ms, "--quit-after", str(a.duration + 120)], stdout=slog, stderr=subprocess.STDOUT, text=True)
    if wait_for(d / "server.log", "SHOOTER_NET_LISTENING", 120) is None:
        srv.kill(); raise SystemExit("server did not start")
    proxy = subprocess.Popen([sys.executable, "-I", str(Path(__file__).resolve().parent / "udp_proxy.py"), "--listen", str(pport), "--server", f"127.0.0.1:{sport}",
                              "--latency-ms", str(a.rtt / 2), "--jitter-ms", str(a.jitter), "--loss", str(a.loss), "--stats", str(d / "proxy.json")],
                             stdout=open(d / "proxy.log", "w"), stderr=subprocess.STDOUT)
    time.sleep(0.5)
    clients = []
    names = []
    for i in range(a.walkers + a.shooters):
        role = "walker" if i < a.walkers else "shooter"
        name = f"{role[0]}{i + 1:02d}"
        names.append(name)
        f = open(d / f"{name}.log", "w")
        p = subprocess.Popen(pin(cc) + base + ["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{pport}", "--name", name, "--loss-bot", role, "--loss-measure", str(a.duration + 600),
                                               "--bot-seconds", str(a.duration + 300), "--map", a.map, "--perf-out", str(d / "perf")], stdout=f, stderr=subprocess.STDOUT, text=True)
        clients.append((p, f))
        time.sleep(0.3)
    time.sleep(a.duration)
    for p, f in clients:
        p.terminate()
    time.sleep(1.5)
    srv.terminate()
    proxy.terminate()
    for p, f in clients:
        try: p.wait(timeout=10)
        except subprocess.TimeoutExpired: p.kill()
        f.close()
    try: srv.wait(timeout=20)
    except subprocess.TimeoutExpired: srv.kill()
    slog.close()

    # --- reliable events: delivery latency per (event, client), minus the proxy's base one-way delay
    sev = read_events(d / "perf" / "events-server.csv")
    base_ms = a.rtt / 2
    lat, sent_total, missing = [], 0, 0
    t_first = min((min(v) for v in sev.values()), default=0) + a.warm * 1_000_000
    for n in names:
        cev = read_events(d / "perf" / f"events-{n}.csv")
        for key, sts in sev.items():
            for k, st in enumerate(sts):
                if st < t_first:
                    continue
                sent_total += 1
                cts = cev.get(key)
                if not cts or k >= len(cts):
                    missing += 1
                    continue
                lat.append((cts[k] - st) / 1000.0 - base_ms)
    # --- per-client 1 Hz logs
    started = completed = recon = expected = 0
    gaps, rtts, ticks_secs = [], [], 0
    for n in names:
        try:
            rows = list(csv.DictReader(open(d / "perf" / f"client-{n}.csv")))[a.warm:]
        except FileNotFoundError:
            continue
        for r in rows:
            started += int(r["ticks_started"]); completed += int(r["ticks_completed"]); recon += int(r["reconciles"])
            gaps.append(float(r["max_snapshot_gap_ms"])); rtts.append(float(r["rtt_ms"])); ticks_secs += 1
        if rows:
            expected += int(rows[-1]["tick"]) - int(rows[0]["tick"])
    slines = [l for l in (d / "server.log").read_text(errors="replace").splitlines() if l.startswith("server: stats")]
    res = {"machine": machine(), "args": vars(a),
           "reliable_event_extra_latency_ms": stats(lat), "reliable_events_expected": sent_total, "reliable_events_missing": missing,
           "ticks_started": started, "ticks_completed": completed, "tick_completion_pct_of_started": 100.0 * completed / started if started else None, "ticks_expected": expected, "tick_completion_pct_of_expected": 100.0 * completed / expected if expected else None,
           "client_seconds": ticks_secs, "snapshots_completed_per_client_s": completed / ticks_secs if ticks_secs else None, "reconciles_per_client_s": recon / ticks_secs if ticks_secs else None,
           "max_gap_ms_per_second": stats(gaps), "enet_rtt_ms": stats(rtts), "server_stats_tail": slines[-2:], "proxy": __import__("json").load(open(d / "proxy.json")) if (d / "proxy.json").exists() else None}
    write_json(d / "result.json", res)
    print(a.tag, "events", sent_total, "missing", missing, "extra latency ms", {k: round(v, 1) for k, v in stats(lat).items()})
    print(f'  ticks expected {expected} started {started} completed {completed} ({res["tick_completion_pct_of_expected"]}% of expected) ; completed/s/client {res["snapshots_completed_per_client_s"]}; reconciles/s/client {res["reconciles_per_client_s"]}')
    print(f'  max gap/s {res["max_gap_ms_per_second"]} ; rtt {res["enet_rtt_ms"]}')
    print("  ", slines[-1:] , "->", d)


if __name__ == "__main__":
    main()

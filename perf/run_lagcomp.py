#!/usr/bin/env python3
"""B1: what the missing lag compensation costs (measurement plan B1/B3), and the A/B for the rewind of ADR 0013.

Server with --perf-out --perf-lagcomp; N walker bots patrol the cluster; M `aimer` bots stand still and shoot the nearest remote where THEIR screen
shows it (interpolated position), i.e. a perfect-aim human. Every hitscan shot is logged to lagcomp.csv with hit_used (what the server did: rewound,
or present with --no-lagcomp), hit_now (present positions) and hit_rewound (the poses the shooter's screen showed). Latency is added by the userspace
UDP proxy (real datagrams, both directions).

Usage: run_lagcomp.py [--rtts 0,50,100,200] [--walkers 6] [--aimers 2] [--duration 90] [--tag B1] [--no-lagcomp]
"""
import argparse, csv, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *


def run(a, rtt, d):
    sc, cc = cpus()
    base = [GODOT, "--headless", "--path", str(SHOOTER), "--"]
    sport, pport = a.port, a.port + 1
    ms = "min_players=1,score_goal=100000,time_limit=100000,min_spawn_distance=5,respawn_delay=2,spawn_protection=0"
    slog = open(d / "server.log", "w")
    srv = subprocess.Popen(pin(sc) + base + ["--role", "server", "--port", str(sport), "--map", a.map, "--cluster-spawns", "--perf-out", str(d / "perf"), "--perf-lagcomp",
                                              "--match-set", ms, "--quit-after", str(a.duration + 90)] + (["--no-lagcomp"] if a.no_lagcomp else []), stdout=slog, stderr=subprocess.STDOUT, text=True)
    if wait_for(d / "server.log", "SHOOTER_NET_LISTENING", 120) is None:
        srv.kill(); raise SystemExit("server did not start")
    proxy = None
    target = sport
    if rtt > 0:
        proxy = subprocess.Popen([sys.executable, "-I", str(Path(__file__).resolve().parent / "udp_proxy.py"), "--listen", str(pport), "--server", f"127.0.0.1:{sport}",
                                  "--latency-ms", str(rtt / 2), "--jitter-ms", str(a.jitter), "--stats", str(d / "proxy.json")], stdout=open(d / "proxy.log", "w"), stderr=subprocess.STDOUT)
        target = pport
        time.sleep(0.5)
    clients = []
    for i in range(a.walkers + a.aimers):
        role = "walker" if i < a.walkers else "aimer"
        name = f"{role[0]}{i + 1:02d}"
        f = open(d / f"{name}.log", "w")
        p = subprocess.Popen(pin(cc) + base + ["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{target}", "--name", name, "--loss-bot", role, "--loss-measure", str(a.duration + 600),
                                               "--bot-seconds", str(a.duration + 300), "--map", a.map], stdout=f, stderr=subprocess.STDOUT, text=True)
        clients.append((p, f))
        time.sleep(0.3)
    time.sleep(a.duration)
    for p, f in clients:
        p.terminate()
    srv.terminate()
    if proxy:
        proxy.terminate()
    for p, f in clients:
        try: p.wait(timeout=10)
        except subprocess.TimeoutExpired: p.kill()
        f.close()
    try: srv.wait(timeout=20)
    except subprocess.TimeoutExpired: srv.kill()
    slog.close()
    rows = list(csv.DictReader(open(d / "perf" / "lagcomp.csv")))
    return rows


pc = lambda xs, p: xs[min(len(xs) - 1, int(p / 100 * len(xs)))] if xs else 0


def summarise(rows, rtt):
    ok = rows
    shots = len(ok)
    used_hit = sum(1 for r in ok if r["hit_used"] != "0")  # what the server did: the real hit rate
    now_hit = sum(1 for r in ok if r["hit_now"] != "0")
    resolve = sorted(float(r["resolve_us"]) for r in ok)
    rew_hit = sum(1 for r in ok if r["hit_rewound"] != "0")
    both = sum(1 for r in ok if r["hit_now"] != "0" and r["hit_rewound"] != "0")
    rtts = sorted(float(r["rtt_ms"]) for r in ok)
    speeds = sorted(float(x) for r in ok for x in r["speeds_mps"].split("|") if x)
    lat = [(float(r["lateral_rewound_m"]), float(r["lateral_now_m"]), float(r["aimed_dist_m"])) for r in ok if r.get("aimed_id", "0") != "0" and float(r["lateral_now_m"]) < 1e6 and float(r["lateral_rewound_m"]) < 1e6]
    # shots that are really aimed: the ray passes within 1.5 m of the rewound target (where the shooter saw it)
    aimed = [x for x in lat if x[0] < 1.5]
    shift = sorted(abs(x[1] - x[0]) for x in aimed)
    lr = sorted(x[0] for x in aimed)
    ln = sorted(x[1] for x in aimed)
    return {"aimed_shots": len(aimed), "lateral_rewound_m_p50": pc(lr, 50), "lateral_now_m_p50": pc(ln, 50), "lateral_now_m_p90": pc(ln, 90), "lateral_now_minus_rewound_m_p50": pc(shift, 50),
            "now_within_0p3m_pct": 100.0 * sum(1 for x in aimed if x[1] < 0.3) / len(aimed) if aimed else None, "rewound_within_0p3m_pct": 100.0 * sum(1 for x in aimed if x[0] < 0.3) / len(aimed) if aimed else None,
            "now_within_0p56m_pct": 100.0 * sum(1 for x in aimed if x[1] < 0.56) / len(aimed) if aimed else None, "rewound_within_0p56m_pct": 100.0 * sum(1 for x in aimed if x[0] < 0.56) / len(aimed) if aimed else None,
            "target_rtt_ms": rtt, "shots_logged": len(rows), "shots_with_history": shots, "measured_rtt_ms_p50": pc(rtts, 50), "back_ticks_p50": pc(sorted(int(r["back_ticks"]) for r in ok), 50),
            "hit_rate_used": used_hit / shots if shots else None, "hit_rate_now": now_hit / shots if shots else None, "hit_rate_rewound": rew_hit / shots if shots else None, "both": both,
            "resolve_us_p50": pc(resolve, 50), "resolve_us_p90": pc(resolve, 90), "resolve_us_max": resolve[-1] if resolve else None,
            "only_rewound": rew_hit - both, "only_now": now_hit - both, "target_speed_mps_p50": pc(speeds, 50), "target_speed_mps_p90": pc(speeds, 90)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=str(REPO / "out/village/map"))
    ap.add_argument("--rtts", default="0,50,100,200")
    ap.add_argument("--walkers", type=int, default=6)
    ap.add_argument("--aimers", type=int, default=2)
    ap.add_argument("--duration", type=int, default=90)
    ap.add_argument("--jitter", type=float, default=0.0)
    ap.add_argument("--port", type=int, default=27430)
    ap.add_argument("--tag", default="B1-lagcomp")
    ap.add_argument("--no-lagcomp", action="store_true", help="server with rewind off (A/B baseline): hit_used is then the present resolution")
    a = ap.parse_args()
    out = []
    for rtt in [int(x) for x in a.rtts.split(",")]:
        d = out_dir(f"{a.tag}-rtt{rtt}")
        rows = run(a, rtt, d)
        s = summarise(rows, rtt)
        write_json(d / "result.json", s)
        out.append(s)
        print(s, flush=True)
        a.port += 2
    write_json(out_dir(a.tag) / "summary.json", {"machine": machine(), "rows": out})


if __name__ == "__main__":
    main()

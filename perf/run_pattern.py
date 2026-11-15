#!/usr/bin/env python3
"""C1 / C-02: what makes the client's prediction disagree with the server? One movement behaviour per run (idle, straight, jumper, turner),
`--clients` copies of it on a direct link (no proxy), reconcile rate and prediction-error statistics from the client perf logs.

Usage: run_pattern.py --patterns idle,straight,jumper,turner --clients 1 --duration 40
"""
import argparse, csv, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *


def run(role, a, port):
    d = out_dir(f"{a.tag}-{role}-x{a.clients}")
    sc, cc = cpus()
    base = [GODOT, "--headless", "--path", str(SHOOTER), "--"]
    ms = "min_players=1,score_goal=100000,time_limit=100000,min_spawn_distance=5,respawn_delay=2,spawn_protection=1"
    slog = open(d / "server.log", "w")
    srv = subprocess.Popen(pin(sc) + base + ["--role", "server", "--port", str(port), "--map", a.map, "--cluster-spawns", "--match-set", ms, "--quit-after", str(a.duration + 60)] + a.server_args.split(), stdout=slog, stderr=subprocess.STDOUT, text=True)
    if wait_for(d / "server.log", "SHOOTER_NET_LISTENING", 120) is None:
        srv.kill(); raise SystemExit("server did not start")
    cl = []
    for i in range(a.clients):
        n = f"{role[0]}{i + 1:02d}"
        f = open(d / f"{n}.log", "w")
        cl.append((subprocess.Popen(pin(cc) + base + ["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{port}", "--name", n, "--loss-bot", role, "--loss-measure", str(a.duration + 600),
                                                       "--bot-seconds", str(a.duration + 300), "--map", a.map, "--perf-out", str(d / "perf")] + a.client_args.split(), stdout=f, stderr=subprocess.STDOUT, text=True), f, n))
        time.sleep(0.3)
    time.sleep(a.duration)
    for p, f, _ in cl: p.terminate()
    time.sleep(1)
    srv.terminate()
    for p, f, _ in cl:
        try: p.wait(timeout=10)
        except subprocess.TimeoutExpired: p.kill()
        f.close()
    try: srv.wait(timeout=20)
    except subprocess.TimeoutExpired: srv.kill()
    slog.close()
    recon = secs = n_err = 0
    err_mean, err_max, dx, dy, dz = [], [], [], [], []
    for _, _, n in cl:
        try: rows = list(csv.DictReader(open(d / "perf" / f"client-{n}.csv")))[a.warm:]
        except FileNotFoundError: continue
        for r in rows:
            recon += int(r["reconciles"]); secs += 1; k = int(r["err_n"]); n_err += k
            if k:
                err_mean.append(float(r["pred_err_mean_m"])); err_max.append(float(r["pred_err_max_m"])); dx.append(float(r["err_dx_mean_m"])); dy.append(float(r["err_dy_mean_m"])); dz.append(float(r["err_dz_mean_m"]))
    res = {"role": role, "clients": a.clients, "client_seconds": secs, "reconciles_per_s": recon / secs if secs else None, "acked_comparisons_per_s": n_err / secs if secs else None,
           "pred_err_mean_m": stats(err_mean), "pred_err_max_per_s_m": stats(err_max), "signed_dx_m": stats(dx), "signed_dy_m": stats(dy), "signed_dz_m": stats(dz)}
    write_json(d / "result.json", res)
    print(role, "x", a.clients, "reconciles/s", round(res["reconciles_per_s"] or 0, 2), "comparisons/s", round(res["acked_comparisons_per_s"] or 0, 1), "err mean p50/p95",
          round(res["pred_err_mean_m"].get("p50", 0), 4), round(res["pred_err_mean_m"].get("p95", 0), 4), "max p95", round(res["pred_err_max_per_s_m"].get("p95", 0), 3),
          "signed dx/dy/dz mean", round(res["signed_dx_m"].get("mean", 0), 4), round(res["signed_dy_m"].get("mean", 0), 4), round(res["signed_dz_m"].get("mean", 0), 4), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=str(REPO / "out/village/map"))
    ap.add_argument("--patterns", default="idle,straight,jumper,turner")
    ap.add_argument("--clients", type=int, default=1)
    ap.add_argument("--duration", type=int, default=40)
    ap.add_argument("--warm", type=int, default=8)
    ap.add_argument("--tag", default="C1-pattern")
    ap.add_argument("--port", type=int, default=28130)
    ap.add_argument("--server-args", default="")
    ap.add_argument("--client-args", default="")
    a = ap.parse_args()
    for i, r in enumerate(a.patterns.split(",")):
        run(r, a, a.port + i)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""L1 + N1: dedicated server start-up time, memory and idle CPU on a map (measurement plan, groups L and N).

For each run: start `godot --headless ... --role server` pinned to the server cores, sample RSS and CPU every 100 ms, record the time from process
start to SHOOTER_NET_LISTENING, the `map load phases` line, and then let it idle for --idle seconds (CPU % with no client, N1). Several runs; the
first after a build is "cold" for the file cache, later ones warm.

Usage: l1_server_boot.py --map out/village/map [--runs 5] [--idle 30] [--port 27115] [--perf-phases]
"""
import argparse, re, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *


def one(run, a, d):
    log = d / f"server-{run}.log"
    sc, _ = cpus()
    args = [GODOT, "--headless", "--path", str(SHOOTER), "--", "--role", "server", "--port", str(a.port), "--map", str(Path(a.map).resolve()),
            "--quit-after", str(a.idle + 600)]
    if a.perf_phases:
        args += ["--perf-out", str(d / f"perf-{run}")]
    f = open(log, "w")
    t0 = time.time()
    p = subprocess.Popen(pin(sc) + args, stdout=f, stderr=subprocess.STDOUT, text=True)
    # the pinned launcher may be taskset itself; the Godot pid is its child (or the same process after exec)
    time.sleep(0.2)
    pid = p.pid
    smp = Sampler(pid)
    smp.start()
    ready = wait_for(log, "SHOOTER_NET_LISTENING", 300)
    t_ready = time.time() - t0
    cpu_ready = smp.rows[-1]["cpu_s"] if smp.rows else 0.0
    rss_ready = smp.rows[-1]["rss_kb"] if smp.rows else 0
    time.sleep(a.idle)
    idle_rows = [r for r in smp.rows if r["t"] > t_ready + 3]
    smp.stop()
    p.terminate()
    try:
        p.wait(timeout=15)
    except subprocess.TimeoutExpired:
        p.kill()
    f.close()
    smp.write_csv(d / f"proc-{run}.csv")
    text = log.read_text(errors="replace")
    ph = re.search(r"map load phases: chunks ([\d.]+)s, trees ([\d.]+)s, objects ([\d.]+)s, clutter ([\d.]+)s, boundary ([\d.]+)s", text)
    return {
        "run": run, "listening_s": ready and t_ready, "wall_to_ready_s": t_ready,
        "cpu_s_to_ready": cpu_ready, "rss_mb_at_ready": rss_ready / 1024, "hwm_mb_peak": max((r["hwm_kb"] for r in smp.rows), default=0) / 1024,
        "rss_mb_end": (smp.rows[-1]["rss_kb"] if smp.rows else 0) / 1024, "threads_end": smp.rows[-1]["threads"] if smp.rows else 0,
        "map_load_phases_s": dict(zip(["chunks", "trees", "objects", "clutter", "boundary"], map(float, ph.groups()))) if ph else None,
        "idle_cpu_pct": stats([r["cpu_pct"] for r in idle_rows]), "idle_seconds": a.idle,
        "server_lines": [l for l in text.splitlines() if l.startswith("server:") and ("spawns" in l or "stats" in l or "perf" in l or "map" in l.lower())][:12],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default="out/village/map")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--idle", type=int, default=30)
    ap.add_argument("--port", type=int, default=27115)
    ap.add_argument("--perf-phases", action="store_true")
    ap.add_argument("--tag", default="")
    a = ap.parse_args()
    d = out_dir("L1-server-boot" + (("-" + a.tag) if a.tag else ""))
    res = [one(i, a, d) for i in range(a.runs)]
    summary = {"machine": machine(), "map": str(a.map), "runs": res,
               "ready_s": stats([r["wall_to_ready_s"] for r in res]), "rss_ready_mb": stats([r["rss_mb_at_ready"] for r in res]),
               "hwm_mb": stats([r["hwm_mb_peak"] for r in res]), "idle_cpu_pct_all": stats([r["idle_cpu_pct"].get("mean", 0) for r in res])}
    write_json(d / "result.json", summary)
    print(f"ready in {summary['ready_s']}\nRSS at ready {summary['rss_ready_mb']}\npeak {summary['hwm_mb']}\nidle CPU % {summary['idle_cpu_pct_all']}\n-> {d}")


if __name__ == "__main__":
    main()

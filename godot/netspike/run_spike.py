#!/usr/bin/env python3
"""Harness for the 64-client headless netcode spike (stdlib only).

Launches one headless Godot server and N headless bot clients (one process each) per
variant, collects the JSON/binary output the processes write and computes the metrics.

  python3 godot/netspike/run_spike.py                # full: N=16,32,64, 60 s, all variants + probe
  python3 godot/netspike/run_spike.py --quick        # N=8, 15 s
  python3 godot/netspike/run_spike.py --ns 16 --seconds 20 --variants builtin,custom --skip-probe

Map folder: --map DIR or DRYCASTLE_MAP. Output: out/netspike/<timestamp>/ (gitignored).
"""
import argparse
import bisect
import json
import math
import os
import platform
import shutil
import statistics
import struct
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
PORT_BASE = 27100

# variant name -> (mode, interest radius m)
VARIANTS = {
    "builtin": ("builtin", 0),
    "custom": ("custom", 0),
    "builtin-interest": ("builtin", 50),
    "custom-interest": ("custom", 50),
}


def pct(xs, p):
    if not xs:
        return None
    xs = sorted(xs)
    return xs[min(len(xs) - 1, max(0, math.ceil(p / 100 * len(xs)) - 1))]


def summ(xs):
    if not xs:
        return {"count": 0}
    return {"count": len(xs), "mean": statistics.fmean(xs), "p50": pct(xs, 50), "p95": pct(xs, 95),
            "p99": pct(xs, 99), "max": max(xs)}


def godot_cmd(*args):
    return ["godot", "--headless", "--path", str(HERE), "--", *args]


def cpu_sets():
    cpus = sorted(os.sched_getaffinity(0))
    if shutil.which("taskset") and len(cpus) >= 8:
        s = max(2, len(cpus) // 4)
        return cpus[:s], cpus[s:]
    return None, None


def prefix(cpus):
    if cpus and shutil.which("taskset"):
        return ["taskset", "-c", ",".join(map(str, cpus))]
    return []


def read_history(path):
    data = Path(path).read_bytes()
    n_ticks, n_ids = struct.unpack_from("<ii", data, 0)
    off = 8
    ids = list(struct.unpack_from(f"<{n_ids}i", data, off))
    off += 4 * n_ids
    rec = struct.Struct(f"<id{3 * n_ids}f")
    ticks, stamps, pos = [], [], []
    for _ in range(n_ticks):
        v = rec.unpack_from(data, off)
        off += rec.size
        ticks.append(v[0])
        stamps.append(v[1])
        pos.append(v[2:])
    return ids, ticks, stamps, pos


def analyse_samples(outdir, ids, ticks, stamps, pos):
    idx_of = {i: k for k, i in enumerate(ids)}
    tick_row = {t: r for r, t in enumerate(ticks)}
    rec = struct.Struct("<iifffd")
    age, div, stale, unknown = [], [], [], 0
    total = 0
    for f in sorted(Path(outdir).glob("client_*.bin")):
        data = f.read_bytes()
        for tick, eid, x, y, z, recv in rec.iter_unpack(data[: len(data) // rec.size * rec.size]):
            total += 1
            r = tick_row.get(tick)
            k = idx_of.get(eid)
            if r is None or k is None:
                unknown += 1
                continue
            sx, sy, sz = pos[r][3 * k: 3 * k + 3]
            if sx != sx:  # NaN: entity not present on server at that tick
                unknown += 1
                continue
            age.append((recv - stamps[r]) * 1000.0)
            div.append(((x - sx) ** 2 + (y - sy) ** 2 + (z - sz) ** 2) ** 0.5)
            # staleness: client's last known position vs where the server entity is at receive time
            r2 = bisect.bisect_right(stamps, recv) - 1
            if r2 >= 0:
                tx, ty, tz = pos[r2][3 * k: 3 * k + 3]
                if tx == tx:
                    stale.append(((x - tx) ** 2 + (y - ty) ** 2 + (z - tz) ** 2) ** 0.5)
    return {"samples_total": total, "samples_unmatched": unknown, "age_ms": summ(age),
            "divergence_at_tick_m": summ(div), "staleness_at_receive_m": summ(stale)}


def run_variant(args, outroot, name, n, port):
    mode, interest = VARIANTS[name]
    outdir = outroot / f"{name}_n{n}"
    outdir.mkdir(parents=True, exist_ok=True)
    srv_cpus, cli_cpus = cpu_sets()
    common = ["--mode", mode, "--port", str(port), "--out", str(outdir), "--interest", str(interest),
              "--seed", str(args.seed)]
    if args.map:
        common += ["--map", args.map]
    server_log = open(outdir / "server.log", "w")
    env = dict(os.environ)
    srv = subprocess.Popen(prefix(srv_cpus) + godot_cmd("--role", "server", "--n", str(n), "--seconds", str(args.seconds),
                                                       "--warmup", str(args.warmup), "--join-timeout", "90", *common),
                           stdout=server_log, stderr=subprocess.STDOUT, env=env)
    # wait for the server to be ready
    t0 = time.time()
    while time.time() - t0 < 120:
        if srv.poll() is not None:
            raise RuntimeError(f"server exited early ({srv.returncode}); see {outdir / 'server.log'}")
        if "NETSPIKE_READY" in (outdir / "server.log").read_text(errors="ignore"):
            break
        time.sleep(0.2)
    else:
        srv.kill()
        raise RuntimeError("server not ready in 120 s")

    clients = []
    for i in range(n):
        log = open(outdir / f"client_{i}.log", "w")
        cmd = prefix(cli_cpus) + godot_cmd("--role", "client", "--index", str(i), "--connect-timeout", "60", *common)
        clients.append((subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env), log))
        time.sleep(args.stagger)

    limit = 90 + args.warmup + args.seconds + 60
    try:
        srv.wait(timeout=limit + n * args.stagger)
    except subprocess.TimeoutExpired:
        srv.kill()
    server_log.close()
    deadline = time.time() + 20
    for p, log in clients:
        try:
            p.wait(timeout=max(0.1, deadline - time.time()))
        except subprocess.TimeoutExpired:
            p.kill()
        log.close()

    res = {"variant": name, "mode": mode, "interest_m": interest, "n": n, "seconds": args.seconds}
    sj = outdir / "server.json"
    if not sj.exists():
        res["error"] = "server wrote no results"
        return res
    server = json.loads(sj.read_text())
    res["server"] = server
    cj = []
    for i in range(n):
        f = outdir / f"client_{i}.json"
        if f.exists():
            cj.append(json.loads(f.read_text()))
    connected = [c for c in cj if c.get("connected")]
    res["clients"] = {
        "launched": n, "result_files": len(cj), "connected": len(connected),
        "failed_or_missing": n - len(connected),
        "early_disconnects": sum(1 for c in cj if "early" in c.get("note", "")),
        "notes": sorted({c.get("note", "") for c in cj if c.get("note")}),
        "connect_ms": summ([c["connect_ms"] for c in connected]),
        "down_kBps_per_client": summ([c["window"]["recv_Bps"] / 1000 for c in connected]),
        "up_kBps_per_client": summ([c["window"]["sent_Bps"] / 1000 for c in connected]),
        "cpu_pct_per_client": summ([c["window"]["cpu_pct"] for c in connected]),
        "peak_rss_mb_per_client": summ([c["window"]["peak_rss_mb"] for c in connected]),
        "avg_visible_entities": summ([c["avg_visible_entities"] for c in connected]),
        "updates_per_client_per_s": summ([c["updates"] / max(c["window"]["window_s"], 1e-9) for c in connected]),
    }
    w = server["window"]
    res["server_bandwidth"] = {"up_kBps_total": w["sent_Bps"] / 1000, "down_kBps_total": w["recv_Bps"] / 1000,
                               "up_kBps_per_client": w["sent_Bps"] / 1000 / max(server["players_at_go"], 1),
                               "down_kBps_per_client": w["recv_Bps"] / 1000 / max(server["players_at_go"], 1)}
    hp = outdir / "history.bin"
    if hp.exists():
        res["sync"] = analyse_samples(outdir, *read_history(hp))
    (outdir / "result.json").write_text(json.dumps(res, indent=2))
    return res


def run_probe(args, outroot, k, syncs, mode, port, change_from=0):
    outdir = outroot / "probe"
    outdir.mkdir(parents=True, exist_ok=True)
    common = ["--port", str(port), "--out", str(outdir), "--k", str(k), "--syncs", str(syncs), "--sync-mode", mode,
              "--seconds", "6", "--change-from", str(change_from)]
    log = open(outdir / f"probe_k{k}_s{syncs}_{mode}_c{change_from}.log", "w")
    srv = subprocess.Popen(godot_cmd("--role", "probe-server", *common), stdout=log, stderr=subprocess.STDOUT)
    time.sleep(2.0)
    cli = subprocess.Popen(godot_cmd("--role", "probe-client", *common), stdout=log, stderr=subprocess.STDOUT)
    try:
        cli.wait(timeout=60)
    except subprocess.TimeoutExpired:
        cli.kill()
    try:
        srv.wait(timeout=20)
    except subprocess.TimeoutExpired:
        srv.kill()
    log.close()
    f = outdir / f"probe_k{k}_s{syncs}_{mode}_c{change_from}.json"
    if f.exists():
        return json.loads(f.read_text())
    return {"k": k, "syncs": syncs, "sync_mode": mode, "error": "no result"}


def fmt(v, d=2):
    return "-" if v is None else f"{v:.{d}f}"


def print_table(results):
    print("\nvariant            N  conn  tick_hz  busy p50/p95/p99 ms   srv_cpu%  up_total  down_total  "
          "client_down kB/s  age_p50/p95 ms  div_p95 m  stale_p95 m")
    for r in results:
        if "server" not in r:
            print(f"{r['variant']:<18} {r['n']:>3}  ERROR {r.get('error')}")
            continue
        s, c, b, y = r["server"], r["clients"], r["server_bandwidth"], r.get("sync", {})
        busy = s["frame_busy_ms"]
        print(f"{r['variant']:<18} {r['n']:>3} {c['connected']:>5} {s['achieved_tick_hz']:>8.2f}  "
              f"{fmt(busy['p50'])}/{fmt(busy['p95'])}/{fmt(busy['p99'])}  {s['window']['cpu_pct']:>8.1f}  "
              f"{b['up_kBps_total']:>8.1f}  {b['down_kBps_total']:>10.1f}  {fmt(c['down_kBps_per_client'].get('mean'), 1):>16}  "
              f"{fmt(y.get('age_ms', {}).get('p50'), 1)}/{fmt(y.get('age_ms', {}).get('p95'), 1)}  "
              f"{fmt(y.get('divergence_at_tick_m', {}).get('p95'), 4)}  {fmt(y.get('staleness_at_receive_m', {}).get('p95'), 3)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--quick", action="store_true", help="N=8, 15 s, builtin and custom, short probe")
    ap.add_argument("--ns", default="16,32,64")
    ap.add_argument("--seconds", type=int, default=60)
    ap.add_argument("--warmup", type=int, default=4)
    ap.add_argument("--variants", default=",".join(VARIANTS))
    ap.add_argument("--skip-probe", action="store_true")
    ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
    ap.add_argument("--out", default="")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--stagger", type=float, default=0.05, help="seconds between client launches")
    args = ap.parse_args()
    if args.quick:
        args.ns, args.seconds, args.warmup = "8", 15, 3
        args.variants = "builtin,custom,builtin-interest,custom-interest"
    if not args.map:
        sys.exit("no map: set DRYCASTLE_MAP or pass --map DIR (a folder produced by `make export`)")
    args.map = str(Path(args.map).resolve())

    outroot = Path(args.out) if args.out else REPO / "out" / "netspike" / time.strftime("%Y%m%d-%H%M%S")
    outroot.mkdir(parents=True, exist_ok=True)
    subprocess.run(["dotnet", "build", str(HERE / "netspike.csproj"), "-v", "q", "-nologo"], check=True)
    if not (HERE / ".godot").exists():
        subprocess.run(["godot", "--headless", "--path", str(HERE), "--import"], check=False,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    ver = subprocess.run(["godot", "--version"], capture_output=True, text=True).stdout.strip()
    env = {"godot": ver, "cpus": os.cpu_count(), "platform": platform.platform(), "args": vars(args)}
    env["args"] = {k: v for k, v in env["args"].items() if k != "map"}
    (outroot / "env.json").write_text(json.dumps(env, indent=2))
    print("output:", outroot)

    results = []
    port = PORT_BASE
    for n in [int(x) for x in args.ns.split(",")]:
        for v in [x for x in args.variants.split(",") if x]:
            port += 1
            print(f"--- {v} N={n} ({args.seconds} s)", flush=True)
            try:
                r = run_variant(args, outroot, v, n, port)
            except Exception as e:  # keep going, record the failure
                r = {"variant": v, "n": n, "error": str(e)}
            results.append(r)
            time.sleep(2)
    print_table(results)

    probes = []
    if not args.skip_probe:
        cases = [(64, 1, "always"), (65, 1, "onchange", 64)] if args.quick else \
            [(32, 1, "always"), (63, 1, "always"), (64, 1, "always"), (65, 1, "always"), (80, 1, "always"),
             (128, 1, "always"), (128, 2, "always"), (128, 4, "always"), (65, 1, "onchange"), (128, 1, "onchange"), (128, 1, "onchange", 64),
             (128, 1, "always", 64), (65, 1, "onchange", 64), (64, 1, "onchange", 63), (128, 1, "onchange", 127)]
        for k, s, m, *cf in cases:
            port += 1
            cf = cf[0] if cf else 0
            print(f"--- probe K={k} syncs={s} mode={m} change_from={cf}", flush=True)
            p = run_probe(args, outroot, k, s, m, port, cf)
            probes.append(p)
            print("   ", p)
    (outroot / "summary.json").write_text(json.dumps({"env": env, "runs": results, "probe": probes}, indent=2))
    print("summary:", outroot / "summary.json")


if __name__ == "__main__":
    main()

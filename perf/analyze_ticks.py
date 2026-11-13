#!/usr/bin/env python3
"""Per-phase stats of a server-ticks.csv written by --perf-out (measurement plan N5/N6).

Usage: analyze_ticks.py FILE.csv [--skip-s 5] [--json out.json]
Prints, per phase, mean / p50 / p95 / p99 / max in microseconds and its share of the mean tick, then allocation, GC and ray/LiveTargets rates.
Ticks before --skip-s seconds (30 ticks per second) are dropped as warm-up.
"""
import argparse, csv, json, math
from pathlib import Path


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, max(0, math.ceil(p / 100 * len(xs)) - 1))] if xs else 0.0


def load(path, skip_s=5):
    rows = list(csv.DictReader(open(path)))
    return rows[int(skip_s * 30):]


def analyze(rows):
    if not rows:
        return {"ticks": 0}
    phases = [k for k in rows[0] if k.endswith("_us") and k != "total_us"]
    secs = len(rows) / 30.0
    out = {"ticks": len(rows), "seconds": secs, "total_us": {}, "phases": {}, "rates_per_s": {}}
    tot = [float(r["total_us"]) for r in rows]
    mean_tot = sum(tot) / len(tot)
    out["total_us"] = {"mean": mean_tot, "p50": pct(tot, 50), "p95": pct(tot, 95), "p99": pct(tot, 99), "max": max(tot)}
    for ph in phases:
        v = [float(r[ph]) for r in rows]
        out["phases"][ph[:-3]] = {"mean": sum(v) / len(v), "p50": pct(v, 50), "p95": pct(v, 95), "p99": pct(v, 99), "max": max(v), "share_pct": 100 * (sum(v) / len(v)) / mean_tot if mean_tot else 0}
    for k in ("alloc_bytes", "gc0", "gc1", "gc2", "rays", "live_targets_calls", "live_targets_elems"):
        out["rates_per_s"][k] = sum(float(r[k]) for r in rows) / secs
    for k in ("players", "bots", "cars_moving", "cars_total", "entities"):
        v = [float(r[k]) for r in rows]
        out[k] = {"mean": sum(v) / len(v), "max": max(v)}
    allocs = [float(r["alloc_bytes"]) for r in rows]
    out["alloc_bytes_per_tick"] = {"p50": pct(allocs, 50), "p99": pct(allocs, 99), "max": max(allocs)}
    return out


def show(a):
    if not a["ticks"]:
        print("no ticks")
        return
    t = a["total_us"]
    print(f'{a["ticks"]} ticks ({a["seconds"]:.0f} s)  tick total us: mean {t["mean"]:.0f} p50 {t["p50"]:.0f} p95 {t["p95"]:.0f} p99 {t["p99"]:.0f} max {t["max"]:.0f}')
    print(f'{"phase":20}{"mean":>9}{"p50":>9}{"p95":>9}{"p99":>9}{"max":>9}{"share%":>8}')
    for k, v in sorted(a["phases"].items(), key=lambda kv: -kv[1]["mean"]):
        print(f'{k:20}{v["mean"]:>9.0f}{v["p50"]:>9.0f}{v["p95"]:>9.0f}{v["p99"]:>9.0f}{v["max"]:>9.0f}{v["share_pct"]:>8.1f}')
    r = a["rates_per_s"]
    print(f'per second: alloc {r["alloc_bytes"] / 1e6:.2f} MB, gen0 {r["gc0"]:.2f} gen1 {r["gc1"]:.2f} gen2 {r["gc2"]:.2f}, rays {r["rays"]:.0f}, LiveTargets calls {r["live_targets_calls"]:.0f} (elems {r["live_targets_elems"]:.0f})')
    print(f'players {a["players"]["mean"]:.1f} bots {a["bots"]["mean"]:.1f} cars {a["cars_total"]["mean"]:.0f} (moving mean {a["cars_moving"]["mean"]:.1f}) entities {a["entities"]["mean"]:.0f}; alloc/tick p50 {a["alloc_bytes_per_tick"]["p50"]:.0f} p99 {a["alloc_bytes_per_tick"]["p99"]:.0f} max {a["alloc_bytes_per_tick"]["max"]:.0f} B')


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--skip-s", type=float, default=5)
    ap.add_argument("--json", default="")
    a = ap.parse_args()
    res = analyze(load(a.csv, a.skip_s))
    show(res)
    if a.json:
        Path(a.json).write_text(json.dumps(res, indent=2))

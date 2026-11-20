#!/usr/bin/env python3
"""Medians of the "client: perf" lines of client load test logs (perf/vps_client_test.sh), one column per log:
    perf/client_summary.py docs/perf-investigation/client-logs/*.log.gz out/perf/client/<new>.log
The first 3 windows of a log (joining, shaders compiling, the first spawn) are left out; --all keeps them."""
import gzip, re, statistics, sys

SKIP = 3
ROWS = ["fps", "frame_p50_ms", "frame_p99_ms", "frame_max_ms", "frames_over_33ms", "frames_over_50ms", "frames_over_100ms", "process_peak_ms", "physics_peak_ms",
        "render_cpu_ms", "render_gpu_ms", "draws", "prims_k", "objects", "nodes", "video_mem_mb", "rss_mb", "gc_heap_mb", "alloc_mb_s",
        "gc_gen0", "gc_gen1", "gc_gen2", "gc_pause_ms", "rtt_ms", "loss_pct", "snaps_per_s", "snap_gap_max_ms", "reconciles", "pred_err_max_m",
        "in_kb_s", "out_kb_s", "remotes", "world_entities"]

EVENT = re.compile(r"([A-Za-z0-9]+)=(-?[0-9.]+)\|([0-9.]+)")
PAIR = re.compile(r"([a-z0-9_]+)=(-?[0-9]+(?:\.[0-9]+)?)")
COUNTS = {}  # log -> events per second by kind, one row per window (beside the ms of the same line)
GPU = {}  # log -> the card's state through the run (the "# gpu:" line of vps_client_test.sh)
def pairs(line): return {k: float(v) for k, v in PAIR.findall(line)}  # the engine's error output can land in the middle of a line

def read(path):
    vals, scopes, events, counts = [], [], [], []
    with (gzip.open(path, "rt") if path.endswith(".gz") else open(path)) as f:
        for line in f:
            if line.startswith("# gpu: "):
                GPU[path] = pairs(line)
            elif line.startswith("client: perf events "):
                events.append({k: float(a) for k, a, _ in EVENT.findall(line)}); counts.append({k: float(n) for k, _, n in EVENT.findall(line)})
            elif line.startswith("client: perf scopes ms_per_s "):
                scopes.append(pairs(line))
            elif line.startswith("client: perf fps="):
                vals.append(pairs(line))
    COUNTS[path] = counts
    return vals, scopes, events

def med(rows, key):
    xs = [r[key] for r in rows if key in r]
    return statistics.median(xs) if xs else None

def fmt(v): return "-" if v is None else f"{v:.2f}" if abs(v) < 100 else f"{v:.0f}"

def main():
    args = [a for a in sys.argv[1:] if a != "--all"]
    skip = 0 if "--all" in sys.argv else SKIP
    if not args: sys.exit(__doc__)
    runs, evs = [], []
    for p in args:
        vals, scopes, events = read(p)
        runs.append((p.split("/")[-1].removesuffix(".gz").removesuffix(".log"), vals[skip:], scopes[skip:]))
        evs.append(events[skip:])
    w = max(24, *(len(n) + 2 for n, _, _ in runs))
    def line(name, cells): print(f"{name:26}" + "".join(f"{c:>{w}}" for c in cells))
    line("", [n for n, _, _ in runs])
    line("windows (10 s)", [str(len(v)) for _, v, _ in runs])
    for r in ROWS: line(r, [fmt(med(v, r)) for _, v, _ in runs])
    line("frame_max_ms, run max", [fmt(max((x["frame_max_ms"] for x in v if "frame_max_ms" in x), default=None)) for _, v, _ in runs])
    if GPU:  # render_gpu_ms of a capped run and of a free one are not comparable
        for k in ["power_w", "clock_mhz", "temp_max_c", "capped_pct"]: line("gpu " + k, [fmt(GPU.get(p, {}).get(k)) for p in args])
    print("scopes, ms per second (10 = 1 % of a core)")
    names = []
    for _, _, s in runs:
        for row in s:
            for k in row:
                if k not in names: names.append(k)
    for k in names: line("  " + k, [fmt(med(s, k)) for _, _, s in runs])
    line("  all scopes", [fmt(sum(med(s, k) or 0 for k in names)) if s else "-" for _, _, s in runs])
    # the frame cap is off, so a faster run does per-frame work more often in a second: per frame is what compares between runs
    print("scopes, ms per frame (ms per second / fps)")
    for k in names: line("  " + k, [fmt(med(s, k) / med(v, "fps")) if s and med(s, k) is not None and med(v, "fps") else "-" for _, v, s in runs])
    line("  all scopes", [fmt(sum(med(s, k) or 0 for k in names) / med(v, "fps")) if s and med(v, "fps") else "-" for _, v, s in runs])
    if any(evs):
        print("events inside net_events, ms per second (mean over the windows; a kind is listed only in windows where it was in the top ten)")
        kinds = {}
        for ev in evs:
            for row in ev:
                for k, v in row.items(): kinds[k] = kinds.get(k, 0) + v
        for k in sorted(kinds, key=kinds.get, reverse=True)[:12]:
            line("  " + k, [fmt(sum(r.get(k, 0) for r in ev) / len(ev)) if ev else "-" for ev in evs])
        print("the same kinds, microseconds per event (a fight with more shots costs more per second: per event is what compares between runs)")
        for k in sorted(kinds, key=kinds.get, reverse=True)[:6]:
            cells = []
            for p, ev in zip(args, evs):
                n = sum(r.get(k, 0) for r in COUNTS.get(p, [])[skip:])
                cells.append(fmt(1000 * sum(r.get(k, 0) for r in ev) / n) + f" ({n / len(ev):.1f}/s)" if ev and n else "-")
            line("  " + k, cells)

if __name__ == "__main__":
    main()


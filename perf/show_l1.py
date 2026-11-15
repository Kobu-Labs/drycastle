#!/usr/bin/env python3
"""Print the L1/N1 result.json of a run directory (default: the newest)."""
import json, sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "out" / "perf"
d = Path(sys.argv[1]) if len(sys.argv) > 1 else sorted(root.glob("*/L1-server-boot*"))[-1]
r = json.loads((d / "result.json").read_text())
m = r["machine"]
print(d, "|", m["cpu"], "| governor", m["governor"], "| load1", m["load1_at_start"], "| godot", m["godot"])
for x in r["runs"]:
    idle = {k: round(v, 1) for k, v in x["idle_cpu_pct"].items() if k in ("mean", "p95", "max")}
    print(f'run {x["run"]}: ready {x["wall_to_ready_s"]:.1f}s  cpu-to-ready {x["cpu_s_to_ready"]:.1f}s  RSS@ready {x["rss_mb_at_ready"]:.0f} MB  peak {x["hwm_mb_peak"]:.0f} MB  threads {x["threads_end"]}  phases {x["map_load_phases_s"]}  idle cpu% {idle}')
for k in ("ready_s", "rss_ready_mb", "hwm_mb", "idle_cpu_pct_all"):
    print(k, {a: round(b, 2) for a, b in r[k].items()})

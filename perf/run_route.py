#!/usr/bin/env python3
"""R1-R7 + L2: graphical client frame measurement on the real map (measurement plan group R).

Starts the client offline (`--role client --offline --perf-route`) in a 1920x1080 window, samples its RSS/CPU, measures the wall time to SHOOTER_READY
(process start -> map loaded -> in the world) and collects route-summary.json / route-frames.csv written by PerfRoute.cs. One variant per run:
  base | msaa0 | msaa2 | msaa4 | msaa8 | noshadow | shadow100 | mobile | compat

Usage: run_route.py --variants base,msaa0,noshadow,mobile [--views 48] [--dwell 1.5] [--gpu nvidia|intel]
GPU choice on this hybrid laptop: --gpu nvidia sets the PRIME offload variables, --gpu intel leaves the iGPU.
"""
import argparse, json, os, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import *

VARIANTS = {
    "base": [],
    "msaa0": ["--perf-msaa", "0"], "msaa2": ["--perf-msaa", "2"], "msaa4": ["--perf-msaa", "4"], "msaa8": ["--perf-msaa", "8"],
    "noshadow": ["--perf-noshadow"], "shadow100": ["--perf-shadow-dist", "100"], "shadow60": ["--perf-shadow-dist", "60"], "shadow40": ["--perf-shadow-dist", "40"],
    "mobile": ["--perf-msaa", "0"], "compat": ["--perf-msaa", "0"],
}
RENDERER = {"mobile": ["--rendering-method", "mobile"], "compat": ["--rendering-method", "gl_compatibility"]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=str(REPO / "out/village/map"))
    ap.add_argument("--variants", default="base")
    ap.add_argument("--views", type=int, default=48)
    ap.add_argument("--dwell", type=float, default=1.5)
    ap.add_argument("--gpu", default="nvidia")
    ap.add_argument("--tag", default="R1-route")
    ap.add_argument("--res", default="1920x1080")
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--gpu-index", type=int, default=-1, help="Godot --gpu-index (pick the adapter: 0/1)")
    ap.add_argument("--repeat", type=int, default=1)
    ap.add_argument("--blasts", type=float, default=0, help="bench: rocket explosions per second around the camera (--perf-blasts)")
    a = ap.parse_args()
    for v in [x for _ in range(a.repeat) for x in a.variants.split(",")]:
        d = out_dir(f"{a.tag}-{a.gpu}{a.gpu_index if a.gpu_index >= 0 else ''}-{v}-{int(time.time()) % 100000}")
        env = dict(os.environ)
        if a.gpu == "nvidia":
            env.update({"__NV_PRIME_RENDER_OFFLOAD": "1", "__GLX_VENDOR_LIBRARY_NAME": "nvidia", "__VK_LAYER_NV_optimus": "NVIDIA_only", "DRI_PRIME": "1"})
        args = [GODOT, "--path", str(SHOOTER), "--resolution", a.res, "--windowed"] + (["--gpu-index", str(a.gpu_index)] if a.gpu_index >= 0 else []) + RENDERER.get(v, []) + ["--", "--role", "client", "--offline", "--map", a.map, "--perf-route", "--perf-out", str(d),
                "--perf-views", str(a.views), "--perf-dwell", str(a.dwell), "--perf-tag", v] + VARIANTS.get(v, []) + (["--perf-blasts", str(a.blasts)] if a.blasts > 0 else [])
        log = open(d / "client.log", "w")
        t0 = time.time()
        p = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, text=True, env=env)
        smp = Sampler(p.pid); smp.start()
        ready = wait_for(d / "client.log", "SHOOTER_READY", 300)
        t_ready = (time.time() - t0) if ready is not None else None
        try:
            p.wait(timeout=a.timeout)
        except subprocess.TimeoutExpired:
            p.kill()
        smp.stop(); log.close()
        smp.write_csv(d / "proc.csv")
        res = {}
        sp = d / "route-summary.json"
        if sp.exists():
            res = json.loads(sp.read_text())
        res.update({"variant": v, "gpu_env": a.gpu, "wall_to_ready_s": t_ready, "rss_mb": stats([r["rss_kb"] / 1024 for r in smp.rows]), "hwm_mb": max((r["hwm_kb"] for r in smp.rows), default=0) / 1024,
                    "machine": machine()})
        write_json(d / "result.json", res)
        fm = res.get("frame_ms", {})
        print(f'{v:10} adapter={res.get("adapter")} renderer={res.get("renderer")} msaa={res.get("msaa")} ready={t_ready and round(t_ready, 1)}s  fps={res.get("fps_mean", 0):.0f}  frame p50/p95/p99/max={fm.get("p50", 0):.1f}/{fm.get("p95", 0):.1f}/{fm.get("p99", 0):.1f}/{fm.get("max", 0):.1f} ms  '
              f'draws p50/p90={res.get("draws", {}).get("p50")}/{res.get("draws", {}).get("p90")}  per-view draws p50/p90={res.get("per_view_mean_draws", {}).get("p50", 0):.0f}/{res.get("per_view_mean_draws", {}).get("p90", 0):.0f}  rss={res.get("hwm_mb", 0):.0f}MB vram={res.get("video_mem_mb", 0):.0f}MB -> {d}', flush=True)


if __name__ == "__main__":
    main()

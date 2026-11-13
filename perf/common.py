"""Shared helpers for the perf-harness runners (docs/perf-investigation/measurement-plan.md).

Everything here is measurement only: it starts the game's own processes, samples /proc, and writes JSON/CSV under out/perf/<UTC>/<experiment>/.
"""
import json, math, os, platform, shutil, statistics, subprocess, threading, time
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
GODOT = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
SHOOTER = REPO / "godot" / "shooter"
CLK = os.sysconf("SC_CLK_TCK")


def pct(xs, p):
    if not xs:
        return 0.0
    xs = sorted(xs)
    return xs[min(len(xs) - 1, max(0, math.ceil(p / 100 * len(xs)) - 1))]


def stats(xs):
    if not xs:
        return {"n": 0}
    return {"n": len(xs), "mean": statistics.fmean(xs), "p50": pct(xs, 50), "p95": pct(xs, 95), "p99": pct(xs, 99), "max": max(xs), "min": min(xs)}


def out_dir(experiment, stamp=None):
    stamp = stamp or os.environ.get("PERF_STAMP") or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    d = REPO / "out" / "perf" / stamp / experiment
    d.mkdir(parents=True, exist_ok=True)
    return d


def cpus():
    """(server cores, client cores): a quarter, at least two, for the server, the rest for clients (as the loss test does)."""
    c = sorted(os.sched_getaffinity(0))
    if shutil.which("taskset") and len(c) >= 8:
        s = max(2, len(c) // 4)
        return c[:s], c[s:]
    return None, None


def pin(c):
    return ["taskset", "-c", ",".join(map(str, c))] if c and shutil.which("taskset") else []


def _read(path):
    try:
        return Path(path).read_text()
    except OSError:
        return ""


def machine():
    cpu = "?"
    for line in _read("/proc/cpuinfo").splitlines():
        if line.startswith("model name"):
            cpu = line.split(":", 1)[1].strip()
            break
    try:
        godot = subprocess.run([GODOT, "--version"], capture_output=True, text=True, timeout=20).stdout.strip()
    except Exception:
        godot = "?"
    gov = _read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor").strip()
    try:
        gpu = subprocess.run(["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"], capture_output=True, text=True, timeout=10).stdout.strip()
    except Exception:
        gpu = ""
    load = os.getloadavg()
    try:
        head = subprocess.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    except Exception:
        head = ""
    mem = {l.split(":")[0]: int(l.split()[1]) // 1024 for l in _read("/proc/meminfo").splitlines()[:3] if ":" in l}
    return {"cpu": cpu, "cores": os.cpu_count(), "kernel": platform.release(), "godot": godot, "governor": gov, "gpu": gpu, "load1_at_start": load[0],
            "mem_mb": mem, "git": head, "time_utc": datetime.now(timezone.utc).isoformat()}


class Sampler(threading.Thread):
    """Samples RSS (VmRSS/VmHWM kB), threads and CPU time of a pid every `period` seconds until stopped."""

    def __init__(self, pid, period=0.1):
        super().__init__(daemon=True)
        self.pid, self.period, self.rows, self._stop_ev = pid, period, [], threading.Event()
        self.t0 = time.time()

    def stop(self):
        self._stop_ev.set()
        self.join()

    def run(self):
        last_cpu, last_t = None, None
        while not self._stop_ev.is_set():
            st = _read(f"/proc/{self.pid}/status")
            if not st:
                break
            kv = {}
            for line in st.splitlines():
                if line.startswith(("VmRSS", "VmHWM", "VmSize", "Threads")):
                    k, v = line.split(":", 1)
                    kv[k] = int(v.split()[0])
            stat = _read(f"/proc/{self.pid}/stat").rsplit(")", 1)[-1].split()
            cpu = (int(stat[11]) + int(stat[12])) / CLK if len(stat) > 12 else 0.0  # utime + stime, seconds
            now = time.time()
            pct_cpu = 0.0 if last_cpu is None else 100.0 * (cpu - last_cpu) / max(1e-6, now - last_t)
            last_cpu, last_t = cpu, now
            self.rows.append({"t": now - self.t0, "rss_kb": kv.get("VmRSS", 0), "hwm_kb": kv.get("VmHWM", 0), "vsz_kb": kv.get("VmSize", 0),
                              "threads": kv.get("Threads", 0), "cpu_s": cpu, "cpu_pct": pct_cpu})
            self._stop_ev.wait(self.period)

    def write_csv(self, path):
        with open(path, "w") as f:
            f.write("t,rss_kb,hwm_kb,vsz_kb,threads,cpu_s,cpu_pct\n")
            for r in self.rows:
                f.write(f'{r["t"]:.3f},{r["rss_kb"]},{r["hwm_kb"]},{r["vsz_kb"]},{r["threads"]},{r["cpu_s"]:.3f},{r["cpu_pct"]:.1f}\n')


def wait_for(path, marker, timeout):
    """Seconds until `marker` appears in the file at `path`, or None."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if marker in Path(path).read_text(errors="replace"):
            return time.time() - t0
        time.sleep(0.05)
    return None


def write_json(path, obj):
    Path(path).write_text(json.dumps(obj, indent=2, default=str))

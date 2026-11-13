#!/usr/bin/env python3
"""Netcode loss and latency test gate (issue #93): one headless server + N headless bots on the real map, per impairment scenario.

Every scenario: server and bots run the same --net-sim (delay both ways, drop a share of unreliable datagrams), a round with scripted walking
and a few shooting bots, a measured window, then a quiet phase after which server and clients dump their final state. This script joins those
dumps and the per-tick logs, prints a table, asserts recovery and writes out/shooter-loss/<timestamp>/results.json (+ results.md).

Usage: run_loss_test.py [--map DIR] [--bots 16] [--shooters 4] [--measure 30] [--scenarios clean,rtt100] [--out DIR] [--port N]
Scenarios: clean | rtt100 (50 ms each way, 5 ms jitter, 3 % loss each way) | rtt200 (100 ms each way, 10 ms jitter, 3 % loss) | loss10 (50 ms, 10 % loss) | name=latency_ms=..,jitter_ms=..,loss=..
Exit code 0 only if every assertion in every scenario holds.
"""
import argparse, json, math, os, platform, shutil, statistics, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCEN = {
    "clean": "",
    "rtt100": "latency_ms=50,jitter_ms=5,loss=0.03",
    "rtt200": "latency_ms=100,jitter_ms=10,loss=0.03",
    "loss10": "latency_ms=50,jitter_ms=5,loss=0.10",
}
# thresholds (documented in docs/spikes/shooter-loss-test.md)
MIN_SNAP_RATE = 24.0      # snapshots/s per client in the window (server sends 30/s; 3 % loss leaves ~29)
MAX_GAP_MS = 1000.0       # longest wait between two snapshots in the window
MAX_ACK_LAG = 30          # inputs not yet acked at the end (1 s)
MAX_STALE_TICKS = 75      # longest time a decoded player state may differ from the server's: a lost delta heals at the next keyframe (30 ticks), a lost keyframe at the one after, plus margin
MAX_FINAL_PRED_ERR = 0.05 # m, own predicted position vs server at the end
MAX_TICK_P95_MS = 33.3    # server tick work p95 must fit in the 30 Hz budget
MIN_HITS = 20             # combat coverage: the run must contain at least this many hit events


def pct(xs, p):
    if not xs: return 0.0
    xs = sorted(xs)
    return xs[min(len(xs) - 1, max(0, math.ceil(p / 100 * len(xs)) - 1))]


def cpus():
    c = sorted(os.sched_getaffinity(0))
    if shutil.which("taskset") and len(c) >= 8:
        s = max(2, len(c) // 4)
        return c[:s], c[s:]
    return None, None


def pin(c):
    return ["taskset", "-c", ",".join(map(str, c))] if c and shutil.which("taskset") else []


def machine():
    cpu = "?"
    try:
        cpu = next(l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name"))
    except Exception: pass
    try: godot = subprocess.run([GODOT, "--version"], capture_output=True, text=True, timeout=20).stdout.strip()
    except Exception: godot = "?"
    return {"cpu": cpu, "cores": os.cpu_count(), "kernel": platform.release(), "godot": godot, "python": platform.python_version(),
            "load_at_start": os.getloadavg()}


def last_json(path, marker):
    out = None
    for line in Path(path).read_text(errors="replace").splitlines():
        if line.startswith(marker + " "):
            out = json.loads(line[len(marker) + 1:])
    return out


def load_states(path, lo=None, hi=None):
    """csv tick,id,x,y,z,health,state -> {tick: {id: (x,y,z,h,s)}}"""
    d = {}
    for line in Path(path).read_text().splitlines():
        t, i, x, y, z, h, s = map(int, line.split(","))
        if (lo is not None and t < lo) or (hi is not None and t > hi): continue
        d.setdefault(t, {})[i] = (x, y, z, h, s)
    return d


def divergence(server, client):
    """Compare a client's decoded state with the server's record of the same tick. Returns per-sample errors and stale runs (in ticks)."""
    errs, status_bad, samples = [], 0, 0
    runs, cur = [], {}
    for t in sorted(client):
        srv = server.get(t)
        if srv is None: continue
        for i, c in client[t].items():
            s = srv.get(i)
            if s is None: continue
            samples += 1
            e = math.dist(c[:3], s[:3]) / 128.0
            sb = (c[3], c[4]) != (s[3], s[4])
            errs.append(e); status_bad += sb
            bad = e > 1 / 128.0 or sb
            if bad:
                cur.setdefault(i, [t, t])[1] = t
            elif i in cur:
                a, b = cur.pop(i); runs.append(t - a)   # ticks from first bad sample to the first good one
    for i, (a, b) in cur.items(): runs.append(b - a + 1)
    return {"samples": samples, "err_p50": pct(errs, 50), "err_p95": pct(errs, 95), "err_p99": pct(errs, 99), "err_max": max(errs, default=0.0),
            "bad_samples": sum(1 for e in errs if e > 1 / 128.0), "status_bad": status_bad, "runs": len(runs), "run_max_ticks": max(runs, default=0),
            "run_p95_ticks": pct(runs, 95)}


def run_scenario(name, sim, a, outdir, godot, port):
    d = outdir / name; d.mkdir(parents=True, exist_ok=True)
    sc, cc = cpus()
    simarg = ["--net-sim", sim] if sim else []
    mapargs = ["--map", str(Path(a.map).resolve())] if a.map else []
    base = [godot, "--headless", "--path", str(HERE), "--"]
    ms = f"min_players={a.bots},score_goal=1000,time_limit=900,min_spawn_distance=5,respawn_delay=3,spawn_protection=3"
    logs = {}

    def start(tag, args, cp):
        f = open(d / f"{tag}.log", "w")
        logs[tag] = f
        return subprocess.Popen(pin(cp) + base + args + mapargs, stdout=f, stderr=subprocess.STDOUT, text=True)

    deadline = a.measure + 60 + 12 + 160
    srv = start("server", ["--role", "server", "--port", str(port), "--loss-test", str(a.bots), "--loss-measure", str(a.measure), "--loss-out", str(d),
                           "--match-set", ms, "--quit-after", str(deadline)] + simarg
                           + (["--perf-out", str(d / "perf")] if os.environ.get("PERF_PHASES") else []), sc)  # perf-harness: per-phase tick CSV
    t0 = time.time()
    while time.time() - t0 < 90 and "SHOOTER_NET_LISTENING" not in (d / "server.log").read_text(errors="replace"):
        time.sleep(0.5)
    bots = []
    for k in range(a.bots):
        role = "shooter" if k % max(1, a.bots // max(1, a.shooters)) == 0 and sum(1 for b in bots if b[1] == "shooter") < a.shooters else "walker"
        name_b = f"b{k + 1:02d}"
        bots.append((start(name_b, ["--role", "client", "--headless-bot", "--connect", f"127.0.0.1:{port}", "--name", name_b, "--loss-bot", role,
                                    "--loss-measure", str(a.measure), "--loss-out", str(d), "--bot-seconds", str(deadline)] + simarg, cc), role, name_b))
        time.sleep(0.4)
    limit = time.time() + deadline + 30
    for p, _, _ in bots:
        try: p.wait(timeout=max(1, limit - time.time()))
        except subprocess.TimeoutExpired: p.kill()
    try: srv.wait(timeout=max(1, limit - time.time()))
    except subprocess.TimeoutExpired: srv.kill()
    for f in logs.values(): f.close()

    s = last_json(d / "server.log", "LOSSTEST_SERVER")
    clients = [(r, last_json(d / f"{n}.log", "LOSSTEST_CLIENT")) for _, r, n in bots]
    return analyse(name, sim, a, d, s, clients, srv.returncode)


def analyse(name, sim, a, d, s, clients, srv_rc):
    res = {"scenario": name, "net_sim": sim or "none", "failures": []}
    fail = res["failures"].append
    if s is None or not s.get("complete"): fail("server did not finish the scripted round"); res["server"] = s; return res
    got = [c for _, c in clients if c]
    if len(got) != a.bots: fail(f"only {len(got)}/{a.bots} bots reported")
    for _, c in clients:
        if c and not c["ok"]: fail(f"bot {c['name']}: {c['why']}")
    w = s["window"]
    res["server"] = {"window_s": w["window_s"], "players": w["players"], "tick_work_ms": w["tick_work_ms"], "tick_interval_ms": w["tick_interval_ms"],
                     "ticks_over_1.5_intervals": w["ticks_over_33ms_interval"], "ticks_per_s": (w["end_tick"] - w["start_tick"]) / w["window_s"], "cpu_percent_of_one_core": w["cpu_percent_of_one_core"],
                     "sent_kBps_per_client": w["sent_kBps_per_client"], "recv_kBps_per_client": w["recv_kBps_per_client"],
                     "sim_unreliable_sent": w["sim_unreliable_sent"], "sim_unreliable_dropped": w["sim_unreliable_dropped"]}
    if w["players"] < a.bots: fail(f"only {w['players']} players in the window")
    cw = [c["window"] for c in got]
    res["clients"] = {
        "snapshots_per_s_min": min(x["snapshots_per_s"] for x in cw), "snapshots_per_s_mean": statistics.fmean(x["snapshots_per_s"] for x in cw),
        "max_gap_ms_worst": max(x["max_gap_ms"] for x in cw), "max_gap_ms_mean_of_max": statistics.fmean(x["max_gap_ms"] for x in cw),
        "gaps_over_200ms_total": sum(x["gaps_over_200ms"] for x in cw), "gaps_over_500ms_total": sum(x["gaps_over_500ms"] for x in cw), "max_tick_gap": max(x["max_tick_gap"] for x in cw),
        "reconciliations_total": sum(x["reconciliations"] for x in cw), "reconciliations_per_client_max": max(x["reconciliations"] for x in cw),
        "max_pred_error_m": max(x["max_pred_error_m"] for x in cw), "pred_error_p95_m_worst": max(x["pred_error_p95_m"] for x in cw),
        "teleport_snaps": sum(x["teleport_snaps"] for x in cw),
        "recv_kBps_mean": statistics.fmean(x["recv_kBps"] for x in cw), "sent_kBps_mean": statistics.fmean(x["sent_kBps"] for x in cw),
        "ack_lag_max": max(c["ack_lag_inputs"] for c in got), "final_pred_vs_server_m_max": max(c["final_pred_vs_server_m"] for c in got),
        "client_sim_unreliable_dropped": sum(c["sim_unreliable_dropped"] for c in got), "client_sim_unreliable_sent": sum(c["sim_unreliable_sent"] for c in got),
    }
    cl = res["clients"]
    # --- events: reliable hit/death events to victims, scoreboards ---
    end = {p["id"]: p for p in s["final"]["players"]}
    ev = {"players": len(end), "hits_taken_total": sum(p["hitsTaken"] for p in end.values()), "kills_total": sum(p["kills"] for p in end.values()),
          "deaths_total": sum(p["deaths"] for p in end.values()), "victim_event_mismatch": 0, "death_event_mismatch": 0, "attacker_event_mismatch": 0, "scoreboard_mismatch": 0}
    srv_board = {r["id"]: (r["kills"], r["deaths"]) for r in s["final"]["scoreboard"]}
    for c in got:
        p = end.get(c["id"])
        if p is None: continue
        if c["hit_events_as_victim"] != p["hitsTaken"]: ev["victim_event_mismatch"] += 1; fail(f"{c['name']}: {c['hit_events_as_victim']} hit events as victim, server dealt {p['hitsTaken']}")
        if c["hit_events_as_attacker"] != p["hitsDealt"]: ev["attacker_event_mismatch"] += 1; fail(f"{c['name']}: {c['hit_events_as_attacker']} hit events as attacker, server {p['hitsDealt']}")
        if c["death_events_self"] != p["deathEventsSent"]: ev["death_event_mismatch"] += 1; fail(f"{c['name']}: {c['death_events_self']} own death events, server {p['deathEventsSent']}")
        board = {r["id"]: (r["kills"], r["deaths"]) for r in c["final_scoreboard"]}
        if board != srv_board: ev["scoreboard_mismatch"] += 1; fail(f"{c['name']}: scoreboard differs from the server's")
    res["events"] = ev
    if ev["hits_taken_total"] < MIN_HITS: fail(f"only {ev['hits_taken_total']} hits happened (< {MIN_HITS}): not enough combat to judge the events")
    # --- final state agreement ---
    srv_final = {p["id"]: (p["x"], p["y"], p["z"], p["health"], p["state"]) for p in s["final"]["players"]}
    fin = {"clients_with_mismatch": 0, "entity_mismatches": 0, "max_pos_err_m": 0.0, "static_server": all(
        (p["x"], p["y"], p["z"], p["health"], p["state"]) == srv_final[p["id"]] for p in (s.get("late") or {"players": []})["players"] if p["id"] in srv_final) and bool(s.get("late"))}
    # Each client's final state is compared with the server's record at the same tick (its last snapshot's). The server's dump at
    # ServerFinalAfterStop can predate a late server move (a knockback after the quiet phase began): a race in the gate, not a decode error.
    server_ticks = load_states(d / "server_states.csv")
    for c in got:
        bad = 0
        seen = {p["id"]: (p["x"], p["y"], p["z"], p["health"], p["state"]) for p in c["final_players"]}
        ft = c.get("final_tick") or 0
        ref = server_ticks[ft] if ft in server_ticks else srv_final
        for i, v in ref.items():
            if seen.get(i) != v:
                bad += 1
                if i in seen: fin["max_pos_err_m"] = max(fin["max_pos_err_m"], math.dist(seen[i][:3], v[:3]) / 128)
                fail(f"{c['name']}: final state of #{i} is {seen.get(i)}, server at tick {ft} has {v}")
        fin["entity_mismatches"] += bad; fin["clients_with_mismatch"] += bad > 0
    res["final_state"] = fin
    if not fin["static_server"]: fail("server state still changing at the end: final comparison inconclusive")
    # --- per tick divergence of decoded snapshots vs the server's record (window only) ---
    server = load_states(d / "server_states.csv", w["start_tick"], w["end_tick"])
    agg = {"samples": 0, "bad_samples": 0, "status_bad": 0, "runs": 0, "run_max_ticks": 0, "err_p95": 0.0, "err_p99": 0.0, "err_max": 0.0}
    for c in got:
        f = d / f"snap_{c['name']}.csv"
        if not f.exists(): fail(f"{c['name']}: no snapshot log"); continue
        r = divergence(server, load_states(f, w["start_tick"], w["end_tick"]))
        for k in ("samples", "bad_samples", "status_bad", "runs"): agg[k] += r[k]
        agg["run_max_ticks"] = max(agg["run_max_ticks"], r["run_max_ticks"])
        for k in ("err_p95", "err_p99", "err_max"): agg[k] = max(agg[k], r[k])
    agg["bad_share"] = agg["bad_samples"] / max(1, agg["samples"])
    res["divergence"] = agg
    # --- assertions ---
    if cl["snapshots_per_s_min"] < MIN_SNAP_RATE: fail(f"a client received only {cl['snapshots_per_s_min']:.1f} snapshots/s (< {MIN_SNAP_RATE})")
    if cl["max_gap_ms_worst"] > MAX_GAP_MS: fail(f"a client waited {cl['max_gap_ms_worst']:.0f} ms for a snapshot (> {MAX_GAP_MS})")
    if cl["ack_lag_max"] > MAX_ACK_LAG: fail(f"a client ended {cl['ack_lag_max']} inputs behind the server ack (> {MAX_ACK_LAG})")
    if cl["final_pred_vs_server_m_max"] > MAX_FINAL_PRED_ERR: fail(f"own predicted position ends {cl['final_pred_vs_server_m_max']:.3f} m off the server (> {MAX_FINAL_PRED_ERR})")
    if agg["run_max_ticks"] > MAX_STALE_TICKS: fail(f"a decoded player state stayed wrong for {agg['run_max_ticks']} ticks (> {MAX_STALE_TICKS})")
    if res["server"]["tick_work_ms"]["p95"] > MAX_TICK_P95_MS: fail(f"server tick work p95 {res['server']['tick_work_ms']['p95']:.1f} ms (> {MAX_TICK_P95_MS})")
    return res


def md_table(results):
    rows = [("scenario", lambda r: r["scenario"]), ("net-sim", lambda r: r["net_sim"]),
            ("snapshots/s min / mean", lambda r: f"{r['clients']['snapshots_per_s_min']:.1f} / {r['clients']['snapshots_per_s_mean']:.1f}"),
            ("max snapshot gap ms (worst client / mean of clients)", lambda r: f"{r['clients']['max_gap_ms_worst']:.0f} / {r['clients']['max_gap_ms_mean_of_max']:.0f}"),
            ("gaps > 200 / > 500 ms (all clients)", lambda r: f"{r['clients']['gaps_over_200ms_total']} / {r['clients']['gaps_over_500ms_total']}"),
            ("reconciliations total (max per client)", lambda r: f"{r['clients']['reconciliations_total']} ({r['clients']['reconciliations_per_client_max']})"),
            ("max prediction error m / p95 worst client", lambda r: f"{r['clients']['max_pred_error_m']:.3f} / {r['clients']['pred_error_p95_m_worst']:.3f}"),
            ("hits / kills / deaths (server)", lambda r: f"{r['events']['hits_taken_total']} / {r['events']['kills_total']} / {r['events']['deaths_total']}"),
            ("victim hit-event / death-event / scoreboard mismatches", lambda r: f"{r['events']['victim_event_mismatch']} / {r['events']['death_event_mismatch']} / {r['events']['scoreboard_mismatch']}"),
            ("final state entity mismatches", lambda r: str(r['final_state']['entity_mismatches'])),
            ("decoded vs server samples wrong (share) / longest wrong run ticks", lambda r: f"{r['divergence']['bad_share'] * 100:.2f} % / {r['divergence']['run_max_ticks']}"),
            ("decoded position error p99 / max m", lambda r: f"{r['divergence']['err_p99']:.3f} / {r['divergence']['err_max']:.3f}"),
            ("server tick work p50 / p95 / p99 / max ms", lambda r: "{p50:.2f} / {p95:.2f} / {p99:.2f} / {max:.2f}".format(**r['server']['tick_work_ms'])),
            ("server ticks/s (tick counter over window)", lambda r: f"{r['server']['ticks_per_s']:.2f}"),
            ("server tick interval p50 / p99 / max ms", lambda r: "{p50:.1f} / {p99:.1f} / {max:.1f}".format(**r['server']['tick_interval_ms'])),
            ("server CPU % of one core", lambda r: f"{r['server']['cpu_percent_of_one_core']:.0f}"),
            ("kB/s per client down / up (client side)", lambda r: f"{r['clients']['recv_kBps_mean']:.2f} / {r['clients']['sent_kBps_mean']:.2f}"),
            ("kB/s per client server send / recv (server side)", lambda r: f"{r['server']['sent_kBps_per_client']:.2f} / {r['server']['recv_kBps_per_client']:.2f}"),
            ("result", lambda r: "PASS" if not r["failures"] else f"FAIL ({len(r['failures'])})")]
    ok = [r for r in results if "clients" in r and "divergence" in r]
    lines = ["| metric | " + " | ".join(r["scenario"] for r in ok) + " |", "|---|" + "---|" * len(ok)]
    for label, f in rows[1:]:
        lines.append(f"| {label} | " + " | ".join(f(r) for r in ok) + " |")
    return "\n".join(lines)


def main():
    global GODOT
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", default=os.environ.get("DRYCASTLE_MAP", ""))
    ap.add_argument("--bots", type=int, default=16)
    ap.add_argument("--shooters", type=int, default=4)
    ap.add_argument("--measure", type=float, default=30)
    ap.add_argument("--scenarios", default="clean,rtt100")
    ap.add_argument("--out", default="")
    ap.add_argument("--port", type=int, default=27940)
    a = ap.parse_args()
    GODOT = os.environ.get("GODOT", str(Path.home() / ".local/bin/godot"))
    outdir = Path(a.out).resolve() if a.out else REPO / "out" / "shooter-loss" / time.strftime("%Y%m%d-%H%M%S")
    outdir.mkdir(parents=True, exist_ok=True)
    info = machine(); print("machine:", json.dumps(info))
    results = []
    for k, sc in enumerate(a.scenarios.split(",")):
        name, _, spec = sc.partition("=")
        if spec == "": name, spec = sc, SCEN[sc]
        print(f"=== scenario {name}: {spec or 'no impairment'}", flush=True)
        r = run_scenario(name, spec, a, outdir, GODOT, a.port + k)
        results.append(r)
        print(f"    {'PASS' if not r['failures'] else 'FAIL'}", flush=True)
        for f in r["failures"][:12]: print("    -", f)
    (outdir / "results.json").write_text(json.dumps({"machine": info, "args": vars(a), "results": results}, indent=1))
    try:
        table = md_table(results); (outdir / "results.md").write_text(table + "\n"); print(table)
    except Exception as e: print("table failed:", e)
    ok = all(not r["failures"] for r in results)
    print(f"results: {outdir}")
    print("SHOOTER_LOSS_TEST_OK" if ok else "SHOOTER_LOSS_TEST_FAIL")
    return 0 if ok else 1


GODOT = "godot"
if __name__ == "__main__":
    sys.exit(main())

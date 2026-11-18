#!/usr/bin/env python3
"""Writes grafana/dashboards/drycastle-server.json (the provisioned dashboard) from the panel list below. Run after editing it:
    deploy/monitoring/make_dashboard.py
Series: drycastle_* are pushed by the game server every 10 s (NetServer.Metrics.cs); node_* are the host's (node-exporter)."""
import json, os

DS = {"type": "prometheus", "uid": "metrics"}
panels = []
y = 0

def row(title):
    global y
    panels.append({"type": "row", "title": title, "collapsed": False, "gridPos": {"h": 1, "w": 24, "x": 0, "y": y}, "id": len(panels) + 1})
    y += 1

def ts(title, queries, x, w=12, h=8, unit="short", stack=False, desc="", max_=None, thresholds=None):
    """queries: (expr, legend) pairs."""
    custom = {"drawStyle": "line", "lineWidth": 1, "fillOpacity": 25 if stack else 5, "showPoints": "never", "spanNulls": 30000,
              "stacking": {"mode": "normal" if stack else "none"}}
    defaults = {"unit": unit, "custom": custom, "min": 0}
    if max_ is not None: defaults["max"] = max_
    if thresholds:
        defaults["thresholds"] = {"mode": "absolute", "steps": [{"color": "green", "value": None}] + [{"color": c, "value": v} for v, c in thresholds]}
        custom["thresholdsStyle"] = {"mode": "line"}
    panels.append({
        "type": "timeseries", "title": title, "description": desc, "datasource": DS, "id": len(panels) + 1,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "fieldConfig": {"defaults": defaults, "overrides": []},
        "options": {"legend": {"displayMode": "table", "placement": "right" if w >= 12 else "bottom", "calcs": ["lastNotNull", "max"]},
                    "tooltip": {"mode": "multi", "sort": "desc"}},
        "targets": [{"datasource": DS, "expr": e, "legendFormat": l, "refId": chr(65 + i)} for i, (e, l) in enumerate(queries)],
    })

def stat(title, expr, x, w=4, unit="short", thresholds=None, desc=""):
    steps = [{"color": "green", "value": None}] + [{"color": c, "value": v} for v, c in (thresholds or [])]
    panels.append({
        "type": "stat", "title": title, "description": desc, "datasource": DS, "id": len(panels) + 1,
        "gridPos": {"h": 4, "w": w, "x": x, "y": y},
        "fieldConfig": {"defaults": {"unit": unit, "thresholds": {"mode": "absolute", "steps": steps}}, "overrides": []},
        "options": {"reduceOptions": {"calcs": ["lastNotNull"]}, "colorMode": "value", "graphMode": "area"},
        "targets": [{"datasource": DS, "expr": expr, "refId": "A"}],
    })

def nextline(h=8):
    global y
    y += h

row("Now")
stat("Last report", "time() - timestamp(drycastle_up_reports)", 0, unit="s", thresholds=[(30, "orange"), (120, "red")],
     desc="Seconds since the server last sent its numbers. It sends every 10 s: more than two minutes means it is down or hung.")
stat("Players", "drycastle_match_players", 4)
stat("Bots", "drycastle_match_bots", 8)
stat("Tick p99", "drycastle_tick_p99_ms", 12, unit="ms", thresholds=[(25, "orange"), (33.3, "red")], desc="The budget of a tick is 33.3 ms (30 a second).")
stat("Server memory", "drycastle_mem_rss_mb", 16, unit="decmbytes", thresholds=[(2600, "orange"), (3000, "red")])
stat("Host memory free", "node_memory_MemAvailable_bytes", 20, unit="bytes", thresholds=None)
nextline(4)

row("Tick")
ts("Tick time", [("drycastle_tick_p50_ms", "p50"), ("drycastle_tick_p99_ms", "p99"), ("drycastle_tick_max_ms", "slowest"), ("drycastle_tick_budget_ms", "budget")], 0, unit="ms",
   desc="Of the ticks of each 10 s. Over the budget the server runs behind.")
ts("Late ticks and skipped snapshots, per 10 s", [("drycastle_tick_overruns", "ticks over the budget"), ("drycastle_tick_skipped_snapshots", "skipped snapshots")], 12)
nextline()
ts("Tick phases, p99", [("drycastle_phase_p99_us", "{{phase}}")], 0, unit="µs", desc="The parts of a tick in the order they run (NetServer._PhysicsProcess).")
ts("Slowest tick of 10 s and the GC pause in it", [("drycastle_worst_tick_ms", "slowest tick"), ("drycastle_worst_tick_gc_pause_ms", "GC pause in it"), ("drycastle_worst_tick_without_gc_ms", "slowest without a collection")], 12, unit="ms")
nextline()

row("Where the time goes")
ts("CPU by thread", [('drycastle_thread_cpu_pct{thread!="total"}', "{{thread}}")], 0, unit="percent", stack=True, desc="Percent of one core. The machine has two.")
ts("Costliest scopes", [("topk(14, drycastle_scope_ms_s)", "{{scope}}")], 12, unit="ms",
   desc="Milliseconds of the game thread per second of play (10 is 1 % of a core). Scopes nest, so they do not add up. Only while somebody plays.")
nextline()
ts("Bot navigation", [('drycastle_nav_ms_s', "search ms per s"), ('drycastle_nav_calls_s', "routes per s")], 0)
ts("Match", [("drycastle_match_players", "players"), ("drycastle_match_bots", "bots"), ("drycastle_match_entities", "entities")], 12)
nextline()

row("Memory and garbage collection")
ts("Server memory", [("drycastle_mem_rss_mb", "process (RSS)"), ("drycastle_mem_heap_mb", "managed heap"), ("drycastle_mem_gc_committed_mb", "GC committed")], 0, unit="decmbytes",
   desc="A line that only climbs over days is a leak.")
ts("Engine objects", [("drycastle_mem_objects", "objects"), ("drycastle_mem_nodes", "nodes"), ("drycastle_mem_orphans", "orphan nodes")], 12, desc="Counts that only grow are a leak; orphans should stay near zero.")
nextline()
ts("Allocation", [("drycastle_gc_period_alloc_mb_s", "all threads, MB/s")], 0, w=8, unit="short")
ts("Allocation by phase, kB/s", [("drycastle_phase_alloc_kb_s", "{{phase}}")], 8, w=8, stack=True)
ts("Collections per 10 s and their pause", [("drycastle_gc_period_gen0", "gen0"), ("drycastle_gc_period_gen1", "gen1"), ("drycastle_gc_period_gen2", "gen2"), ("drycastle_gc_period_pause_ms", "pause, ms")], 16, w=8)
nextline()

row("Network and database")
ts("Sent per client", [("drycastle_match_out_bytes_s_per_client", "bytes per second")], 0, w=8, unit="Bps")
ts("Dropped packets and refused saves, per 10 s", [("drycastle_match_dropped_packets", "dropped packets"), ("drycastle_match_rejected_saves", "refused saves")], 8, w=8)
ts("Database queue", [("drycastle_match_db_queue", "saves waiting")], 16, w=8)
nextline()

row("Host")
ts("Host CPU", [('sum by (mode) (rate(node_cpu_seconds_total{mode!="idle"}[2m])) * 100', "{{mode}}")], 0, w=8, unit="percent", stack=True,
   desc="Percent of one core, of two. 'steal' is time the provider gave to other machines: the server was ready to run and could not.")
ts("Host memory", [("node_memory_MemAvailable_bytes", "available"), ("node_memory_MemTotal_bytes", "total")], 8, w=8, unit="bytes", desc="No swap on this machine: when available reaches zero the kernel kills a process.")
ts("Disk free and load", [('node_filesystem_avail_bytes{mountpoint="/"}', "disk free")], 16, w=8, unit="bytes")
nextline()

dash = {
    "uid": "drycastle-server", "title": "Drycastle server", "tags": ["drycastle"], "timezone": "browser", "schemaVersion": 39, "version": 1,
    "refresh": "30s", "time": {"from": "now-6h", "to": "now"}, "editable": False, "graphTooltip": 1, "panels": panels,
}
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "grafana", "dashboards", "drycastle-server.json")
with open(out, "w") as f:
    json.dump(dash, f, indent=1)
    f.write("\n")
print(out, len(panels), "panels")

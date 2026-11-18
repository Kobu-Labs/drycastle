#!/usr/bin/env python3
"""Writes grafana/dashboards/drycastle-server.json and drycastle-clients.json (the provisioned dashboards) from the panel lists below. Run after editing it:
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

def finish(uid, title, variables=()):
    """Writes the panels so far as the dashboard uid, and starts an empty one."""
    global panels, y
    dash = {
        "uid": uid, "title": title, "tags": ["drycastle"], "timezone": "browser", "schemaVersion": 39, "version": 1,
        "refresh": "30s", "time": {"from": "now-6h", "to": "now"}, "editable": False, "graphTooltip": 1, "panels": panels,
        "templating": {"list": list(variables)},
        "links": [{"type": "dashboards", "tags": ["drycastle"], "asDropdown": False, "title": "Drycastle", "icon": "external link"}],
    }
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "grafana", "dashboards", uid + ".json")
    with open(out, "w") as f:
        json.dump(dash, f, indent=1)
        f.write("\n")
    print(out, len(panels), "panels")
    panels, y = [], 0

finish("drycastle-server", "Drycastle server")

# ---- the clients: what each player's game sends through the server every 10 s of play (NetClient.Report.cs, docs/monitoring.md "Clients")
P = '{player=~"$player"}'
L = "{{player}}"
def c(field, legend=L): return (f"drycastle_client_{field}{P}", legend)

row("Now")
stat("Clients reporting", "count(drycastle_client_fps)", 0, desc="Players whose game sent its numbers in the last minutes. A bot sends none.")
stat("Lowest fps", f"min(drycastle_client_fps{P})", 4, thresholds=None)
stat("Worst frame p99", f"max(drycastle_client_frame_p99_ms{P})", 8, unit="ms", thresholds=[(16.7, "orange"), (33.3, "red")])
stat("Highest ping", f"max(drycastle_client_rtt_ms{P})", 12, unit="ms", thresholds=[(80, "orange"), (150, "red")])
stat("Highest loss", f"max(drycastle_client_loss_pct{P})", 16, unit="percent", thresholds=[(1, "orange"), (5, "red")])
stat("Frames over 100 ms", f"sum(drycastle_client_frames_over_100ms{P})", 20, thresholds=[(1, "orange"), (5, "red")], desc="In the last 10 s, all selected players together.")
nextline(4)

row("Frames")
ts("Frames per second", [c("fps")], 0, desc="Frames drawn in each 10 s, per second. The game caps a focused window at 120 and an unfocused one at 20.")
ts("Frame time p99", [c("frame_p99_ms")], 12, unit="ms", thresholds=[(16.7, "orange"), (33.3, "red")], desc="One frame in a hundred took longer than this. 16.7 ms is 60 a second, 33.3 ms is 30.")
nextline()
ts("Frame time p50", [c("frame_p50_ms")], 0, w=8, unit="ms")
ts("Slowest frame", [c("frame_max_ms")], 8, w=8, unit="ms", desc="The longest frame of each 10 s: a hitch.")
ts("Hitches per 10 s", [c("frames_over_33ms", L + " over 33 ms"), c("frames_over_50ms", L + " over 50 ms"), c("frames_over_100ms", L + " over 100 ms")], 16, w=8)
nextline()

row("Where a frame goes")
ts("Client scopes", [(f'drycastle_client_scope_ms_per_s{{player=~"$player"}}', "{{player}} {{scope}}")], 0, unit="ms", stack=True,
   desc="Milliseconds of the game's own script work per second of play, by part (10 ms/s is 1 % of a core). Not in it: the engine's rendering, physics and audio.")
ts("Render time", [c("render_cpu_ms", L + " cpu"), c("render_gpu_ms", L + " gpu")], 12, unit="ms",
   desc="What the engine measured for the main viewport per frame: preparing the frame on the processor, and drawing it on the graphics card.")
nextline()
ts("Script time, worst frame", [c("process_peak_ms", L + " process"), c("physics_peak_ms", L + " physics")], 0, w=8, unit="ms", desc="The engine's 'process' and 'physics process' times: all scripts of a frame and of a physics step. The engine keeps the worst of each second; this is their mean over 10 s.")
ts("Draw calls", [c("draws")], 8, w=8, desc="Per frame, the mean of the period.")
ts("Triangles and objects", [c("prims_k", L + " thousand primitives"), c("objects", L + " objects")], 16, w=8)
nextline()

row("Memory and garbage collection")
ts("Memory", [c("rss_mb", L + " process"), c("gc_heap_mb", L + " C# heap"), c("video_mem_mb", L + " video")], 0, w=8, unit="decmbytes")
ts("Allocation", [c("alloc_mb_s")], 8, w=8, unit="MBs", desc="What the C# code allocates: the collector's work.")
ts("Collector pause per 10 s", [c("gc_pause_ms")], 16, w=8, unit="ms", desc="A pause is a hitch when it is longer than a frame.")
nextline()
ts("Collections per 10 s", [c("gc_gen0", L + " gen0"), c("gc_gen1", L + " gen1"), c("gc_gen2", L + " gen2")], 0, w=8)
ts("Nodes in the scene", [c("nodes")], 8, w=8, desc="A number that only grows is a leak.")
ts("What the client sees", [c("remotes", L + " players"), c("world_entities", L + " world entities")], 16, w=8)
nextline()

row("Connection")
ts("Ping", [c("rtt_ms"), c("rtt_var_ms", L + " variance")], 0, w=8, unit="ms")
ts("Packet loss", [c("loss_pct")], 8, w=8, unit="percent")
ts("Longest gap between snapshots", [c("snap_gap_max_ms")], 16, w=8, unit="ms", thresholds=[(100, "orange"), (250, "red")], desc="The server sends 30 a second (33 ms apart). A long gap is a freeze of the others on this player's screen.")
nextline()
ts("Snapshots per second", [c("snaps_per_s")], 0, w=8, max_=35)
ts("Corrections", [c("reconciles", L + " reconciles per 10 s"), c("tick_skips", L + " ticks skipped")], 8, w=8, desc="A reconcile: the server put the player somewhere else than the client predicted, and the client replayed its inputs.")
ts("Traffic", [c("in_kb_s", L + " in"), c("out_kb_s", L + " out")], 16, w=8, unit="KBs")
nextline()
ts("Largest prediction error", [c("pred_err_max_m")], 0, w=8, unit="lengthm")
ts("Window size", [c("res_w", L + " width"), c("res_h", L + " height")], 8, w=8, unit="pixel")
nextline()

finish("drycastle-clients", "Drycastle clients", variables=[{
    "name": "player", "label": "Player", "type": "query", "datasource": DS, "query": {"query": "label_values(drycastle_client_fps, player)", "refId": "A"},
    "definition": "label_values(drycastle_client_fps, player)", "refresh": 2, "multi": True, "includeAll": True, "allValue": ".*",
    "current": {"selected": True, "text": "All", "value": "$__all"}, "sort": 1,
}])

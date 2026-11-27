#!/usr/bin/env python3
"""Join-time bench (docs/perf-investigation/join-time.md): a headless server, then N windowed clients joining it through the menu path
(--connect), one after another; every output line is timestamped. Set DRYCASTLE_LOAD_TIMES=1 for the load timers, PROJ for another checkout.

With --menu as the first extra argument the client gets no --connect: it sits in the main menu while the map loads behind it, and the run
ends at the SHOOTER_MAP_READY line (the menu time a player needs before a join waits only for the connection). No server then.

Usage: DRYCASTLE_LOAD_TIMES=1 perf/join_bench.py OUT_DIR RUNS [[--menu] EXTRA_CLIENT_ARGS]"""
import os, re, subprocess, sys, threading, time, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import REPO, GODOT, SHOOTER, cpus, pin, window_env
MAP = os.environ.get('MAP', str(REPO / 'out' / 'village' / 'map'))  # a worktree has no map of its own: pass MAP=
PROJ = os.environ.get('PROJ', str(REPO))
GAME = PROJ + '/godot/shooter' if 'PROJ' in os.environ else str(SHOOTER)
OUT = sys.argv[1]; RUNS = int(sys.argv[2]); PORT = 27341; EXTRA = sys.argv[3].split() if len(sys.argv) > 3 else []
MENU = bool(EXTRA) and EXTRA[0] == '--menu'
if MENU: EXTRA = EXTRA[1:]
os.makedirs(OUT, exist_ok=True)
SERVER_CORES, CLIENT_CORES = cpus()  # None: fewer than 8 cores or no taskset, nothing is pinned
env = dict(os.environ); env.setdefault('DRYCASTLE_ASSETS_PRIVATE', str(REPO / 'assets-private'))

class Proc:
    def __init__(self, name, args, window=False, cores=None):
        e = window_env(env) if window else dict(env)
        self.t0 = time.time(); self.lines = []; self.name = name
        self.f = open(f'{OUT}/{name}.log', 'w')
        self.p = subprocess.Popen(pin(cores) + args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=e, bufsize=1)
        self.th = threading.Thread(target=self._drain, daemon=True); self.th.start()
    def _drain(self):
        for l in self.p.stdout:
            t = time.time() - self.t0
            self.lines.append((t, l.rstrip('\n')))
            self.f.write(f'{t:8.3f} {l}'); self.f.flush()
    def wait_for(self, marker, timeout):
        end = time.time() + timeout; i = 0
        while time.time() < end:
            while i < len(self.lines):
                if marker in self.lines[i][1]: return self.lines[i][0]
                i += 1
            if self.p.poll() is not None and i >= len(self.lines): return None
            time.sleep(0.05)
        return None
    def first(self, marker):
        for t, l in self.lines:
            if marker in l: return t, l
        return None, None
    def stop(self):
        self.p.terminate()
        try: self.p.wait(timeout=15)
        except subprocess.TimeoutExpired: self.p.kill()

def load(): return open('/proc/loadavg').read().split()[:3]
def client(r, args):
    return Proc(f'client{r+1}', [GODOT, '--path', GAME, '--', '--role', 'client', '--name', f'bench{r+1}', '--map', MAP] + args + EXTRA, window=True, cores=CLIENT_CORES)

def run_record(c, la):
    m = {k: c.first(k)[0] for k in ['Vulkan', 'sounds:', 'SHOOTER_MENU', 'map load phases', 'map 0.', 'client: connecting', 'SHOOTER_GAME_STARTED', 'client: welcome', 'client: join synced', 'SHOOTER_MAP_READY']}
    hitches = [(round(t, 2), l) for t, l in c.lines if 'loading hitch' in l]
    run = {'load_before': la, 'marks': m, 'phases': c.first('map load phases')[1], 'map': c.first('map 0.')[1], 'hitches': len(hitches), 'hitch_by_stage': {}}
    for _, l in hitches:
        s = l.split('during')[-1].strip(); run['hitch_by_stage'][s] = run['hitch_by_stage'].get(s, 0) + 1
    run['loadtime'] = [l for _, l in c.lines if l.startswith('loadtime')]
    return run

res = {'load_start': load(), 'menu': MENU}
srv = None
if not MENU:
    srv = Proc('server', [GODOT, '--headless', '--path', GAME, '--', '--role', 'server', '--port', str(PORT), '--map', MAP, '--quit-after', '1500'], cores=SERVER_CORES)
    t = srv.wait_for('SHOOTER_NET_LISTENING', 240)
    res['server'] = {'listening_s': t, 'phases': srv.first('map load phases')[1], 'map': srv.first('map 0.')[1]}
    print('server', res['server'], flush=True)
runs = []
for r in range(RUNS):
    time.sleep(3)
    la = load()
    if MENU:
        c = client(r, [])
        tr = c.wait_for('SHOOTER_MAP_READY', 400)  # seconds since the client started
        line = c.first('SHOOTER_MAP_READY')[1]
        c.stop()
        run = run_record(c, la)
        run['map_ready_s'] = tr
        run['map_ready_ms'] = int(re.search(r'ms=(\d+)', line).group(1)) if line else None  # since the game process started
    else:
        c = client(r, ['--connect', f'127.0.0.1:{PORT}'])
        c.wait_for('client: welcome', 400)
        c.wait_for('SHOOTER_GAME_STARTED', 60)  # the loading screen ends once the join is applied (JoinSynced)
        time.sleep(4)
        c.stop()
        run = run_record(c, la)
    runs.append(run); print(json.dumps(run), flush=True)
if srv: srv.stop()
res['runs'] = runs; res['load_end'] = load()
json.dump(res, open(f'{OUT}/result.json', 'w'), indent=1)

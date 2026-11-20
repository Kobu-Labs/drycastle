#!/usr/bin/env python3
"""Medians of the server's "server: stats" / "server: prof" lines over the 10 s windows of one or more runs, side by side
(docs/perf-investigation/vps-64-bots.md). The first 3 windows with a player in are left out (the bots are still spawning).

    perf/vps_summary.py docs/perf-investigation/vps-logs/*.log.gz out/perf/vps/<new>.log

Scopes are ms per second of play (10 ms/s = 1 % of a core); alloc is kB/s per phase. Only rows of 3 ms/s (20 kB/s) or more are printed.
"""
import gzip, re, sys, statistics as st
def med(v): return st.median(v) if v else float('nan')
def run(path, skip=3):
    L=(gzip.open(path, 'rt', errors='replace') if path.endswith('.gz') else open(path, errors='replace')).read().splitlines()
    stats=[l for l in L if 'server: stats' in l and re.search(r'players [1-9]', l) and re.search(r'bots [1-9]', l)][skip:]
    out={}
    def g(pat, lines, f=float): return [f(m.group(1)) for l in lines for m in [re.search(pat,l)] if m]
    out['windows']=len(stats)
    out['tick_p50']=med(g(r'tick p50 ([0-9.]+)',stats)); out['tick_p99']=med(g(r'p99 ([0-9.]+) ms \(budget',stats))
    out['overruns_per_win']=st.mean(g(r'overruns (\d+)',stats)) if stats else 0
    out['alloc_mb_s']=med(g(r'alloc ([0-9.]+) MB/s',stats)); out['gc_pause_ms_per_10s']=med(g(r'gc pause ([0-9.]+) ms',stats))
    out['gcs_per_10s']=med(g(r'gc gen0 (\d+)',stats))
    n=len(stats)
    def last(key): return [l for l in L if key in l][-n:] if n else []
    worst=g(r' ms=([0-9.]+)', last('prof worst'))
    out['worst_med']=med(worst); out['worst_max']=max(worst) if worst else 0
    th=last('prof threads')
    for k in ('main','bot_nav','total'): out['cpu_'+k]=med(g(k+r'=([0-9.]+)',th))
    sc=last('prof scopes')
    names=set(re.findall(r' ([a-z_]+)=[0-9.]+\|', ' '.join(sc)))
    out['scopes']={k:med(g(' '+k+r'=([0-9.]+)\|',sc)) for k in sorted(names)}
    al=last('prof alloc')
    an=set(re.findall(r' ([a-z]+)=\d+', ' '.join(a.split('kb/s')[1] for a in al)))
    out['alloc']={k:med(g(' '+k+r'=(\d+)',al)) for k in sorted(an)}
    gc=last('prof gc')
    out['gc_pause_each']=med(g(r'pause_ms=([0-9.]+)',gc)); out['finalize_pending']=med(g(r'finalize_pending=(\d+)',gc)); out['promoted_kb']=med(g(r'promoted_kb=(\d+)',gc))
    return out
if len(sys.argv) < 2: sys.exit(__doc__)
runs=[run(p) for p in sys.argv[1:]]
for i, p in enumerate(sys.argv[1:]): print(f'run {i + 1}: {p}')
keys=[k for k in runs[0] if k not in('scopes','alloc')]
for k in keys: print(f'{k:22s}', *[f'{r.get(k,float("nan")):10.2f}' for r in runs])
for sect in ('scopes','alloc'):
    print('--',sect)
    ks=sorted(set().union(*[r[sect].keys() for r in runs]))
    for k in ks:
        vals=[r[sect].get(k,float('nan')) for r in runs]
        if max(v for v in vals if v==v) >= (3 if sect=='scopes' else 20): print(f'{k:22s}', *[f'{v:10.2f}' for v in vals])

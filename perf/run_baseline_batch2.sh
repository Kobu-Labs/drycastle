#!/usr/bin/env bash
# Follow-up baseline runs: 64 and 96 players via server-side bots (the loss test cannot connect 64 clients: MaxPeers = 32), a no-loss control for
# the 56-bot stalls, client variance repeats and the Intel iGPU (explicit adapter index).
set -u
cd "$(dirname "$0")/.."
: "${PERF_STAMP:?set PERF_STAMP}"
export PERF_STAMP
B=out/perf/$PERF_STAMP/_batch
mkdir -p "$B"
step() { local n=$1; shift; echo "=== $n $(date +%T)" | tee -a "$B/_progress.txt"; "$@" > "$B/$n.txt" 2>&1; echo "    rc=$? $(date +%T)" | tee -a "$B/_progress.txt"; }

step N3-64p python3 -I perf/run_load.py --tag N3-64p --bots 48 --walkers 16 --duration 70 --warm 20 --port 28010
step N3-96p python3 -I perf/run_load.py --tag N3-96p --bots 64 --walkers 32 --duration 70 --warm 20 --port 28020
step W3-bots56-loss0 python3 -I perf/run_wire.py --tag W3-bots56-loss0 --rtt 100 --loss 0 --bots 56 --shooters 4 --walkers 12 --duration 60 --warm 10 --port 28030
step W3-bots56-direct python3 -I perf/run_load.py --tag W3-56bots-direct --bots 56 --walkers 16 --duration 60 --warm 20 --port 28040
step R-nvidia-repeat python3 -I perf/run_route.py --variants base,msaa0,noshadow --repeat 3 --views 48 --dwell 1.5 --gpu nvidia --tag R2-route
step R-gpu0 python3 -I perf/run_route.py --variants base,msaa0 --views 48 --dwell 1.5 --gpu intel --gpu-index 0 --tag R3-route --timeout 1500
step R-gpu1 python3 -I perf/run_route.py --variants base,msaa0 --views 48 --dwell 1.5 --gpu intel --gpu-index 1 --tag R3-route --timeout 1500
echo "=== ALL DONE $(date +%T)" | tee -a "$B/_progress.txt"

#!/usr/bin/env bash
# The "after" measurements: the same experiments as the baseline batches, on the merged build. Each step takes the shared lock (flock -o on perf2).
# Usage: PERF_STAMP=<stamp> bash perf/run_after_batch.sh [steps...]   (default: all)
set -u
cd "$(dirname "$0")/.."
: "${PERF_STAMP:?set PERF_STAMP}"
export PERF_STAMP
B=out/perf/$PERF_STAMP/_batch
mkdir -p "$B"
MAP=$PWD/out/village/map
L="flock -o -w 14400 /tmp/drycastle-perf2.lock"
step() { local n=$1; shift; echo "=== $n $(date +%T)" | tee -a "$B/_progress.txt"; $L "$@" > "$B/$n.txt" 2>&1; echo "    rc=$? $(date +%T)" | tee -a "$B/_progress.txt"; }
want() { [ $# -eq 1 ] && return 0; local s=$1; shift; for x in "$@"; do [ "$x" = "$s" ] && return 0; done; return 1; }
STEPS=("$@")
w() { [ ${#STEPS[@]} -eq 0 ] && return 0; local s=$1; for x in "${STEPS[@]}"; do [ "$x" = "$s" ] && return 0; done; return 1; }

w idle && step idle python3 -I perf/l1_server_boot.py --map $MAP --runs 3 --idle 60 --perf-phases --tag idle
w micro && step micro bash -c "mkdir -p out/perf/$PERF_STAMP/micro && godot --headless --path godot/shooter -- --role server --perf-micro --perf-out $PWD/out/perf/$PERF_STAMP/micro --map $MAP --port 28900 > /dev/null 2>&1; python3 -I perf/show_micro.py out/perf/$PERF_STAMP/micro"
w n2 && step N2 env PERF_PHASES=1 python3 -I godot/shooter/run_loss_test.py --map $MAP --scenarios clean,rtt100,rtt200,loss10 --bots 16 --measure 30 --out $PWD/out/perf/$PERF_STAMP/N2-loss --port 28910
w n8 && step N8 python3 -I perf/run_load.py --tag N8-rr-streaks --streakers 16 --streaks uav,attack_missile,turret,care_package,attack_helicopter,helicopter_helper,ac130 --rr --streak-period 50 --duration 85 --warm 25 --port 28920 --map $MAP
w n3 && step N3-64p python3 -I perf/run_load.py --tag N3-64p --bots 48 --walkers 16 --duration 70 --warm 20 --port 28930 --map $MAP
w n3b && step N3-96p python3 -I perf/run_load.py --tag N3-96p --bots 64 --walkers 32 --duration 70 --warm 20 --port 28940 --map $MAP
P=28950
w w2 && for L2 in 0 0.03 0.10; do P=$((P + 4)); step W2-loss$L2 python3 -I perf/run_wire.py --tag W2-loss$L2 --map $MAP --rtt 100 --loss $L2 --shooters 4 --walkers 12 --duration 60 --warm 10 --port $P; done
w b1 && step B1 python3 -I perf/run_lagcomp.py --map $MAP --tag B1-on --rtts 0,50,100,200 --walkers 8 --aimers 4 --duration 100 --port 28980
w b1off && step B1off python3 -I perf/run_lagcomp.py --map $MAP --tag B1-off --no-lagcomp --rtts 50,100,200 --walkers 8 --aimers 4 --duration 100 --port 29010
w c1 && for N in 2 4 8; do step C1-x$N python3 -I perf/run_pattern.py --map $MAP --patterns straight --clients $N --duration 40 --tag C1-pattern --port $((29040 + N)); done
w route && step Route-igpu python3 -I perf/run_route.py --map $MAP --variants base --views 48 --dwell 1.5 --gpu intel --gpu-index 0 --tag R4-route --repeat 3 --timeout 1500
w bench && step Bench-igpu python3 -I perf/run_route.py --map $MAP --variants base --views 12 --dwell 1.5 --gpu intel --gpu-index 0 --tag R4-bench --blasts 7 --timeout 1500
echo "=== ALL DONE $(date +%T)" | tee -a "$B/_progress.txt"

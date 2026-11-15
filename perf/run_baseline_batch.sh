#!/usr/bin/env bash
# The remaining baseline measurements, sequential (one at a time so they do not disturb each other). Each step logs to out/perf/<stamp>/_batch/<name>.txt.
# Usage: PERF_STAMP=<stamp> bash perf/run_baseline_batch.sh
set -u
cd "$(dirname "$0")/.."
: "${PERF_STAMP:?set PERF_STAMP}"
export PERF_STAMP
B=out/perf/$PERF_STAMP/_batch
mkdir -p "$B"
MAP=$PWD/out/village/map
step() { local n=$1; shift; echo "=== $n $(date +%T)" | tee -a "$B/_progress.txt"; "$@" > "$B/$n.txt" 2>&1; echo "    rc=$? $(date +%T)" | tee -a "$B/_progress.txt"; }

# B1: lag compensation, 4 aimers, 100 s per latency
step B1v2 python3 -I perf/run_lagcomp.py --tag B1v2 --rtts 0,50,100,200 --walkers 8 --aimers 4 --duration 100 --port 27630

# W2: reliable event latency and tick completion under real UDP loss, RTT 100
P=27700
for L in 0 0.01 0.03 0.05 0.10; do
  P=$((P + 4))
  step W2-loss$L python3 -I perf/run_wire.py --tag W2-loss$L --rtt 100 --loss $L --shooters 4 --walkers 12 --duration 60 --warm 10 --port $P
done
# H-03: tick completion vs entity count at 3 % loss
for BOTS in 0 24 56; do
  step W3-bots$BOTS python3 -I perf/run_wire.py --tag W3-bots$BOTS-loss0.03 --rtt 100 --loss 0.03 --bots $BOTS --shooters 4 --walkers 12 --duration 60 --warm 10 --port $((27800 + BOTS))
done

# R: client frame, NVIDIA
step R-nvidia python3 -I perf/run_route.py --variants base,msaa0,msaa2,msaa8,noshadow,shadow100,mobile,compat --views 48 --dwell 1.5 --gpu nvidia --tag R1-route
# R: client frame, Intel iGPU (the stress case)
step R-intel python3 -I perf/run_route.py --variants base,msaa0 --views 48 --dwell 1.5 --gpu intel --tag R1-route --timeout 1500

# N2 repeats (variance) and N3 scale
for I in 2 3; do
  step N2-run$I env PERF_PHASES=1 python3 -I godot/shooter/run_loss_test.py --map $MAP --scenarios clean,rtt100 --bots 16 --measure 30 --out $PWD/out/perf/$PERF_STAMP/N2-loss-run$I --port $((27900 + I))
done
for N in 32 64; do
  step N3-bots$N env PERF_PHASES=1 python3 -I godot/shooter/run_loss_test.py --map $MAP --scenarios clean --bots $N --shooters 8 --measure 30 --out $PWD/out/perf/$PERF_STAMP/N3-loss-bots$N --port $((27950 + N))
done
echo "=== ALL DONE $(date +%T)" | tee -a "$B/_progress.txt"

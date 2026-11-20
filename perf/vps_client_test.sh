#!/usr/bin/env bash
# The client load test (docs/perf-investigation/client-64-bots.md): a real client with a window on this machine plays by script
# (--perf-play: walks, turns, jumps, fires) in a fight of N bots on the live VPS, and its "client: perf" lines are saved. The frame cap and
# vsync are off, so the numbers are what a frame costs. It RESTARTS THE LIVE SERVER twice: run it when nobody plays.
#
#   perf/vps_client_test.sh <label>          # log in out/perf/client/<UTC stamp>-<label>.log, summary printed
#   BOTS=32 PLAY_SECONDS=600 GPU=intel perf/vps_client_test.sh after-fix
#   EXPORT=1 perf/vps_client_test.sh <label> # export the client of this checkout first (also done when there is none)
#
# The window opens with WORKSPACE=6 (the development machine's window manager puts it on the hidden workspace); set WORKSPACE to choose another.
#
# The client is this checkout's committed code, exported to out/perf/client-build (again whenever HEAD moved), with this checkout's map
# (MAPDIR, default out/village/map: it must be the server's build), assets and assets-private. The server must know the client's protocol
# version. EXPORT_ONLY=1 stops after the export. The graphics card's state is kept per second (<log>.gpu) and summed in the "# gpu:" line:
# compare render_gpu_ms only between runs with the same capped_pct. Needs: the ssh key (VPS_KEY), godot with export templates.
set -euo pipefail
label=${1:?usage: perf/vps_client_test.sh <label>}
BOTS=${BOTS:-64}
PLAY=${PLAY_SECONDS:-330}
RES=${RES:-1920x1080}
GPU=${GPU:-nvidia}
VPS=${VPS:-root@159.195.244.164}
VPS_KEY=${VPS_KEY:-$HOME/.ssh/drycastle-vps}
here=$(cd "$(dirname "$0")/.." && pwd)
MAPDIR=${MAPDIR:-$here/out/village/map}
dir=${BUILD:-$here/out/perf/client-build}   # BUILD=<a kept copy of this folder> EXPORT=0 runs an earlier commit's export again; the test's own export: never the release export in out/shooter, whose map and assets are copies that go stale
CLIENT=$dir/drycastle-shooter.x86_64
out=$here/out/perf/client; mkdir -p "$out"
log=$out/$(date -u +%Y%m%dT%H%M%SZ)-$label.log
ssh_vps() { ssh -i "$VPS_KEY" -o IdentitiesOnly=yes -o BatchMode=yes "$VPS" "$@"; }

# The export is of the committed code: its commit is kept beside it and it is made again when HEAD moved. A changed godot/ tree is refused
# (the export would be of half-made work, and it resets godot/shooter/ui); EXPORT=1 exports again regardless, EXPORT=0 keeps what is there.
head=$(git -C "$here" rev-parse HEAD)
if [ "${EXPORT:-}" != 0 ] && { [ -n "${EXPORT:-}" ] || [ ! -x "$CLIENT" ] || [ "$(cat "$dir/commit" 2>/dev/null)" != "$head" ]; }; then
  git -C "$here" diff --quiet HEAD -- godot || { echo "godot/ has uncommitted changes: commit them first (the test measures a commit)" >&2; exit 1; }
  echo "exporting the client of $(git -C "$here" rev-parse --short HEAD)"
  mkdir -p "$dir"; rm -f "$dir/commit"
  godot --headless --path "$here/godot/shooter" --export-release "Linux Client" "$CLIENT" > "$out/export.log" 2>&1 || { tail -20 "$out/export.log" >&2; exit 1; }
  git -C "$here" checkout -q -- godot/shooter/ui 2>/dev/null || true  # the export rewrites generated UI files
  [ -x "$CLIENT" ] || { tail -20 "$out/export.log" >&2; exit 1; }
  echo "$head" > "$dir/commit"
fi
ln -sfn "$MAPDIR" "$dir/map"; ln -sfn "$here/assets" "$dir/assets"; ln -sfn "$here/assets-private" "$dir/assets-private"
[ -x "$CLIENT" ] && [ -e "$dir/map/manifest.json" ] || { echo "no client at $CLIENT, or no map at $MAPDIR" >&2; exit 1; }
[ -n "${EXPORT_ONLY:-}" ] && { echo "exported: $CLIENT"; exit 0; }

if ssh_vps 'cd /opt/drycastle/deploy && docker compose logs server --since 2m 2>/dev/null | grep "server: stats" | tail -1 | grep -q "players [1-9]"'; then
  echo "somebody is playing: not restarting the server" >&2; exit 1
fi
if ssh_vps 'test -e /opt/drycastle/deploy/docker-compose.override.yml'; then
  echo "an override file is already on the VPS (another test, or one that did not clean up): look at it first" >&2; exit 1
fi

restore() {
  echo "restoring the server (no bots)"
  ssh_vps 'cd /opt/drycastle/deploy && rm -f docker-compose.override.yml && docker compose up -d --wait 2>&1 | tail -1'
}
trap restore EXIT

echo "restarting the server with $BOTS bots"
ssh_vps "cd /opt/drycastle/deploy && cat > docker-compose.override.yml <<OVR
services:
  server:
    command: [\"--allow-sandbox\", \"--unlock-all\", \"--admin-token\", \"\\\${ADMIN_TOKEN:-}\", \"--bots\", \"$BOTS\"]
OVR
docker compose up -d --wait 2>&1 | tail -1"
until ssh_vps 'cd /opt/drycastle/deploy && docker compose logs server --since 10m 2>/dev/null | grep -q "bot navigation from"'; do sleep 5; done
sleep 10

echo "client in for $PLAY s ($RES, gpu $GPU)"
profile=$(mktemp -d)  # a new install: the game's default settings
gpuenv=()
[ "$GPU" = nvidia ] && gpuenv=(__NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia __VK_LAYER_NV_optimus=NVIDIA_only DRI_PRIME=1)
smi=
if [ "$GPU" = nvidia ] && command -v nvidia-smi > /dev/null; then  # a capped card (hot laptop) draws the same frame in twice the time: kept per second
  nvidia-smi --query-gpu=power.draw,clocks.gr,utilization.gpu,temperature.gpu,clocks_event_reasons.active,pstate --format=csv,noheader,nounits -l 1 > "$log.gpu" 2> /dev/null & smi=$!
fi
( cd "$dir" && timeout $((PLAY + 240)) env XDG_DATA_HOME="$profile" WORKSPACE="${WORKSPACE:-6}" "${gpuenv[@]}" "$CLIENT" --resolution "$RES" --windowed -- --role client \
    --connect "${VPS#*@}:27015" --name perf-client --perf-play --perf-seconds "$PLAY" --perf-res "$RES" > "$log.full" 2>&1 ) || true  # it may crash on exit, after the play
rm -rf "$profile"
[ -z "$smi" ] || kill "$smi" 2> /dev/null || true

{
  echo "# client load test: $label; $(date -u +%FT%TZ); commit $(cut -c1-8 "$dir/commit"); bots $BOTS; $RES; gpu env $GPU"
  [ ! -s "$log.gpu" ] || awk -F', ' '$3 > 50 { n++; w += $1; c += $2; if ($4 > t) t = $4; if (strtonum($5) % 128 >= 4) cap++ }
    END { if (n) printf "# gpu: power_w=%.1f clock_mhz=%.0f temp_max_c=%d capped_pct=%.0f busy_s=%d\n", w / n, c / n, t, 100 * cap / n, n }' "$log.gpu"
  echo "# cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs); adapter: $(grep -m1 -oE 'Vulkan [0-9.]+ - .*|OpenGL API .*' "$log.full" || echo '?')"
  grep -aE '^client: perf (fps=|scopes |events )' "$log.full" || true
} > "$log"
ssh_vps "cd /opt/drycastle/deploy && docker compose logs server --since $((PLAY / 60 + 3))m 2>/dev/null | grep -E 'server: (stats|prof)'" > "$log.server" || true
[ "$(grep -c "^client: perf fps=" "$log")" -ge $((PLAY / 10 - 2)) ] || echo "the client did not reach the end of its play: see $log.full" >&2
echo "kills the client saw: $(grep -c 'kill feed' "$log.full" || true)"
echo "log: $log"
python3 "$here/perf/client_summary.py" "$log"

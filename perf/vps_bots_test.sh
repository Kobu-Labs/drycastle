#!/usr/bin/env bash
# The 64-bot load test on the live VPS (docs/perf-investigation/vps-64-bots.md): restarts the server with --bots N, joins one headless
# walker (bots only play while a human is in), lets them fight, saves the server's "server: stats" / "server: prof" lines, and puts
# the server back as it was. It RESTARTS THE LIVE SERVER twice: run it when nobody plays.
#
#   perf/vps_bots_test.sh <label>            # log in out/perf/vps/<UTC stamp>-<label>.log, summary printed
#   BOTS=32 PLAY_SECONDS=600 perf/vps_bots_test.sh after-fix
#
# Needs: the ssh key (VPS_KEY), and an installed client of the server's protocol version (CLIENT, default the launcher's).
set -euo pipefail
label=${1:?usage: perf/vps_bots_test.sh <label>}
BOTS=${BOTS:-64}
SECONDS_PLAYED=${PLAY_SECONDS:-330}
VPS=${VPS:-root@159.195.244.164}
VPS_KEY=${VPS_KEY:-$HOME/.ssh/drycastle-vps}
CLIENT=${CLIENT:-$HOME/.local/share/Drycastle/game/drycastle-shooter.x86_64}
here=$(cd "$(dirname "$0")/.." && pwd)
out=$here/out/perf/vps; mkdir -p "$out"
log=$out/$(date -u +%Y%m%dT%H%M%SZ)-$label.log
ssh_vps() { ssh -i "$VPS_KEY" -o IdentitiesOnly=yes -o BatchMode=yes "$VPS" "$@"; }

[ -x "$CLIENT" ] || { echo "no client at $CLIENT" >&2; exit 1; }
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
# the server answers nobody until the bots' navigation is in (0.3 s from the cache, 45 s when it bakes)
until ssh_vps 'cd /opt/drycastle/deploy && docker compose logs server --since 10m 2>/dev/null | grep -q "bot navigation from"'; do sleep 5; done
sleep 10

echo "walker in for $SECONDS_PLAYED s"
profile=$(mktemp -d)
( cd "$(dirname "$CLIENT")" && XDG_DATA_HOME=$profile "$CLIENT" --headless -- --role client --headless-bot --connect "${VPS#*@}:27015" \
    --name perf-walker --loss-bot walker --loss-measure 900 --bot-seconds "$SECONDS_PLAYED" > "$log.walker" 2>&1 ) || true  # it may crash on exit, after the play
rm -rf "$profile"

ssh_vps "cd /opt/drycastle/deploy && docker compose logs server --since $((SECONDS_PLAYED / 60 + 3))m 2>/dev/null | grep -E 'server: (stats|prof|bot navigation)'" > "$log"
echo "kill-feed lines the walker saw: $(grep -c 'kill feed' "$log.walker" || true) (0: the bots did not fight, the run is void)"
echo "log: $log"
python3 "$here/perf/vps_summary.py" "$log"

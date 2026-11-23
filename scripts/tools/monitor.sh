#!/usr/bin/env bash
# Opens a Grafana dashboard of the VPS, the server's by default (docs/monitoring.md): an ssh tunnel to the VPS's loopback port and the browser on it. Ctrl+C closes the tunnel.
#   make monitor            (or scripts/tools/monitor.sh; VPS, VPS_KEY, PORT to override)
#   make monitor-clients    (scripts/tools/monitor.sh clients): the players' dashboard
set -euo pipefail
VPS=${VPS:-root@159.195.244.164}
VPS_KEY=${VPS_KEY:-$HOME/.ssh/drycastle-vps}
PORT=${PORT:-3300}
board=drycastle-${1:-server}
url="http://localhost:$PORT/d/$board/$board?kiosk=0"
( for _ in $(seq 1 40); do curl -s -o /dev/null "http://localhost:$PORT/api/health" && break; sleep 0.25; done
  echo "dashboard: $url   (Ctrl+C here closes it)"
  (xdg-open "$url" || open "$url") >/dev/null 2>&1 || true ) &
exec ssh -i "$VPS_KEY" -o IdentitiesOnly=yes -o ExitOnForwardFailure=yes -N -L "$PORT:127.0.0.1:3000" "$VPS"

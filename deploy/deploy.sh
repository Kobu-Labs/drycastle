#!/usr/bin/env bash
# Copies the server export and the compose files to a VPS and (re)starts them.
# Usage: [SSH_OPTS="-i ~/.ssh/key -o IdentitiesOnly=yes"] deploy/deploy.sh user@host [remote_dir]     (default remote_dir: /opt/drycastle)
# Needs on the VPS: Docker with the compose plugin (curl -fsSL https://get.docker.com | sh) and rsync.
# Run `make shooter-export` first. The first run creates .env on the VPS; edit POSTGRES_PASSWORD there, then run again.
set -euo pipefail
host="${1:?usage: deploy/deploy.sh user@host [remote_dir]}"
dir="${2:-/opt/drycastle}"
here="$(cd "$(dirname "$0")" && pwd)"
SSH="ssh ${SSH_OPTS:-}"
export_dir="$here/../out/shooter/linux-server"

[ -x "$export_dir/drycastle-server.x86_64" ] || { echo "no server export: run 'make shooter-export' first"; exit 1; }
[ -d "$export_dir/map" ] || { echo "export has no map/: run 'make map AREA=village' and export again"; exit 1; }

$SSH "$host" "mkdir -p '$dir/out/shooter/linux-server' '$dir/deploy'"
rsync -az -e "$SSH" --delete "$export_dir/" "$host:$dir/out/shooter/linux-server/"
rsync -az -e "$SSH" "$here/Dockerfile" "$here/docker-compose.yml" "$here/.env.example" "$host:$dir/deploy/"

$SSH "$host" "cd '$dir/deploy' && if [ ! -f .env ]; then cp .env.example .env && sed -i \"s/change-me/\$(openssl rand -hex 24)/\" .env && echo 'created .env with a random database password'; fi && docker compose up -d --build --wait && docker compose ps && docker image prune -f"
echo "deployed (server healthy: map loaded, listening). Open UDP 27015 in the firewall. Logs: ssh $host 'cd $dir/deploy && docker compose logs -f server'"

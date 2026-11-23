#!/usr/bin/env bash
# Builds, encrypts and (with a host) uploads the private asset pack, and writes private-pack.json, which the next release puts in manifest.json
# so launchers with the pack password fetch it. Run it BEFORE releasing, whenever assets-private/ changed; then commit private-pack.json.
#
#   scripts/release/publish-private-pack.sh                      build + encrypt only (out/private-pack/), nothing leaves this machine
#   [SSH_OPTS="-i ~/.ssh/key -o IdentitiesOnly=yes"] scripts/release/publish-private-pack.sh user@host [remote_dir]      (default remote_dir: /opt/drycastle)
#
# Uploads two things to the VPS (never to GitHub):
#   <remote_dir>/pack/assets-private-<id>.zip.enc   the encrypted pack, served over plain HTTP by the `pack` service of deploy/docker-compose.yml
#                                                    (TCP PACK_PORT, default 27080: open it in the firewall). The last three are kept.
#   <remote_dir>/assets-private/                     the same files unencrypted, mounted into the game server (collision of the pack's models,
#                                                    animal hit shapes). The server reads them at start: the next deploy restarts it.
# The password is PACK_PASSWORD, else the first line of assets-private/pack-password.txt (created with a random one on the first run; gitignored
# with the folder). Give it to the players; it never travels: the launcher uses it to decrypt (godot/LauncherCore/PackCrypto.cs).
# A new password: run this again with it and release; installs that already have this pack keep it, everyone else needs the new password.
# PACK_URL_BASE overrides http://<host>:<PACK_PORT>.
set -euo pipefail
cd "$(dirname "$0")/../.."
host="${1:-}"
dir="${2:-/opt/drycastle}"
port="${PACK_PORT:-27080}"
iter=600000
out=out/private-pack

python3 -I scripts/release/make_private_pack.py
zip="$out/assets-private.zip"
plain_sha=$(sha256sum "$zip" | cut -d' ' -f1)
id="${plain_sha:0:12}"
name="assets-private-$id.zip.enc"

pwfile=assets-private/pack-password.txt
if [ -n "${PACK_PASSWORD:-}" ]; then pass=(-pass env:PACK_PASSWORD)
else
  if [ ! -s "$pwfile" ]; then openssl rand -base64 18 > "$pwfile"; chmod 600 "$pwfile"; echo "created $pwfile with a random password"; fi
  pass=(-pass "file:$pwfile")
fi

rm -f "$out"/assets-private-*.zip.enc
openssl enc -aes-256-cbc -pbkdf2 -md sha256 -iter "$iter" -salt -in "$zip" -out "$out/$name" "${pass[@]}"
# the file must decrypt back to the same zip, or players would download something nobody can open
back=$(openssl enc -d -aes-256-cbc -pbkdf2 -md sha256 -iter "$iter" -in "$out/$name" "${pass[@]}" | sha256sum | cut -d' ' -f1)
[ "$back" = "$plain_sha" ] || { echo "encrypted pack does not decrypt back to the zip"; exit 1; }
enc_sha=$(sha256sum "$out/$name" | cut -d' ' -f1)
size=$(stat -c %s "$out/$name")
echo "encrypted: $out/$name ($((size / 1048576)) MB, id $id)"

if [ -z "$host" ]; then echo "no host given: nothing uploaded, private-pack.json not written"; exit 0; fi

SSH="ssh ${SSH_OPTS:-}"
rm -rf "$out/tree" && mkdir -p "$out/tree" && unzip -q "$zip" -d "$out/tree"
$SSH "$host" "mkdir -p '$dir/pack' '$dir/assets-private'"
rsync -az --progress -e "$SSH" "$out/$name" "$host:$dir/pack/"
rsync -az -e "$SSH" --delete "$out/tree/" "$host:$dir/assets-private/"
$SSH "$host" "chmod -R a+rX '$dir/pack' '$dir/assets-private' && cd '$dir/pack' && ls -t assets-private-*.zip.enc | tail -n +4 | xargs -r rm -f"
rm -rf "$out/tree"

base="${PACK_URL_BASE:-http://${host#*@}:$port}"
python3 - "$id" "$name" "$base/$name" "$enc_sha" "$size" "$plain_sha" "$iter" <<'PY'
import json, sys
i, name, url, sha, size, plain, it = sys.argv[1:]
json.dump({"id": i, "plain_sha256": plain, "iterations": int(it), "file": {"name": name, "url": url, "sha256": sha, "size": int(size)}},
          open("private-pack.json", "w"), indent=2)
open("private-pack.json", "a").write("\n")
PY
if curl -sfI --max-time 10 "$base/$name" > /dev/null; then echo "reachable: $base/$name"
else echo "NOT reachable yet: $base/$name (the pack service starts with the next deploy of deploy/docker-compose.yml; open TCP $port in the firewall)"; fi
echo "published pack $id; commit private-pack.json, then release (docs/release.md)"

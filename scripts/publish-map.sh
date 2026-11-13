#!/usr/bin/env bash
# Publishes the current village map as its own release (map-YYYYMMDD-<hash>) and points the game at it (map-version.txt).
# Run after `make map`. Commit map-version.txt afterwards; the next push to main ships it to players.
set -euo pipefail
cd "$(dirname "$0")/.."
make map-package
hash=$(sha256sum out/village/map.zip | cut -c1-8)
tag="map-$(date +%Y%m%d)-$hash"
gh release create "$tag" out/village/map.zip out/village/map.json --prerelease \
  --title "Map $tag" --notes "Village map archive for the launcher. Not a game release; the game picks it via map-version.txt."
echo "$tag" > map-version.txt
echo "published $tag; commit map-version.txt"

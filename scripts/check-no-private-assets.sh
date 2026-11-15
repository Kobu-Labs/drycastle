#!/usr/bin/env bash
# Fails when a paid asset is tracked by git: anything under assets-private/ or a pack linked into a Godot project (godot/*/Assets/),
# and Synty source files anywhere. The repo and its releases are public; the pack travels encrypted from the game host instead
# (scripts/publish-private-pack.sh). Run by CI on every PR and before every release build.
set -euo pipefail
cd "$(dirname "$0")/.."
bad=$(git ls-files | grep -E '^assets-private(/|$)|^godot/[^/]+/Assets/|(^|/)Polygon[A-Z][A-Za-z]*/|\.unitypackage$|(^|/)pack-password\.txt$' || true)
if [ -n "$bad" ]; then echo "paid assets are tracked (remove them from git AND rewrite the history that added them):"; echo "$bad"; exit 1; fi
echo "no paid assets tracked"

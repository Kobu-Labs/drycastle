#!/usr/bin/env bash
# One re-runnable batch: Synty SIMPLE Military (paid, private) -> humvee, scud, scud_missile, rocket_cart GLBs under assets-private/synty/glb/simple-military/.
#   scripts/military_kit/build_simple_military.sh [package.unitypackage] [assets-private dir]
# Defaults: ~/downloads/SIMPLE_Military_Unity_2021_3_v1_3_8.unitypackage and the main checkout's assets-private/ (it lives only there, never in a worktree).
# Needs: python3 + numpy + Pillow, godot (4.7+) on PATH. Nothing of the pack is committed; only these scripts are.
#   1 simple_military_extract.py   unitypackage -> assets-private/synty/simple-military/{fbx,textures,sources.json,project.godot}
#   2 godot --import, simple_military_parts.gd   FBX -> parts/*.glb (plain GLBs the model scripts read)
#   3 make_humvee_model.py, make_scud_model.py (also scud_missile.glb), make_rocket_cart_model.py   -> glb/simple-military/*.glb + materials.json + tex/
#      + docs/research/scud-models-facts.d/*.json (the facts the vehicles.json entries are written from)
#   4 simple_military_check.gd     reloads the GLBs like the game, checks node names, measures, tests the rack's rotation
#   5 simple_military_manifest.py  manifest rows (pack:synty-simple-military/<name>)
set -euo pipefail
pkg="${1:-$HOME/downloads/SIMPLE_Military_Unity_2021_3_v1_3_8.unitypackage}"
priv="${2:-/home/jjakubec/coding/drycastle/assets-private}"
here="$(cd "$(dirname "$0")" && pwd)"
proj="$priv/synty/simple-military"
out="$priv/synty/glb/simple-military"
noise='Leaked|leaked|instance_notify|~Dependency|^   at:|still in use|Pages in use|Image index|Resource file not found|Can.t open file|multiple bind'

python3 -I "$here/simple_military_extract.py" "$pkg" "$proj"
( cd "$proj" && godot --headless --import 2>&1 | grep -E "ERROR: .*(fbx|Parse)" || true )
( cd "$proj" && godot --headless --path . --script "$here/simple_military_parts.gd" 2>&1 | grep -v -E "$noise" )
python3 "$here/make_humvee_model.py" "$out/humvee.glb"
python3 "$here/make_scud_model.py" "$out"
python3 "$here/make_rocket_cart_model.py" "$out/rocket_cart.glb"
python3 "$here/make_sentry_turret_model.py" "$out/sentry_turret.glb"
( cd "$proj" && godot --headless --path . --script "$here/simple_military_check.gd" -- "$out" 2>&1 | grep -v -E "$noise" )
python3 -I "$here/simple_military_manifest.py" "$priv"

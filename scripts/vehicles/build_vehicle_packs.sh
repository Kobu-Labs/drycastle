#!/usr/bin/env bash
# One re-runnable batch (issue #325): POLYGON Battle Royale (Godot project zip) and POLYGON QuadBike (Unity package), both paid and private, into
# assets-private/synty/glb/battle-royale/ and glb/quadbike/. Nothing of the packs is committed; only these scripts are.
#   scripts/vehicles/build_vehicle_packs.sh [battle_royale.zip] [quadbike.unitypackage] [assets-private dir]
# Defaults: ~/downloads/POLYGON_BattleRoyale_Godot_4_5_1_v1_0_0.zip, ~/downloads/POLYGON_QuadBike_Unity_2022_3_v1_2_1.unitypackage and the main
# checkout's assets-private/ (it lives only there, never in a worktree). Needs python3 (stdlib only), godot (4.7+) on PATH. ~15 s.
#   1 unpack the zip (python zipfile, into a new empty dir; skipped when battle-royale/polygon-battle-royale-01 exists: delete it to re-unpack) + godot --import
#   2 scripts/packs/private_pack_glb.gd in that project: glb/battle-royale/<Prefab>.glb (191 prefabs, SM_Veh_* among them, parts as child nodes), materials.json, tex/, manifest "synty-battle-royale"
#   3 scripts/vehicles/vehicle_pack_variants.py: the vehicle atlases 02..06 (colour variants) into glb/battle-royale/tex + materials.json; Military Land_Vehicles_NN atlases added (add-only)
#   4 scripts/vehicles/quadbike_extract.py (unitypackage -> quadbike/{fbx,textures,sources.json}) + godot --import + scripts/vehicles/quadbike_export.gd: glb/quadbike/SM_Veh_QuadBike_01.glb, materials.json, tex/, manifest "synty-quadbike"
#   5 scripts/vehicles/vehicle_pack_check.gd: reloads the GLBs like the game, checks the part nodes and material names
#   6 scripts/vehicles/standardize_vehicle.py for every scripts/vehicles/specs/*.json (the contract-conforming GLBs, one per skin, in glb/vehicles-std/ + materials.json + manifest rows + docs/research/vehicle-facts.d/<id>.json), then scripts/vehicles/vehicle_std_check.gd reloads them. Needs numpy + Pillow. Step 6 can be run alone: `scripts/vehicles/build_vehicle_packs.sh --standardize-only`. Vehicles done: car_small (car), bike (motorcycle), quad (quad), truck (truck), harvester, cartel_van (Gang Warfare SM_Veh_Van_01, already imported by tools/build_gang.sh); a steering assembly, two wheels, exposed seats and no glass are config only (docs/standards/car-design.md B1)
set -euo pipefail
only_std=0
if [ "${1:-}" = "--standardize-only" ]; then only_std=1; shift; fi
zip="${1:-$HOME/downloads/POLYGON_BattleRoyale_Godot_4_5_1_v1_0_0.zip}"
pkg="${2:-$HOME/downloads/POLYGON_QuadBike_Unity_2022_3_v1_2_1.unitypackage}"
priv="${3:-/home/jjakubec/coding/drycastle/assets-private}"
here="$(cd "$(dirname "$0")" && pwd)"
br="$priv/synty/battle-royale"
qb="$priv/synty/quadbike"
noise='Leaked|leaked|instance_notify|~Dependency|^   at:|still in use|Pages in use|Image index|Resource file not found|Can.t open file|multiple bind'

if [ "$only_std" = 0 ]; then
  if [ ! -d "$br/polygon-battle-royale-01" ]; then
    mkdir -p "$br"
    python3 -I -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$zip" "$br"  # zipfile drops absolute and ".." members
  fi
  ( cd "$br/polygon-battle-royale-01" && godot --headless --import 2>&1 | grep -E "ERROR: .*Parse" || true )
  ( cd "$br/polygon-battle-royale-01" && godot --headless --path . --script "$here/../packs/private_pack_glb.gd" -- "$priv" 2>&1 | grep -v -E "$noise" )
  python3 -I "$here/vehicle_pack_variants.py" "$priv"

  python3 -I "$here/quadbike_extract.py" "$pkg" "$qb"
  ( cd "$qb" && godot --headless --import 2>&1 | grep -E "ERROR: .*(fbx|Parse)" || true )
  ( cd "$qb" && godot --headless --path . --script "$here/quadbike_export.gd" -- "$priv" 2>&1 | grep -v -E "$noise" )

  godot --headless --script "$here/vehicle_pack_check.gd" -- "$priv" 2>&1 | grep -v -E "$noise"
fi

facts=()
for cfg in "$here"/specs/*.json; do
  python3 "$here/standardize_vehicle.py" "$cfg" --private "$priv"
  facts+=("$here/../../docs/research/vehicle-facts.d/$(basename "$cfg")")
done
godot --headless --script "$here/vehicle_std_check.gd" -- "$priv" "${facts[@]}" 2>&1 | grep -v -E "$noise"

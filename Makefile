# AREA=slice (default): data/raw, data/work, out/map. AREA=village: data/village/..., out/village/map.
AREA ?= slice

.PHONY: editor setup test fetch normalise parcels rules terrain roads props export parcel-run region-run preview map viewer viewer-smoke viewer-edit-smoke viewer-regions-smoke viewer-editor-smoke viewer-panel-smoke viewer-region-editor-smoke viewer-objects-smoke mcp viewer-shot assets-check netspike netspike-quick lint shooter-server shooter-client shooter-test shooter-net-smoke shooter-match-smoke shooter-weapons-smoke shooter-bazooka-smoke shooter-hud-shots shooter-menu-shots shooter-car-smoke shooter-bike-smoke shooter-blast-smoke shooter-loss-test shooter-icons

setup:
	cd pipeline && uv sync

test:
	cd pipeline && uv run pytest

fetch:
	cd pipeline && uv run python -m suchohrad_pipeline fetch --area $(AREA)

normalise:
	cd pipeline && uv run python -m suchohrad_pipeline normalise --area $(AREA)

parcels:
	cd pipeline && uv run python -m suchohrad_pipeline parcels --area $(AREA) --report

rules:
	cd pipeline && uv run python -m suchohrad_pipeline rules --area $(AREA)

terrain:
	cd pipeline && uv run python -m suchohrad_pipeline terrain --area $(AREA)

roads:
	cd pipeline && uv run python -m suchohrad_pipeline roads --area $(AREA)

props:
	cd pipeline && uv run python -m suchohrad_pipeline props --area $(AREA)

export:
	cd pipeline && uv run python -m suchohrad_pipeline export --area $(AREA)

# PARCEL=P-0412 [GROUP=G-0001] [OUT=file.json] [GLB=file.glb]: fences and trees of one parcel as JSON
# (stdout by default), optionally also as a GLB with parcel_<ID>_props nodes
PARCEL_ARGS = $(if $(PARCEL),--parcel $(PARCEL),) $(if $(GROUP),--group $(GROUP),) $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb $(abspath $(GLB)),)

parcel-run:
	cd pipeline && uv run python -m suchohrad_pipeline parcel-run --area $(AREA) $(PARCEL_ARGS)

# [REGION=R-0001] [OUT=file.json] GLB=file.glb: meshes of the regions (none named: all) as a GLB with
# region_<ID> nodes (hot reload); the JSON summary lists the regions to replace
REGION_ARGS = $(if $(REGION),--region $(REGION),) $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb $(abspath $(GLB)),)

region-run:
	cd pipeline && uv run python -m suchohrad_pipeline region-run --area $(AREA) $(REGION_ARGS)

EXAG ?= 3

preview:
	cd pipeline && uv run python -m suchohrad_pipeline preview --area $(AREA) --exaggeration $(EXAG)

map:
	cd pipeline && uv run python -m suchohrad_pipeline run-all --area $(AREA)

# MAP: map folder (default: SUCHOHRAD_MAP env var, else ../../out/map relative to godot/viewer)
MAP_ARG = $(if $(MAP),--map $(abspath $(MAP)),)
SEED ?= 1

viewer:
	dotnet build godot/viewer/viewer.csproj
	godot --path godot/viewer -- $(MAP_ARG)

# Editor mode (see README "Editor mode"). MAP=... AREA=slice|village, optional EDITS=file STATE=file (scratch copies).
editor:
	dotnet build godot/viewer/viewer.csproj
	godot --path godot/viewer -- --editor --area $(AREA) $(MAP_ARG) $(if $(EDITS),--edits $(abspath $(EDITS)),) $(if $(STATE),--state $(abspath $(STATE)),)

viewer-smoke:
	dotnet build godot/viewer/viewer.csproj
	godot --headless --path godot/viewer -- --smoke --seed $(SEED) $(MAP_ARG)

# Hot reload end to end: exports a fresh slice map into a scratch dir (nothing under data/ or out/ is touched),
# runs the viewer headless with --watch and checks that edits show up. Needs uv, dotnet and godot.
viewer-edit-smoke:
	python3 godot/viewer/edit_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Region hot reload end to end (headless): exports a slice map with regions into a scratch dir, then recolours, reshapes,
# adds and removes regions (also together with a parcel edit) through the watcher and checks the swaps, that nothing
# leaks and that a broken file changes nothing. Needs uv, dotnet and godot.
viewer-regions-smoke:
	python3 godot/viewer/regions_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Editor mode end to end (headless): exports a slice map into a scratch dir, drives the editor APIs (picking,
# selection, groups, edits.json writes, outlines, editor-state.json, F2 round trip). Needs uv, dotnet and godot.
viewer-editor-smoke:
	python3 godot/viewer/editor_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Fence and Trees panel end to end (headless): drives the panel API on a scratch export (Apply, pending state, gates,
# groups, reroll, watcher round trip) and checks the saved files with the Python validator. SHOTS=dir adds two
# windowed screenshots of the panel (needs a display). Needs uv, dotnet and godot.
viewer-panel-smoke:
	python3 godot/viewer/panel_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),) $(if $(SHOTS),--shots $(SHOTS),)

# Region tool end to end (headless): exports a slice map with one region into a scratch dir, drives drawing, closing, colour,
# corner drags (valid and self-crossing), recolour, delete with confirm, undo/redo, Esc levels and the Ctrl+P palette through
# the editor APIs and key events, and compares edits.json after every step with the Python writer's text. Needs uv, dotnet and godot.
viewer-region-editor-smoke:
	python3 godot/viewer/region_editor_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Objects, gizmo and session undo end to end (headless): exports a slice map into a scratch dir, drives placement, drag,
# rotate, cross-parcel moves, field edits, duplicate/delete, group + fence Apply and undo/redo through the editor APIs,
# asserts the exact edits.json text of every step, that object edits run no parcel-run, and checks the saved files with
# the Python validator. SHOTS=dir adds two windowed screenshots (needs a display). Needs uv, dotnet and godot.
viewer-objects-smoke:
	python3 godot/viewer/objects_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),) $(if $(SHOTS),--shots $(SHOTS),)

# MCP server (stdio) for parcel edits, see docs/mcp.md. Claude Code normally starts it itself; this is for manual runs.
# EDITS=file STATE=file ASSETS=file override the defaults. AREA=slice|village picks the map work files.
MCP_ARGS = $(if $(EDITS),--edits $(abspath $(EDITS)),) $(if $(STATE),--state $(abspath $(STATE)),) $(if $(ASSETS),--assets $(abspath $(ASSETS)),)

mcp:
	cd pipeline && uv run python -m suchohrad_pipeline.mcp_server --area $(AREA) $(MCP_ARGS)

# Loads every glTF in assets/manifest.json with GLTFDocument (headless); exits nonzero on failure.
assets-check:
	godot --headless --path godot/viewer --script res://asset_check.gd -- --manifest $(abspath assets/manifest.json)

# Windowed run; writes PNGs to out/screens/
viewer-shot:
	dotnet build godot/viewer/viewer.csproj
	mkdir -p out/screens
	godot --path godot/viewer --resolution 1600x900 -- --view overview --screenshot $(abspath out/screens)/overview.png $(MAP_ARG)
	godot --path godot/viewer --resolution 1600x900 -- --view oblique --screenshot $(abspath out/screens)/oblique.png $(MAP_ARG)
	godot --path godot/viewer --resolution 1600x900 -- --view street --screenshot $(abspath out/screens)/street.png $(MAP_ARG)
	godot --path godot/viewer --resolution 1600x900 -- --view player --seed $(SEED) --screenshot $(abspath out/screens)/player.png $(MAP_ARG)

# 64-client netcode spike (see godot/netspike/README in the main README). Needs a map folder:
# MAP=/path/to/out/map or SUCHOHRAD_MAP. Results go to out/netspike/<timestamp>/.
NETSPIKE_MAP = $(if $(MAP),--map $(abspath $(MAP)),)

netspike:
	python3 godot/netspike/run_spike.py $(NETSPIKE_MAP)

netspike-quick:
	python3 godot/netspike/run_spike.py --quick $(NETSPIKE_MAP)

# Shooter v1 (godot/shooter + godot/ShooterCore). MAP=/path/to/out/map or SUCHOHRAD_MAP.
# shooter-server runs headless; add SMOKE=1 to boot, load and exit cleanly.
SHOOTER_MAP = $(if $(MAP),--map $(abspath $(MAP)),)
# Dev mode (issue #149): every weapon, perk, killstreak and level-gated item unlocked on the dev servers. Nothing is saved
# to the database. Release servers are never started with it (see docs/shooter-server.md). UNLOCK_ALL=0 turns it off.
UNLOCK_ALL ?= 1
UNLOCK_ARG = $(if $(filter 1 true,$(UNLOCK_ALL)),--unlock-all,)
UNLOCK_ENV = SUCHOHRAD_UNLOCK_ALL=$(UNLOCK_ALL)

# Loadout and HUD icons (#146): weapon renders need a display (DISPLAY); the rest is ImageMagick + Pillow.
shooter-icons:
	godot --path godot/shooter --script res://tools/render_icons.gd
	python3 godot/shooter/tools/make_icons.py

# TARGETS: stationary target dummies planted for testing (issue #148); 0 turns them off. docs/shooter-server.md.
TARGETS ?= 24

shooter-server:
	dotnet build godot/shooter/shooter.csproj
	godot --headless --path godot/shooter -- --role server --targets $(TARGETS) $(if $(SMOKE),--smoke,) $(UNLOCK_ARG) $(SHOOTER_MAP)

shooter-client:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --role client $(if $(SMOKE),--smoke,) $(SHOOTER_MAP)

# Headless server + 2 scripted bot clients: asserts both move and see each other.
shooter-net-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_net_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

# Headless server (kill goal 3, short timers) + alice/bob bots: kills, spawn protection, respawn, round end and restart.
shooter-match-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_match_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

# Headless server (flat test arena + wall) + alice/bob bots: AK damage and headshot, switch time, wall block, reload timing, knife backstab, ammo refill on kill.
shooter-weapons-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_weapons_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

# Same flat arena: bazooka rocket vs wall, splash falloff, direct hit, self damage and knockback, explosion events, reload and ammo refill.
shooter-bazooka-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_bazooka_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

shooter-car-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_car_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

shooter-blast-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_blast_smoke.py $(if $(MAP),--map $(abspath $(MAP)),)

shooter-bike-smoke:
	dotnet build godot/shooter/shooter.csproj
	$(UNLOCK_ENV) python3 godot/shooter/run_car_smoke.py --kind motorcycle bicycle $(if $(MAP),--map $(abspath $(MAP)),)

# Netcode loss/latency gate (issue #93, docs/spikes/shooter-loss-test.md): one server + 16 headless bots per scenario, about 2 min each.
# SCENARIOS=clean,rtt100 (also rtt200, loss10 or name=latency_ms=..,jitter_ms=..,loss=..), BOTS=16, MEASURE=30 (seconds). Results in out/shooter-loss/<timestamp>/.
shooter-loss-test:
	dotnet build godot/shooter/shooter.csproj
	python3 godot/shooter/run_loss_test.py $(if $(MAP),--map $(abspath $(MAP)),) $(if $(SCENARIOS),--scenarios $(SCENARIOS),) $(if $(BOTS),--bots $(BOTS),) $(if $(MEASURE),--measure $(MEASURE),)

shooter-test:
	dotnet test godot/ShooterCore.Tests/ShooterCore.Tests.csproj

lint:
	cd pipeline && uv run ruff check .

# HUD screenshots with fake data at 1280x720 and 3840x2160 (needs a display): make shooter-hud-shots DIR=out/hud
shooter-hud-shots:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --hud-shots $(abspath $(or $(DIR),out/hud))

# Menu screenshots (main, connect, settings tabs, pause, loading) at 1280x720 and 3840x2160 (needs a display): make shooter-menu-shots DIR=out/menu
shooter-menu-shots:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --menu-shots $(abspath $(or $(DIR),out/menu))

# Export presets (godot/shooter/export_presets.cfg) into out/shooter/. Needs the Godot 4.7.2 mono export templates
# (Editor > Manage Export Templates); not downloaded here. The map folder is NOT bundled; assets/ is copied as a sidecar.
.PHONY: shooter-export shooter-export-check
SHOOTER_TEMPLATES = $(HOME)/.local/share/godot/export_templates/4.7.2.stable.mono

shooter-export-check:
	@test -f godot/shooter/export_presets.cfg || { echo "missing godot/shooter/export_presets.cfg"; exit 1; }
	@grep -q 'include_filter="data/\*.json"' godot/shooter/export_presets.cfg || { echo "export_presets.cfg does not pack data/*.json (#133)"; exit 1; }
	@for f in weapons match vehicles sounds perks levels killstreaks turret environment camo; do test -f godot/shooter/data/$$f.json || { echo "missing godot/shooter/data/$$f.json"; exit 1; }; done
	@echo "$$(grep -c '^name=' godot/shooter/export_presets.cfg) export presets found"
	@if [ -d "$(SHOOTER_TEMPLATES)" ] && [ -n "$$(ls -A $(SHOOTER_TEMPLATES) 2>/dev/null)" ]; then echo "export templates: OK ($(SHOOTER_TEMPLATES))"; \
	else echo "export templates: MISSING at $(SHOOTER_TEMPLATES) (install the 4.7.2 mono templates: Editor > Manage Export Templates)"; exit 1; fi

shooter-export: shooter-export-check
	dotnet build godot/shooter/shooter.csproj
	mkdir -p out/shooter/windows/assets out/shooter/linux-client/assets out/shooter/linux-server/assets
	godot --headless --path godot/shooter --export-release "Windows Client"
	godot --headless --path godot/shooter --export-release "Linux Client"
	godot --headless --path godot/shooter --export-release "Linux Server"
	for d in windows linux-client linux-server; do cp -r assets/. out/shooter/$$d/assets/; done
	@echo "exported to out/shooter/. Run with SUCHOHRAD_ASSETS=<dir>/assets and the map via --map or SUCHOHRAD_MAP (see docs/shooter-server.md)"

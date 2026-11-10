# AREA=slice (default): data/raw, data/work, out/map. AREA=village: data/village/..., out/village/map.
AREA ?= slice

.PHONY: setup test fetch normalise parcels rules terrain roads props export parcel-run preview map viewer viewer-smoke viewer-edit-smoke viewer-editor-smoke viewer-panel-smoke mcp viewer-shot assets-check netspike netspike-quick lint

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

viewer-smoke:
	dotnet build godot/viewer/viewer.csproj
	godot --headless --path godot/viewer -- --smoke --seed $(SEED) $(MAP_ARG)

# Hot reload end to end: exports a fresh slice map into a scratch dir (nothing under data/ or out/ is touched),
# runs the viewer headless with --watch and checks that edits show up. Needs uv, dotnet and godot.
viewer-edit-smoke:
	python3 godot/viewer/edit_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Editor mode end to end (headless): exports a slice map into a scratch dir, drives the editor APIs (picking,
# selection, groups, edits.json writes, outlines, editor-state.json, F2 round trip). Needs uv, dotnet and godot.
viewer-editor-smoke:
	python3 godot/viewer/editor_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Fence and Trees panel end to end (headless): drives the panel API on a scratch export (Apply, pending state, gates,
# groups, reroll, watcher round trip) and checks the saved files with the Python validator. SHOTS=dir adds two
# windowed screenshots of the panel (needs a display). Needs uv, dotnet and godot.
viewer-panel-smoke:
	python3 godot/viewer/panel_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),) $(if $(SHOTS),--shots $(SHOTS),)

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

lint:
	cd pipeline && uv run ruff check .

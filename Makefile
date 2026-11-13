# The map: the village inside its outline (pipeline/config/map.local.yaml), data/village/..., out/village/map.
# MAP: the map folder the viewer, editor and shooter targets load (override with MAP=<dir>).
MAP ?= out/village/map

.PHONY: rozhlas editor setup test fetch normalise parcels rules terrain roads props detail detail-rust-build export parcel-run region-run preview ortho ortho-trees poles-preview poles-manifest floodwall-preview floodwall-manifest map viewer viewer-smoke viewer-edit-smoke viewer-regions-smoke viewer-editor-smoke viewer-panel-smoke viewer-region-editor-smoke viewer-objects-smoke mcp viewer-shot assets-check netspike netspike-quick lint shooter-server shooter-client shooter-client-sandbox shooter-test shooter-net-smoke shooter-match-smoke shooter-weapons-smoke shooter-bazooka-smoke shooter-hud-shots shooter-menu-shots shooter-intel-shots shooter-car-smoke shooter-bike-smoke shooter-blast-smoke shooter-loss-test shooter-icons

setup:
	cd pipeline && uv sync

test:
	cd pipeline && uv run pytest

fetch:
	cd pipeline && uv run python -m drycastle_pipeline fetch

normalise:
	cd pipeline && uv run python -m drycastle_pipeline normalise

parcels:
	cd pipeline && uv run python -m drycastle_pipeline parcels --report

rules:
	cd pipeline && uv run python -m drycastle_pipeline rules

terrain:
	cd pipeline && uv run python -m drycastle_pipeline terrain

roads:
	cd pipeline && uv run python -m drycastle_pipeline roads

props:
	cd pipeline && uv run python -m drycastle_pipeline props

# Ground detail: draped patches (dirt, gravel, leaf litter, beds, furrows, verges) and clutter instances (tufts, pebbles, ...)
# for every parcel; writes data/village/work/detail*.{json,npy}. Needs props. See docs/pipeline.md "Detail".
# Runs on the Rust engine only, which detail-rust-build builds first (an unchanged engine is a few seconds).
detail: detail-rust-build
	cd pipeline && uv run python -m drycastle_pipeline detail

# Builds the Rust engine of the detail stage (pipeline/rust/detail, PyO3, linked to the system GEOS: needs cargo and
# geos-devel, e.g. `sudo dnf install geos-devel`) and installs it into the pipeline's venv with maturin. `make setup` (uv sync) removes it again: build it again after.
detail-rust-build:
	cd pipeline && uv pip install --quiet maturin && uv run maturin develop --release --manifest-path rust/detail/Cargo.toml

export:
	cd pipeline && uv run python -m drycastle_pipeline export

# PARCEL=P-0412 [GROUP=G-0001] [OUT=file.json] [GLB=file.glb]: fences and trees of one parcel as JSON
# (stdout by default), optionally also as a GLB with parcel_<ID>_props nodes
PARCEL_ARGS = $(if $(PARCEL),--parcel $(PARCEL),) $(if $(GROUP),--group $(GROUP),) $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb $(abspath $(GLB)),)

parcel-run:
	cd pipeline && uv run python -m drycastle_pipeline parcel-run $(PARCEL_ARGS)

# [REGION=R-0001] [OUT=file.json] GLB=file.glb: meshes of the regions (none named: all) as a GLB with
# region_<ID> nodes (hot reload); the JSON summary lists the regions to replace
REGION_ARGS = $(if $(REGION),--region $(REGION),) $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb $(abspath $(GLB)),)

region-run:
	cd pipeline && uv run python -m drycastle_pipeline region-run $(REGION_ARGS)

EXAG ?= 3

preview:
	cd pipeline && uv run python -m drycastle_pipeline preview --exaggeration $(EXAG)

# Aerial photo of the village for the editor's "Aerial photo" layer, in data/village/ortho (reference only, never exported or
# shipped: its licence forbids redistributing and altering it). Needs `make fetch` first. ORTHO_LEVEL=12 is 0.3 m per pixel.
ORTHO_LEVEL ?= 11

ortho:
	cd pipeline && uv run python -m drycastle_pipeline.ortho --level $(ORTHO_LEVEL)

# Where the trees are, read from the photo (data/village/ortho/trees.json + canopy.png). Needs `make ortho` (and normalise) first.
# The first run trains the canopy model (a minute or two), later runs reuse it; RETRAIN=1 trains it again.
ortho-trees:
	cd pipeline && uv run python -m drycastle_pipeline.ortho_trees $(if $(RETRAIN),--retrain,)

# Pictures of the generated utility poles in out/poles (variants, random seeds, a wired row, the parts kit); GLB=1 also
# writes each placeable pole as a GLB. No map data needed. See docs/utility-poles.md.
poles-preview:
	cd pipeline && uv run python -m drycastle_pipeline.poles_preview $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb,)

# Rewrites the prim:pole_* and prim:connector_* entries of assets/manifest.json after the poles or connectors changed.
poles-manifest:
	cd pipeline && uv run python -m drycastle_pipeline.poles_preview --sync-manifest

# Pictures of the flood wall kit in out/floodwall (the pieces from both sides, a laid run, the access point); GLB=1 also
# writes each piece as a GLB. No map data needed. See docs/flood-wall.md.
floodwall-preview:
	cd pipeline && uv run python -m drycastle_pipeline.floodwall_preview $(if $(OUT),--out $(abspath $(OUT)),) $(if $(GLB),--glb,)

# Rewrites the prim:floodwall_* entries of assets/manifest.json after the kit changed.
floodwall-manifest:
	cd pipeline && uv run python -m drycastle_pipeline.floodwall_preview --sync-manifest

map: detail-rust-build
	cd pipeline && uv run python -m drycastle_pipeline run-all

MAP_ARG = $(if $(MAP),--map $(abspath $(MAP)),)
SEED ?= 1

viewer:
	dotnet build godot/viewer/viewer.csproj
	godot --path godot/viewer -- $(MAP_ARG)

# Editor mode (see README "Editor mode"). MAP=..., optional EDITS=file STATE=file (scratch copies).
editor:
	dotnet build godot/viewer/viewer.csproj
	godot --path godot/viewer -- --editor $(MAP_ARG) $(if $(EDITS),--edits $(abspath $(EDITS)),) $(if $(STATE),--state $(abspath $(STATE)),)

viewer-smoke:
	dotnet build godot/viewer/viewer.csproj
	godot --headless --path godot/viewer -- --smoke --seed $(SEED) $(MAP_ARG)

# Hot reload end to end: exports a fresh map into a scratch dir (nothing under data/ or out/ is touched),
# runs the viewer headless with --watch and checks that edits show up. Needs uv, dotnet and godot.
viewer-edit-smoke:
	python3 godot/viewer/edit_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Region hot reload end to end (headless): exports a map with regions into a scratch dir, then recolours, reshapes,
# adds and removes regions (also together with a parcel edit) through the watcher and checks the swaps, that nothing
# leaks and that a broken file changes nothing. Needs uv, dotnet and godot.
viewer-regions-smoke:
	python3 godot/viewer/regions_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Editor mode end to end (headless): exports a map into a scratch dir, drives the editor APIs (picking,
# selection, groups, edits.json writes, outlines, editor-state.json, F2 round trip). Needs uv, dotnet and godot.
viewer-editor-smoke:
	python3 godot/viewer/editor_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Fence panel end to end (headless): drives the panel API on a scratch export (Apply, pending state, gates,
# groups, watcher round trip) and checks the saved files with the Python validator. SHOTS=dir adds a
# windowed screenshot of the panel (needs a display). Needs uv, dotnet and godot.
viewer-panel-smoke:
	python3 godot/viewer/panel_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),) $(if $(SHOTS),--shots $(SHOTS),)

# Region tool end to end (headless): exports a map with one region into a scratch dir, drives drawing, closing, colour,
# corner drags (valid and self-crossing), recolour, delete with confirm, undo/redo, Esc levels and the Ctrl+P palette through
# the editor APIs and key events, and compares edits.json after every step with the Python writer's text. Needs uv, dotnet and godot.
viewer-region-editor-smoke:
	python3 godot/viewer/region_editor_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),)

# Objects, gizmo and session undo end to end (headless): exports a map into a scratch dir, drives placement, drag,
# rotate, cross-parcel moves, field edits, duplicate/delete, group + fence Apply and undo/redo through the editor APIs,
# asserts the exact edits.json text of every step, that object edits run no parcel-run, and checks the saved files with
# the Python validator. SHOTS=dir adds two windowed screenshots (needs a display). Needs uv, dotnet and godot.
viewer-objects-smoke:
	python3 godot/viewer/objects_smoke.py $(if $(SMOKE_DIR),--dir $(SMOKE_DIR),) $(if $(SHOTS),--shots $(SHOTS),)

# MCP server (stdio) for parcel edits, see docs/mcp.md. Claude Code normally starts it itself; this is for manual runs.
# EDITS=file STATE=file ASSETS=file override the defaults.
MCP_ARGS = $(if $(EDITS),--edits $(abspath $(EDITS)),) $(if $(STATE),--state $(abspath $(STATE)),) $(if $(ASSETS),--assets $(abspath $(ASSETS)),)

mcp:
	cd pipeline && uv run python -m drycastle_pipeline.mcp_server $(MCP_ARGS)

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
# MAP=/path/to/out/village/map or DRYCASTLE_MAP. Results go to out/netspike/<timestamp>/.
NETSPIKE_MAP = $(if $(MAP),--map $(abspath $(MAP)),)

netspike:
	python3 godot/netspike/run_spike.py $(NETSPIKE_MAP)

netspike-quick:
	python3 godot/netspike/run_spike.py --quick $(NETSPIKE_MAP)

# Shooter v1 (godot/shooter + godot/ShooterCore). MAP=/path/to/out/village/map or DRYCASTLE_MAP.
# shooter-server runs headless; add SMOKE=1 to boot, load and exit cleanly.
SHOOTER_MAP = $(if $(MAP),--map $(abspath $(MAP)),)
# Dev mode (issue #149): every weapon, perk, killstreak and level-gated item unlocked on the dev servers. Nothing is saved
# to the database. Release servers are never started with it (see docs/shooter-server.md). UNLOCK_ALL=0 turns it off.
UNLOCK_ALL ?= 1
UNLOCK_ARG = $(if $(filter 1 true,$(UNLOCK_ALL)),--unlock-all,)
UNLOCK_ENV = DRYCASTLE_UNLOCK_ALL=$(UNLOCK_ALL)

# Loadout and HUD icons (#146): weapon renders need a display (DISPLAY); the rest is ImageMagick + Pillow.
shooter-icons:
	godot --path godot/shooter --script res://tools/render_icons.gd
	python3 godot/shooter/tools/make_icons.py

# TARGETS: stationary target dummies planted for testing (issue #148); 0 turns them off. docs/shooter-server.md.
TARGETS ?= 24

# SANDBOX (issue #156): the dev server grants sandbox mode to clients that ask (godmode, unlimited ammo, every killstreak
# on demand, no XP or score). SANDBOX=0 turns the grant off. Release servers never pass --allow-sandbox (docs/shooter-server.md).
SANDBOX ?= 1
SANDBOX_ARG = $(if $(filter 1 true,$(SANDBOX)),--allow-sandbox,)

# ADMIN (chat commands /bots /spawn /time): the dev server makes every player an admin so no token is needed. ADMIN=0 turns it off
# (then pass --admin-token). Release servers never pass --admin-all (docs/shooter-v2.md).
ADMIN ?= 1
ADMIN_ARG = $(if $(filter 1 true,$(ADMIN)),--admin-all,)

# BOTS=N adds N bots while a player is in the match (#214); CLUSTER=1 keeps one dense cluster of spawns so they are met (they do not walk yet).
BOTS ?= 0
BOTS_ARG = $(if $(filter-out 0,$(BOTS)),--bots $(BOTS),) $(if $(filter 1 true,$(CLUSTER)),--cluster-spawns,)

shooter-server:
	dotnet build godot/shooter/shooter.csproj
	godot --headless --path godot/shooter -- --role server --targets $(TARGETS) $(if $(SMOKE),--smoke,) $(UNLOCK_ARG) $(SANDBOX_ARG) $(ADMIN_ARG) $(BOTS_ARG) $(SHOOTER_MAP)

shooter-client:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --role client $(if $(SMOKE),--smoke,) $(SHOOTER_MAP)

# Client that asks the dev server for sandbox mode (--sandbox). The server must run with SANDBOX=1 (its default) to grant it.
shooter-client-sandbox:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --role client --sandbox $(SHOOTER_MAP)

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

# Bot navigation spike (#214): headless collision-only map load, navmesh bake from the colliders, then bake time, cache size and
# path queries (spawn pairs, yards behind fences). No server, no clients. NAV="--nav-cell 0.5 --nav-split 2 --nav-threads 8" for
# other settings. Results in out/navspike/<timestamp>/report.json.
shooter-nav-spike:
	dotnet build godot/shooter/shooter.csproj
	godot --headless --path godot/shooter -- --role server --nav-spike --nav-out $(abspath out/navspike) $(NAV) $(SHOOTER_MAP)

# Bake the rozhlas broadcast from assets/drycastle-rozhlas/source/{song,announcement,opakujem}.* (scripts/make_rozhlas.py).
rozhlas:
	python3 -I scripts/make_rozhlas.py

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

# The intel screen's content (the base's wall screen) with made-up events, at its two texture sizes (needs a display): make shooter-intel-shots DIR=out/intel
shooter-intel-shots:
	dotnet build godot/shooter/shooter.csproj
	godot --path godot/shooter -- --intel-shots $(abspath $(or $(DIR),out/intel))

# Export presets (godot/shooter/export_presets.cfg) into out/shooter/. Needs the Godot 4.7.2 mono export templates
# (Editor > Manage Export Templates); not downloaded here. assets/ and map/ (SHOOTER_PKG_MAP) are copied next to each binary.
.PHONY: shooter-export shooter-export-check shooter-package
SHOOTER_TEMPLATES = $(HOME)/.local/share/godot/export_templates/4.7.2.stable.mono
# The map bundled into every export (as map/ next to the binary): the full village build. Clients and the server must run
# the same map build, so exports always carry it; override with SHOOTER_PKG_MAP=<map dir>.
SHOOTER_PKG_MAP ?= out/village/map

shooter-export-check:
	@test -f godot/shooter/export_presets.cfg || { echo "missing godot/shooter/export_presets.cfg"; exit 1; }
	@grep -q 'include_filter="data/\*.json"' godot/shooter/export_presets.cfg || { echo "export_presets.cfg does not pack data/*.json (#133)"; exit 1; }
	@for f in weapons match vehicles sounds perks levels killstreaks turret environment camo; do test -f godot/shooter/data/$$f.json || { echo "missing godot/shooter/data/$$f.json"; exit 1; }; done
	@[ -n "$(SHOOTER_NO_MAP)" ] || test -f $(SHOOTER_PKG_MAP)/manifest.json || { echo "map not built: $(SHOOTER_PKG_MAP)/manifest.json missing. Exports bundle the full village map: run 'make map' (or set SHOOTER_PKG_MAP=<map dir>)"; exit 1; }
	@echo "$$(grep -c '^name=' godot/shooter/export_presets.cfg) export presets found; map: $(if $(SHOOTER_NO_MAP),not bundled (SHOOTER_NO_MAP),bundling $(SHOOTER_PKG_MAP))"
	@if [ -d "$(SHOOTER_TEMPLATES)" ] && [ -n "$$(ls -A $(SHOOTER_TEMPLATES) 2>/dev/null)" ]; then echo "export templates: OK ($(SHOOTER_TEMPLATES))"; \
	else echo "export templates: MISSING at $(SHOOTER_TEMPLATES) (install the 4.7.2 mono templates: Editor > Manage Export Templates)"; exit 1; fi

shooter-export: shooter-export-check
	dotnet build godot/shooter/shooter.csproj
	mkdir -p out/shooter/windows/assets out/shooter/linux-client/assets out/shooter/linux-server/assets out/shooter/windows/map out/shooter/linux-client/map out/shooter/linux-server/map out/shooter/macos/assets out/shooter/macos/map out/shooter/macos-export
	godot --headless --path godot/shooter --export-release "Windows Client"
	godot --headless --path godot/shooter --export-release "Linux Client"
	godot --headless --path godot/shooter --export-release "Linux Server"
	godot --headless --path godot/shooter --export-release "macOS Client"
	unzip -qo out/shooter/macos-export/drycastle-shooter.zip -d out/shooter/macos   # Godot writes the .app as a zip; unzip keeps the exec bits
	for d in windows linux-client linux-server macos; do cp -r assets/. out/shooter/$$d/assets/; [ -n "$(SHOOTER_NO_MAP)" ] || cp -r $(SHOOTER_PKG_MAP)/. out/shooter/$$d/map/; done
	@echo "exported to out/shooter/ (assets/ and map/ next to each binary; see docs/shooter-server.md)"

# Double-click Windows package: out/shooter/drycastle-windows.zip (exe + assets/ + map/ + README.txt).
# Staged as symlinks and zipped (zip follows them), so nothing is copied or deleted. Map: the bundled map folder
# (default out/village/map, set above) is already inside each export; it must be the build the server runs.
shooter-package: shooter-export
	mkdir -p out/shooter/package/Drycastle
	for f in out/shooter/windows/*; do ln -sfn "$$PWD/$$f" out/shooter/package/Drycastle/; done
	ln -sfn "$$PWD/docs/player-readme.txt" out/shooter/package/Drycastle/README.txt
	cd out/shooter/package && zip -qrFS ../drycastle-windows.zip Drycastle
	@echo "package: out/shooter/drycastle-windows.zip ($$(du -h out/shooter/drycastle-windows.zip | cut -f1))"

# Launcher distribution (docs/launcher.md). The CI release job runs these; they work locally too.
# Game zips WITHOUT the map (the launcher installs the map from its own release): out/shooter/drycastle-game-{windows,linux,macos}.zip, files at the zip root.
.PHONY: shooter-export-server shooter-package-game map-package launcher-build launcher-export launcher-test
shooter-package-game:
	$(MAKE) shooter-export SHOOTER_NO_MAP=1
	rm -f out/shooter/drycastle-game-windows.zip out/shooter/drycastle-game-linux.zip out/shooter/drycastle-game-macos.zip
	cd out/shooter/windows && zip -qr ../drycastle-game-windows.zip . -x 'map/*'
	cd out/shooter/linux-client && zip -qr ../drycastle-game-linux.zip . -x 'map/*'
	cd out/shooter/macos && zip -qr ../drycastle-game-macos.zip . -x 'map/*'
	@echo "packages: out/shooter/drycastle-game-{windows,linux,macos}.zip"

# Linux server export only (used by the release pipeline to deploy): out/shooter/linux-server with assets/ and map/ (SHOOTER_PKG_MAP).
shooter-export-server: shooter-export-check
	dotnet build godot/shooter/shooter.csproj
	mkdir -p out/shooter/linux-server/assets out/shooter/linux-server/map
	godot --headless --path godot/shooter --export-release "Linux Server"
	cp -r assets/. out/shooter/linux-server/assets/
	cp -r $(SHOOTER_PKG_MAP)/. out/shooter/linux-server/map/

# Map archive for the launcher: out/village/map.zip + out/village/map.json (name, size, sha256). Publish with scripts/publish-map.sh.
map-package:
	@test -f $(SHOOTER_PKG_MAP)/manifest.json || { echo "map not built: run 'make map'"; exit 1; }
	rm -f out/village/map.zip
	cd $(SHOOTER_PKG_MAP) && zip -qr $(abspath out/village/map.zip) .
	python3 scripts/map_json.py out/village/map.zip out/village/map.json

launcher-build:
	dotnet build godot/launcher/launcher.csproj

launcher-test:
	dotnet test godot/ShooterCore.Tests/ShooterCore.Tests.csproj --filter "FullyQualifiedName~Launcher"

# Exports godot/launcher to out/launcher/ (Windows, Linux and macOS zips). Needs the same 4.7.2 mono templates as shooter-export.
launcher-export: launcher-build
	mkdir -p out/launcher/windows out/launcher/linux out/launcher/macos
	godot --headless --path godot/launcher --export-release "Windows Launcher"
	godot --headless --path godot/launcher --export-release "Linux Launcher"
	godot --headless --path godot/launcher --export-release "macOS Launcher"
	unzip -qo out/launcher/macos/DrycastleLauncher.zip -d out/launcher/macos && rm out/launcher/macos/DrycastleLauncher.zip   # Godot writes the .app as a zip; re-zipped below with the README
	cp docs/launcher-readme.txt out/launcher/windows/README.txt; cp docs/launcher-readme.txt out/launcher/linux/README.txt; cp docs/launcher-readme.txt out/launcher/macos/README.txt
	rm -f out/launcher/drycastle-launcher-windows.zip out/launcher/drycastle-launcher-linux.zip out/launcher/drycastle-launcher-macos.zip
	cd out/launcher/windows && zip -qr ../drycastle-launcher-windows.zip .
	cd out/launcher/linux && zip -qr ../drycastle-launcher-linux.zip .
	cd out/launcher/macos && zip -qr ../drycastle-launcher-macos.zip .

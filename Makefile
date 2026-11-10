# AREA=slice (default): data/raw, data/work, out/map. AREA=village: data/village/..., out/village/map.
AREA ?= slice

.PHONY: setup test fetch normalise parcels rules terrain roads props export preview map viewer viewer-smoke viewer-shot netspike netspike-quick lint

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

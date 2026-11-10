.PHONY: setup test fetch normalise parcels rules terrain roads props export preview map viewer viewer-smoke viewer-shot lint

setup:
	cd pipeline && uv sync

test:
	cd pipeline && uv run pytest

fetch:
	cd pipeline && uv run python -m suchohrad_pipeline fetch

normalise:
	cd pipeline && uv run python -m suchohrad_pipeline normalise

parcels:
	cd pipeline && uv run python -m suchohrad_pipeline parcels --report

rules:
	cd pipeline && uv run python -m suchohrad_pipeline rules

terrain:
	cd pipeline && uv run python -m suchohrad_pipeline terrain

roads:
	cd pipeline && uv run python -m suchohrad_pipeline roads

props:
	cd pipeline && uv run python -m suchohrad_pipeline props

export:
	cd pipeline && uv run python -m suchohrad_pipeline export

EXAG ?= 3

preview:
	cd pipeline && uv run python -m suchohrad_pipeline preview --exaggeration $(EXAG)

map:
	cd pipeline && uv run python -m suchohrad_pipeline run-all

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

lint:
	cd pipeline && uv run ruff check .

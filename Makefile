# AREA=slice (default): data/raw, data/work, out/map. AREA=village: data/village/..., out/village/map.
AREA ?= slice

.PHONY: setup test fetch normalise parcels rules terrain roads props export preview map viewer lint

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

viewer:
	godot --path godot/viewer

lint:
	cd pipeline && uv run ruff check .

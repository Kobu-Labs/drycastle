.PHONY: setup test fetch normalise parcels rules terrain roads props export preview map viewer lint

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

viewer:
	godot --path godot/viewer

lint:
	cd pipeline && uv run ruff check .

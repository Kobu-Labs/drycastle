.PHONY: setup test fetch map viewer lint

setup:
	cd pipeline && uv sync

test:
	cd pipeline && uv run pytest

fetch:
	cd pipeline && uv run python -m suchohrad_pipeline fetch

map:
	cd pipeline && uv run python -m suchohrad_pipeline run-all

viewer:
	godot --path godot/viewer

lint:
	cd pipeline && uv run ruff check .

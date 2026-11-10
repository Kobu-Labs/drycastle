"""Pipeline stages, in execution order.

Each stage is a stub for now. A real stage replaces its ``run`` function and
keeps the same name in ``STAGES`` so the CLI and ``run-all`` keep working.
"""

from collections.abc import Callable


def _stub(name: str) -> Callable[[], int]:
    def run() -> int:
        print(f"{name}: not implemented yet")
        return 0

    run.__name__ = f"run_{name}"
    return run


STAGES: dict[str, Callable[[], int]] = {
    name: _stub(name)
    for name in ("fetch", "normalise", "parcels", "rules", "terrain", "roads", "props", "export")
}

"""Pipeline stages, in execution order.

Stages other than fetch are stubs for now. A real stage replaces its ``run`` function and
keeps the same name in ``STAGES`` so the CLI and ``run-all`` keep working.
"""

from collections.abc import Callable

from suchohrad_pipeline import fetch


def _stub(name: str) -> Callable[..., int]:
    def run(**_options: object) -> int:
        print(f"{name}: not implemented yet")
        return 0

    run.__name__ = f"run_{name}"
    return run


STAGES: dict[str, Callable[..., int]] = {
    "fetch": fetch.run,
    **{
        name: _stub(name)
        for name in ("normalise", "parcels", "rules", "terrain", "roads", "props", "export")
    },
}

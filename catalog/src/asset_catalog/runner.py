"""Runs one stage over many assets with the cache in front of it."""

from __future__ import annotations

import sys
import traceback
from collections.abc import Callable, Iterable
from concurrent.futures import ProcessPoolExecutor, as_completed

from pydantic import BaseModel

from .model import Asset
from .store import Store


def run_stage(
    stage: str,
    assets: Iterable[Asset],
    key_of: Callable[[Asset], str],
    compute: Callable[[Asset], BaseModel],
    model: type[BaseModel],
    *,
    workers: int = 1,
    force: bool = False,
    store: Store | None = None,
) -> dict[str, int]:
    """compute(asset) for every asset whose stored key differs from key_of(asset).

    compute must be a module-level function when workers > 1 (it is pickled).
    A failing asset is reported and skipped; the rest still run.
    """
    store = store or Store()
    todo: list[tuple[Asset, str]] = []
    fresh = 0
    for a in assets:
        k = key_of(a)
        if not force and store.key(stage, a.id) == k:
            fresh += 1
        else:
            todo.append((a, k))
    done = failed = 0

    def finish(a: Asset, k: str, result: BaseModel | None, err: str | None) -> None:
        nonlocal done, failed
        if err is not None:
            failed += 1
            print(f"[{stage}] FAILED {a.id}: {err}", file=sys.stderr)
            return
        store.put(stage, a.id, k, result)
        done += 1
        if done % 100 == 0:
            print(f"[{stage}] {done}/{len(todo)}", file=sys.stderr)

    if workers <= 1:
        for a, k in todo:
            try:
                finish(a, k, compute(a), None)
            except Exception:  # noqa: BLE001 - one bad asset must not stop the run
                finish(a, k, None, traceback.format_exc(limit=3))
    else:
        with ProcessPoolExecutor(workers) as pool:
            futures = {pool.submit(compute, a): (a, k) for a, k in todo}
            for fut in as_completed(futures):
                a, k = futures[fut]
                try:
                    finish(a, k, fut.result(), None)
                except Exception as e:  # noqa: BLE001 - one bad asset must not stop the run
                    finish(a, k, None, repr(e))
    return {"fresh": fresh, "done": done, "failed": failed}

"""Writers of edits.json share one lock; the document ``validate`` checks is built from the loaded edits."""

import contextlib
import fcntl
from collections.abc import Iterator
from pathlib import Path

from drycastle_pipeline.edits.schema import Edits


@contextlib.contextmanager
def edits_lock(path: Path) -> Iterator[None]:
    """Exclusive advisory lock on ``<edits>.lock`` (other writers that honour it wait)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path.with_name(path.name + ".lock"), "a+") as fh:
        fcntl.flock(fh, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(fh, fcntl.LOCK_UN)


def edits_doc(edits: Edits) -> dict:
    """The edits as the document ``validate`` checks (patches and zones included when present)."""
    doc: dict = {"version": 1, "parcels": edits.parcels, "groups": edits.groups}
    if edits.objects:
        doc["objects"] = edits.objects
    if edits.ground_patches:
        doc["ground_patches"] = edits.ground_patches
    if edits.zones:
        doc["zones"] = edits.zones
    if edits.zone_cuts:
        doc["zone_cuts"] = edits.zone_cuts
    if edits.cables:
        doc["cables"] = edits.cables
    if edits.ground_types:
        doc["ground_types"] = edits.ground_types
    if edits.paths:
        doc["paths"] = edits.paths
    if edits.mineral_nodes:
        doc["mineral_nodes"] = edits.mineral_nodes
    return doc

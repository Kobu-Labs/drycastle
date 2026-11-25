"""Tiny synthetic CatalogRecords for index and search tests. No real assets."""

from __future__ import annotations

from asset_catalog.record import CatalogRecord


def make(asset_id: str, **over) -> CatalogRecord:
    """A valid record; override any field. plain_name defaults to the stem with spaces."""
    source, pack, stem = asset_id.split("/", 2)
    base = dict(  # noqa: C408
        id=asset_id, source=source, pack=pack, name=stem, path=f"x/{stem}.glb",
        variant_group=None, kind="prop", category="prop/misc/other", secondary_categories=[],
        materials=[], settings=[], placements=[], functions=[], styles=[], condition=["intact"],
        real_front="none", plain_name=stem.replace("_", " "), description="", usage="",
        aliases=[], distinguishing="", snaps_with=[], likely_queries=[], doubts=[],
        size_m=(0.5, 0.5, 0.5), longest_m=0.5, size_class="tabletop", form="box",
        flat_faces=[], pivot="base-centre", tris=100, has_rig=False, animations=[], colours=[],
        contact_sheet="c.png", views={}, described_by="m",
    )  # fmt: skip
    base.update(over)
    return CatalogRecord(**base)

"""The catalogue record: everything search knows about one asset, joined from the stages."""

from __future__ import annotations

from pydantic import BaseModel

from .model import Asset
from .stages import describe, measure, render
from .store import Store


class CatalogRecord(BaseModel):
    # identity
    id: str
    source: str
    pack: str
    name: str
    path: str
    variant_group: str | None
    # written by the describing model: closed vocabularies
    kind: str
    category: str
    secondary_categories: list[str]
    materials: list[str]
    settings: list[str]
    placements: list[str]
    functions: list[str]
    styles: list[str]
    condition: list[str]
    real_front: str
    # written by the describing model: free text
    plain_name: str
    description: str
    usage: str
    aliases: list[str]
    distinguishing: str
    snaps_with: list[str]
    likely_queries: list[str]
    doubts: list[str]
    # measured by code
    size_m: tuple[float, float, float]  # width x, height y, depth z
    longest_m: float
    size_class: str
    form: str
    flat_faces: list[str]
    pivot: str
    tris: int
    has_rig: bool
    animations: list[str]
    colours: list[str]  # names, largest area first, no repeats
    # files, relative to the catalogue data dir
    contact_sheet: str
    views: dict[str, str]
    described_by: str

    def what_it_is(self) -> str:
        """Text for the 'what it is' embedding: identity and appearance."""
        parts = [self.plain_name, self.category.replace("/", ", "), self.description]
        if self.aliases:
            parts.append("Also called: " + ", ".join(self.aliases) + ".")
        if self.distinguishing:
            parts.append(self.distinguishing)
        return " ".join(parts)

    def what_it_is_for(self) -> str:
        """Text for the 'what it is for' embedding: use, place, need."""
        parts = [self.plain_name + ".", self.usage]
        if self.settings:
            parts.append("Belongs in: " + ", ".join(self.settings) + ".")
        if self.functions:
            parts.append("Serves as: " + ", ".join(self.functions) + ".")
        return " ".join(parts)


def build(asset: Asset, model: str, st: Store) -> CatalogRecord | None:
    """The joined record, or None while any stage's output is missing."""
    d = st.get(describe.stage_name(model), asset.id, describe.Described)
    m = st.get(measure.STAGE, asset.id, measure.Measure)
    r = st.get(render.STAGE, asset.id, render.RenderPayload)
    if d is None or m is None or m.geometry is None or r is None or r.status != "ok":
        return None
    g = m.geometry
    colours = list(dict.fromkeys(c.name for c in r.dominant_colors))
    return CatalogRecord(
        **asset.model_dump(include={"id", "source", "pack", "name", "path", "variant_group"}),
        **d.record.model_dump(),
        size_m=tuple(g.size_m),
        longest_m=max(g.size_m),
        size_class=g.size_class,
        form=g.form,
        flat_faces=g.flat_faces,
        pivot=g.pivot.kind,
        tris=m.tris,
        has_rig=m.has_rig,
        animations=[a.name for a in m.animations],
        colours=colours,
        contact_sheet=r.contact_sheet,
        views=r.views,
        described_by=d.model,
    )


def load_all(assets: list[Asset], model: str, st: Store | None = None) -> list[CatalogRecord]:
    st = st or Store()
    return [rec for a in assets if (rec := build(a, model, st)) is not None]

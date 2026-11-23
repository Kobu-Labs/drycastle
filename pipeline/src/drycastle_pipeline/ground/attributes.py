"""The attribute catalog of ground types (ADR 0015, 0022): what a ground type can carry.

A **ground type** is a named bundle of terrain **attributes** (``attributes`` of its entry in ``rules.yaml``
``ground.types`` or ``edits.json`` ``ground_types``). The attributes are fixed here, in code: each one needs something in
the pipeline to use it. The editor groups them by relation (:data:`GROUPS`) and a type ticks the ones it carries. Every
point of the map has exactly one ground type, so there is nothing to stack: a ground type is terrain only (colour, detail,
water) and carries nothing a game system reads. What an area means in play is a zone (:mod:`drycastle_pipeline.edits.zones`).

The editor mirrors this table (``godot/viewer/GroundTypeEdits.cs``: ``Attributes``, ``Groups``).
"""

from dataclasses import dataclass

#: The recipes a ground type can name (``detail.RECIPES`` without the parcel recipe ``garden``; ``crop``: the crop fields, ``detail/crop.py``).
RECIPES = ("field", "forest", "village", "dike", "crop")

COLOURS_MAX = 6


@dataclass(frozen=True)
class Group:
    key: str
    label: str
    note: str


@dataclass(frozen=True)
class Attribute:
    key: str
    group: str
    label: str


#: The groups of the editor, in order. ``note`` is what the editor says under each heading.
GROUPS = (
    Group("appearance", "Appearance", "The terrain colour and the ground detail of the type."),
    Group("water", "Water", "Whether the ground is a body of water."),
)

ATTRIBUTES: dict[str, Attribute] = {
    a.key: a
    for a in (
        Attribute("colours", "appearance", "Terrain colours"),
        Attribute("recipe", "appearance", "Detail recipe"),
        Attribute("detail", "appearance", "Detail settings"),
        Attribute("water", "water", "Water body"),
    )
}

TERRAIN_KEYS = tuple(ATTRIBUTES)
#: The boolean attributes: carried as ``true`` (``null`` in an override removes it).
FLAG_KEYS = ("water",)

#: The keys of a ground type entry besides ``attributes``: where it takes land from (``osm``, ``rule``) and ``deleted``.
SOURCE_KEYS = ("osm", "rule")
TYPE_KEYS = (*SOURCE_KEYS, "attributes")


def attributes_of(spec: dict | None) -> dict:
    """The attributes a ground type carries (a missing ``attributes`` is none)."""
    return (spec or {}).get("attributes") or {}


def carries_terrain(spec: dict | None) -> bool:
    """True when the type has any attribute: it can be the type of a ground area (a type with none is only a name)."""
    attrs = attributes_of(spec)
    return any(k in attrs for k in TERRAIN_KEYS)

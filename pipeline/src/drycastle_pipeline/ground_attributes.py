"""The attribute catalog of ground types (ADR 0015): what a ground type can carry, and how stacked types combine.

A **ground type** is a named bundle of **attributes** (``attributes`` of its entry in ``rules.yaml`` ``ground.types`` or
``edits.json`` ``ground_types``). The attributes are fixed here, in code: each one needs something in the pipeline or the
game to use it. The editor groups them by relation (:data:`GROUPS`), a type ticks the ones it carries, and a ground area
can carry several types stacked, the draw order being the priority. How the stack is read at a point depends on the
attribute's **stacking** (:data:`ATTRIBUTES`):

* ``terrain``: the topmost type carrying *any* terrain attribute decides all of them at that point (terrain colour,
  detail recipe and settings, whether it is water), exactly as the single type of a ground area did before. The stages
  that build the terrain, the detail and the water see one such type per point and need no stacking logic.
* ``any`` (a flag) and ``union`` (a list): every stacked type counts, none shadows another. A type that carries only
  attributes of these kinds can lie on top of forest without changing how the forest looks.

The editor mirrors this table (``godot/viewer/GroundTypeEdits.cs``: ``Attributes``, ``Groups``, ``Events``).
"""

from dataclasses import dataclass

TERRAIN = "terrain"
ANY = "any"
UNION = "union"

#: The events an ``event_spawn`` attribute can name (docs/events/README.md). Only an area that carries the event can spawn it.
EVENTS = ("bigfoot",)

#: The values each list attribute (stacking ``union``) can hold; a runtime attribute (``attribute_points.py``) is one of these values.
LIST_VALUES = {"event_spawn": EVENTS}

#: The recipes a ground type can name (``detail.RECIPES`` without the parcel recipe ``garden``).
RECIPES = ("field", "forest", "village", "dike")

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
    stacking: str  # TERRAIN, ANY or UNION
    label: str
    tint: str | None = None  # the default colour of a region that is only a layer of this attribute (editor overlay)


#: The groups of the editor, in order. ``note`` is what the editor says under each heading.
GROUPS = (
    Group("appearance", "Appearance", "The topmost stacked type with appearance or water attributes decides the terrain colour and detail."),
    Group("spawning", "Spawning", "Adds up over every stacked type."),
    Group("water", "Water", "The topmost stacked type with appearance or water attributes decides if the ground is water."),
    Group("events", "Events", "Adds up over every stacked type."),
)

ATTRIBUTES: dict[str, Attribute] = {
    a.key: a
    for a in (
        Attribute("colours", "appearance", TERRAIN, "Terrain colours"),
        Attribute("recipe", "appearance", TERRAIN, "Detail recipe"),
        Attribute("detail", "appearance", TERRAIN, "Detail settings"),
        Attribute("spawns", "spawning", ANY, "Players may spawn here", tint="#4aa3e8"),
        Attribute("water", "water", TERRAIN, "Water body"),
        Attribute("event_spawn", "events", UNION, "Event spawn", tint="#c46be0"),
    )
}

TERRAIN_KEYS = tuple(k for k, a in ATTRIBUTES.items() if a.stacking == TERRAIN)
LAYER_KEYS = tuple(k for k, a in ATTRIBUTES.items() if a.stacking != TERRAIN)
#: The boolean attributes: carried as ``true`` (``null`` in an override removes it).
FLAG_KEYS = ("spawns", "water")

#: The keys of a ground type entry besides ``attributes``: where it takes land from (``osm``, ``rule``) and ``deleted``.
SOURCE_KEYS = ("osm", "rule")
TYPE_KEYS = (*SOURCE_KEYS, "attributes")


def attributes_of(spec: dict | None) -> dict:
    """The attributes a ground type carries (a missing ``attributes`` is none)."""
    return (spec or {}).get("attributes") or {}


def carries_terrain(spec: dict | None) -> bool:
    """True when the type has any terrain attribute: it can be the type that decides a point's terrain."""
    attrs = attributes_of(spec)
    return any(k in attrs for k in TERRAIN_KEYS)


def carries_layer(spec: dict | None) -> bool:
    """True when the type has any attribute that adds up (spawning, events) instead of deciding the terrain."""
    attrs = attributes_of(spec)
    return any(k in attrs for k in LAYER_KEYS)


def carries_value(spec: dict | None, attribute: str, value: str) -> bool:
    """True when the type carries the list attribute ``attribute`` with ``value`` in it (``event_spawn`` with ``bigfoot``)."""
    return value in (attributes_of(spec).get(attribute) or [])


def terrain_index(stack: list[dict]) -> int | None:
    """The index in ``stack`` (types, lowest first) of the type that decides the terrain: the topmost one that carries
    terrain attributes, or None when none does."""
    for i in range(len(stack) - 1, -1, -1):
        if carries_terrain(stack[i]):
            return i
    return None


def accumulate(stack: list[dict]) -> dict:
    """What the stacked types (lowest first) add up to: ``spawns`` (any type carries it) and ``event_spawn`` (the
    union of the events, in the order of :data:`EVENTS`). Only the attributes some type carries are in the result."""
    out: dict = {}
    for spec in stack:
        for key, value in attributes_of(spec).items():
            stacking = ATTRIBUTES[key].stacking if key in ATTRIBUTES else None
            if stacking == ANY and value:
                out[key] = True
            elif stacking == UNION:
                out[key] = sorted({*out.get(key, []), *value}, key=lambda e: EVENTS.index(e) if e in EVENTS else len(EVENTS))
    return out


def default_tint(keys: list[str] | set[str]) -> str | None:
    """The overlay colour for a layer of these attributes: that of the first attribute (catalog order) that has one."""
    for key, attr in ATTRIBUTES.items():
        if key in keys and attr.tint:
            return attr.tint
    return None

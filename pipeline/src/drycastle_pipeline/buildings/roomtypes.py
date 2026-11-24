"""The rooms a house is to have: its programme, by room type, with the size each room should get.

Types: ``hall``, ``living``, ``kitchen``, ``living_kitchen`` (one space to live and cook in), ``dining``, ``bedroom``,
``bathroom``, ``storage`` (a closet, a pantry, a utility room), ``study``.

The programme comes before the walls (buildings/floorplan.py cuts the house to fit it), so rooms differ in size as their
uses do: one large living space, a kitchen, a small bathroom, bedrooms of unequal size with the largest last, closets.
It is listed from the front door inward: what guests see first, what is private last.
"""

import random
from dataclasses import dataclass, replace

LIVING, KITCHEN, LIVING_KITCHEN, DINING, BEDROOM, BATHROOM, STORAGE, STUDY, HALL = (
    "living", "kitchen", "living_kitchen", "dining", "bedroom", "bathroom", "storage", "study", "hall",
)  # fmt: skip
TYPES = (HALL, LIVING, KITCHEN, LIVING_KITCHEN, DINING, BEDROOM, BATHROOM, STORAGE, STUDY)
PUBLIC = (LIVING, LIVING_KITCHEN, KITCHEN, DINING)
SMALL = (BATHROOM, STORAGE)  # rooms that keep their size however large the house is
MIN_SIDE_M = {LIVING: 3.0, LIVING_KITCHEN: 3.0, KITCHEN: 2.2, DINING: 2.4, BEDROOM: 2.4, STUDY: 2.0, BATHROOM: 1.5, STORAGE: 1.1}


MIN_M2 = {LIVING: 12.0, LIVING_KITCHEN: 14.0, KITCHEN: 5.0, DINING: 7.0, BEDROOM: 7.0, STUDY: 6.0, BATHROOM: 3.0, STORAGE: 1.5}


@dataclass(frozen=True)
class Want:
    type: str
    area: float

    @property
    def min_area(self) -> float:
        return MIN_M2[self.type]

    @property
    def min_side(self) -> float:
        return MIN_SIDE_M[self.type]


def scaled(wants: list[Want], area: float) -> list[Want]:
    """The same rooms with their sizes adding up to ``area``: the small ones keep theirs (within a quarter), the others share the rest."""
    total = sum(w.area for w in wants)
    if total <= 0 or not wants:
        return wants
    k = area / total
    small = [w for w in wants if w.type in SMALL]
    big = [w for w in wants if w.type not in SMALL]
    if not big or not small:
        return [replace(w, area=w.area * k) for w in wants]
    ks = min(max(k, 0.8), 1.25)
    kb = (area - ks * sum(w.area for w in small)) / sum(w.area for w in big)
    if kb <= 0.3:
        return [replace(w, area=w.area * k) for w in wants]
    return [replace(w, area=w.area * (ks if w.type in SMALL else kb)) for w in wants]


def programme(area: float, style: str, rng: random.Random) -> list[Want]:
    """The rooms for ``area`` m² of floor (the hall not counted) in a house of this style, front door first."""
    u = rng.uniform
    if area < 20:
        return [Want(LIVING_KITCHEN, area)]
    if area < 34:
        return scaled([Want(LIVING_KITCHEN, area - 4.5), Want(BATHROOM, 4.5)], area)
    beds = 1 if area < 52 else 2 if area < 82 else 3 if area < 125 else 4
    front: list[Want] = []
    if area > 75:
        front.append(Want(STORAGE, u(2.2, 3.6)))  # the closet by the door
    if style == "open":
        public = [Want(LIVING_KITCHEN, min(area * u(0.38, 0.48), 58.0))]
    else:
        share = u(0.26, 0.32) if style == "mixed" else u(0.22, 0.28)
        public = [Want(LIVING, min(area * share, 40.0)), Want(KITCHEN, min(max(area * u(0.10, 0.14), 7.0), 16.0))]
        if area > 105 and (style == "closed" or rng.random() < 0.5):
            public.append(Want(DINING, u(10.0, 14.0)))
    middle = [Want(BATHROOM, u(4.5, 6.5))]
    if area > 60 and rng.random() < 0.7:
        middle.insert(0, Want(STORAGE, u(2.5, 5.0)))  # pantry or utility room, next to the kitchen
    private = [Want(BEDROOM, u(9.0, 12.5)) for _ in range(beds - 1)]
    if area > 100 and rng.random() < 0.6:
        private.insert(0, Want(STUDY, u(8.0, 11.5)))
    if beds >= 3 and rng.random() < 0.6:
        private.append(Want(BATHROOM, u(3.5, 4.8)))  # beside the last bedroom: its own, where the plan lets it
    private.append(Want(BEDROOM, u(13.0, 17.0)))
    wants = [*front, *public, *middle, *private]
    while sum(w.area for w in wants) < 0.88 * area:  # a large house: more rooms before larger ones
        extra = Want(BEDROOM, u(10.0, 14.0)) if rng.random() < 0.5 else Want(STUDY, u(9.0, 13.0)) if rng.random() < 0.5 else Want(STORAGE, u(4.0, 7.0))
        wants.insert(len(wants) - 1, extra)
    return scaled(wants, area)

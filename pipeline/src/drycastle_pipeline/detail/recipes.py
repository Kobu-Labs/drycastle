"""The recipe registry and :func:`area_type`, the one place that says which recipe a parcel or ground area gets."""

import functools

from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.detail.model import DetailResult, ParcelContext
from drycastle_pipeline.detail.recipes_field import field_detail, village_detail
from drycastle_pipeline.detail.recipes_forest import forest_detail
from drycastle_pipeline.detail.recipes_garden import garden_detail
from drycastle_pipeline.ground.areas import ground_kind, ground_types
from drycastle_pipeline.ground.attributes import attributes_of


def dike_recipe(ctx: ParcelContext, cfg: dict) -> DetailResult:
    from drycastle_pipeline.detail.dike import dike_detail

    return dike_detail(ctx, cfg)


def crop_recipe(ctx: ParcelContext, cfg: dict) -> DetailResult:
    from drycastle_pipeline.detail.crop import crop_detail

    return crop_detail(ctx, cfg)


RECIPES = {"garden": garden_detail, "field": field_detail, "forest": forest_detail, "village": village_detail, "dike": dike_recipe, "crop": crop_recipe}


@functools.cache
def _shipped_types() -> dict[str, dict]:
    return ground_types()


@functools.cache
def dike_parcels() -> frozenset[str]:
    """The parcels that are a flood dike (``detail.dike.parcels``): they get the dike recipe, whatever their kind."""
    return frozenset(load_section("detail", DEFAULT_RULES)["dike"]["parcels"])


def area_type(parcel: dict, types: dict[str, dict] | None = None) -> str | None:
    """The recipe (:data:`RECIPES`) of a parcel or ground area, by its kind; None: no detail (water).

    The only place that reads the kind: a parcel is a yard, a ground area has the ``recipe`` of its type
    (``ground.types`` in ``rules.yaml``), and so has a parcel the edits gave a ground type (``ground.ground_kind``)."""
    if parcel.get("id") in dike_parcels():
        return "dike"
    if parcel["kind"] in ("garden", "building_plot") and not parcel.get("ground_type"):
        return "garden"
    return attributes_of((types if types is not None else _shipped_types()).get(ground_kind(parcel))).get("recipe")

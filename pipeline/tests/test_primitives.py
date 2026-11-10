import numpy as np
import pytest

from suchohrad_pipeline.assets import (
    CATEGORIES,
    DEFAULT_MANIFEST,
    MANIFEST_FORMAT_VERSION,
    asset_ids,
    load_manifest,
    validate_asset_ref,
)
from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.primitives import PRIMITIVES, build_primitive

MANIFEST = load_manifest()


def bbox(mesh):
    pts = mesh.tris.reshape(-1, 3)
    return pts.min(axis=0), pts.max(axis=0)


def signed_volume(mesh) -> float:
    a, b, c = mesh.tris[:, 0], mesh.tris[:, 1], mesh.tris[:, 2]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6)


@pytest.mark.parametrize("name", sorted(PRIMITIVES))
def test_primitive_is_deterministic_flat_and_outward(name) -> None:
    a, b = build_primitive(name), build_primitive(name)
    assert a.face_count > 0
    assert np.array_equal(a.tris, b.tris) and np.array_equal(a.colours, b.colours)
    assert a.colours.dtype == np.uint8 and len(a.colours) == a.face_count
    assert signed_volume(a) > 0  # counter-clockwise seen from outside


@pytest.mark.parametrize("name", sorted(PRIMITIVES))
def test_primitive_matches_declared_size_and_stands_on_ground(name) -> None:
    prim = PRIMITIVES[name]
    lo, hi = bbox(prim.builder(None))
    x, y_up, z = prim.size_m  # glTF axes: plan is x by z, height is y
    assert hi[2] == pytest.approx(y_up) and lo[2] == pytest.approx(0)
    assert hi[0] - lo[0] == pytest.approx(x)
    assert hi[1] - lo[1] == pytest.approx(z)
    assert (lo[:2] + hi[:2]) / 2 == pytest.approx([0, 0], abs=1e-9)
    assert hex_to_rgb(prim.colour) in prim.builder(None).colours.tolist()


def test_wall_default_and_params() -> None:
    lo, hi = bbox(build_primitive("wall"))
    assert (hi - lo) == pytest.approx([3.0, 0.2, 1.2])
    lo, hi = bbox(build_primitive("wall", {"length_m": 5, "height_m": 2, "colour": "#112233"}))
    assert (hi - lo) == pytest.approx([5.0, 0.2, 2.0])
    assert {tuple(c) for c in build_primitive("wall", {"colour": "#112233"}).colours} == {
        (17, 34, 51)
    }


def test_slab_is_a_light_grey_pavement_box() -> None:
    mesh = build_primitive("slab")
    lo, hi = bbox(mesh)
    assert (hi - lo) == pytest.approx([1.0, 1.0, 0.1])
    r, g, b = mesh.colours[0]
    assert r == g == b and r >= 180


def test_pot_is_tapered_eight_sided_with_soil() -> None:
    mesh = build_primitive("pot")
    prim = PRIMITIVES["pot"].params
    pts = mesh.tris.reshape(-1, 3)
    radius = np.hypot(pts[:, 0], pts[:, 1])
    assert radius[pts[:, 2] == 0].max() == pytest.approx(prim["bottom_radius_m"])
    assert radius[np.isclose(pts[:, 2], prim["height_m"])].max() == pytest.approx(
        prim["top_radius_m"]
    )
    soil = hex_to_rgb(prim["soil_colour"])
    soil_faces = (mesh.colours == soil).all(axis=1)
    assert soil_faces.sum() == 8
    assert (mesh.tris[soil_faces][:, :, 2] < prim["height_m"]).all()  # below the rim
    assert mesh.face_count == 8 * 8
    assert len(build_primitive("pot", {"sides": 12}).tris) == 12 * 8


# --- manifest ---


def test_manifest_is_valid_and_ids_unique() -> None:
    assert MANIFEST["format_version"] == MANIFEST_FORMAT_VERSION
    ids = [a["id"] for a in MANIFEST["assets"]]
    assert ids and len(ids) == len(set(ids))
    pack_names = {p["name"] for p in MANIFEST["packs"]}
    for p in MANIFEST["packs"]:
        assert p["licence"] == "CC0" and p["url"].startswith("https://") and p["downloaded"]
        assert (DEFAULT_MANIFEST.parent / p["name"] / "LICENSE.txt").is_file()
    for a in MANIFEST["assets"]:
        assert a["category"] in CATEGORIES, a["id"]
        assert a["licence"] == "CC0", a["id"]
        assert a["name"] and a["source"] and isinstance(a["tags"], list)
        assert len(a["size_m"]) == 3 and all(v > 0 for v in a["size_m"]), a["id"]
        if a["id"].startswith("pack:"):
            assert a["pack"] in pack_names and a["file"], a["id"]
            assert a["id"].startswith(f"pack:{a['pack']}/")
            assert a["file"].endswith((".glb", ".gltf"))
        else:
            assert a["id"].startswith("prim:") and a["file"] is None and a["pack"] is None


def test_every_pack_file_exists() -> None:
    for a in MANIFEST["assets"]:
        if a["file"]:
            assert (DEFAULT_MANIFEST.parent / a["file"]).is_file(), a["id"]


def test_prim_ids_match_registry_both_ways() -> None:
    prims = {i.removeprefix("prim:") for i in asset_ids(MANIFEST) if i.startswith("prim:")}
    assert prims == set(PRIMITIVES)


def test_validate_asset_ref() -> None:
    assert validate_asset_ref("prim:wall", MANIFEST) is None
    assert validate_asset_ref("pack:kenney-car-kit/sedan", MANIFEST) is None
    assert "unknown primitive" in validate_asset_ref("prim:nope", MANIFEST)
    assert "unknown pack model" in validate_asset_ref("pack:kenney-car-kit/nope", MANIFEST)
    assert "must start with" in validate_asset_ref("car", MANIFEST)
    assert validate_asset_ref("prim:slab") is None  # default manifest


def test_props_warns_for_unknown_assets_only() -> None:
    from suchohrad_pipeline.props import unknown_asset_warnings

    objs = [
        {"id": "a", "asset": "prim:slab"},
        {"id": "b", "asset": "prim:box"},
        {"id": "c", "asset": "pack:cars/sedan"},
    ]
    warnings = unknown_asset_warnings(objs)
    assert len(warnings) == 2 and "objects b" in warnings[0] and "objects c" in warnings[1]

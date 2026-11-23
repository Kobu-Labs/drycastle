"""The three Slovak road signs (#425, ADR 0039): manifest entries and the generated models they point at."""
import json
import struct

from drycastle_pipeline.core.assets import DEFAULT_MANIFEST, load_manifest

MANIFEST = load_manifest(private=False)
SIGNS = ["sign_stop", "sign_give_way", "sign_main_road"]


def glb_json(path) -> dict:
    data = path.read_bytes()
    length = struct.unpack_from("<I", data, 12)[0]
    return json.loads(data[20 : 20 + length])


def test_three_signs_are_in_the_manifest_with_a_plate_point_and_a_solid_pole() -> None:
    by_id = {a["id"]: a for a in MANIFEST["assets"]}
    for key in SIGNS:
        a = by_id[f"pack:drycastle-generated-models/{key}"]
        assert a["category"] == "sign" and a["licence"] == "CC0"
        assert len(a["plates"]) == 1 and len(a["plates"][0]) == 3  # the plate's centre (pipeline axes, z up)
        x, y, z = a["plates"][0]
        assert 1.5 < z < a["size_m"][1]
        # the pole alone is solid (the plate is not): one thin metal box up to the pole's height
        (solid,) = a["solids"]
        assert solid["surface"] == "metal" and solid["max"][2] <= 2.55 and solid["max"][0] <= 0.2


def test_each_model_has_a_pole_and_a_plate_node_with_metal_materials() -> None:
    for key in SIGNS:
        g = glb_json(DEFAULT_MANIFEST.parent / "drycastle-generated-models" / f"{key}.glb")
        names = [n["name"] for n in g["nodes"]]
        assert names.count("Pole") == 1 and names.count("Plate") == 1
        assert all(m["pbrMetallicRoughness"]["metallicFactor"] >= 0.5 for m in g["materials"])
        assert not any("emissiveFactor" in m for m in g["materials"])

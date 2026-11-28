"""Static mineral nodes: the edits section and the mineral_nodes.json builder."""

import numpy as np

from drycastle_pipeline.edits.schema import dump_text, parse, validate
from drycastle_pipeline.export.docs import build_mineral_nodes


class FlatTerrain:
    def surface(self, x, y) -> np.ndarray:
        return np.full(len(x), 190.0)


def test_mineral_nodes_validate_roundtrip_and_omit_when_empty() -> None:
    ok = {"version": 1, "parcels": {}, "mineral_nodes": {
        "MN-0001": {"mineral": "smetiskomite", "position": [-588819, -1249483.5]},
        "MN-0002": {"mineral": "thundernite", "position": [-589000, -1249600], "z": 212.5}}}
    assert validate(ok) == []
    assert parse(ok).mineral_nodes == ok["mineral_nodes"] and '"mineral_nodes"' in dump_text(parse(ok))
    assert '"mineral_nodes"' not in dump_text(parse({"version": 1, "parcels": {}}))
    for bad in ({"mineral": "x", "position": [1]}, {"mineral": "Opal", "position": [1, 2]}, {"position": [1, 2]},
                {"mineral": "opal"}, {"mineral": "opal", "position": [1, 2], "z": "high"}, {"mineral": "opal", "position": [1, 2], "kind": 1}):
        assert validate({"version": 1, "parcels": {}, "mineral_nodes": {"MN-0001": bad}}), bad
    assert validate({"version": 1, "parcels": {}, "mineral_nodes": {"M-1": {"mineral": "opal", "position": [1, 2]}}})


def test_build_mineral_nodes_is_local_with_terrain_or_own_z() -> None:
    nodes = {"MN-0002": {"mineral": "thundernite", "position": [110.5, 220], "z": 212.5},
             "MN-0001": {"mineral": "opal", "position": [105, 205]}}
    doc = build_mineral_nodes(nodes, [100.0, 200.0], FlatTerrain())
    assert doc is not None and doc["version"] == 1
    assert doc["nodes"] == [{"id": "MN-0001", "mineral": "opal", "x": 5.0, "y": 5.0, "z": 190.0},
                            {"id": "MN-0002", "mineral": "thundernite", "x": 10.5, "y": 20.0, "z": 212.5}]
    assert build_mineral_nodes({}, [0.0, 0.0], FlatTerrain()) is None
    assert [n["id"] for n in build_mineral_nodes(nodes, [100.0, 200.0], None)["nodes"]] == ["MN-0002"]  # no terrain: only a node with its own z

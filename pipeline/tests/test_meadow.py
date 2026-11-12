"""The meadow raster: file round trip and the hash that the game (MeadowData.cs) mirrors."""

import numpy as np

from suchohrad_pipeline import meadow


def _raster() -> meadow.Meadow:
    layers = [meadow.MeadowLayer("tuft", "tuft", "", 0.0, 3.0, (0.75, 1.25))]
    m = meadow.Meadow(2.0, 8.0, layers, 0.5, 0.75)
    m.array((0, 0))[0, 1:3, 1:3] = 200
    return m


def test_hash_matches_the_numbers_pinned_in_the_game_tests() -> None:
    h = meadow.hash01(np.array([0, 1]), np.array([0, 2]), 0)
    assert abs(h[0] - 0.4797017333778095) < 1e-15
    assert abs(meadow.hash01(np.array([1]), np.array([2]), 3)[0] - 0.3702791664253329) < 1e-15


def test_encode_decode_and_generate_are_deterministic() -> None:
    m = _raster()
    back = meadow.decode(meadow.encode(m))
    assert np.array_equal(back.chunks[(0, 0)], m.chunks[(0, 0)])
    a, b = meadow.generate(m, 0, 4, 0, 4), meadow.generate(back, 0, 4, 0, 4)
    assert a == b and a
    assert all(2.0 - 0.5 <= i["x"] <= 6.0 + 0.5 for i in a)


def test_zero_density_makes_nothing() -> None:
    m = _raster()
    m.chunks[(0, 0)][:] = 0
    assert meadow.generate(m, 0, 4, 0, 4) == []

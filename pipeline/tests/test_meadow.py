"""The meadow raster: file round trip and the hash that the game (MeadowData.cs) mirrors."""

import numpy as np

from drycastle_pipeline import meadow


def _raster() -> meadow.Meadow:
    layers = [meadow.MeadowLayer("tuft", 3.0, (0.75, 1.25), [[("tuft", 1.0)], [("tall", 0.75), ("tuft", 0.25)]])]
    m = meadow.Meadow(2.0, 8.0, layers, 0.5, 0.75, frozenset({"tuft"}))
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


def test_tint_follows_a_smooth_field_and_a_bias() -> None:
    n = meadow.tone_noise(np.array([10.0, 10.2]), np.array([4.0, 4.0]))
    assert abs(n[0] - n[1]) < 0.05  # smooth: close points share their mood
    assert meadow.tint_byte(0.5, 0.5)[()] == meadow.NEUTRAL_TINT
    assert meadow.tint_byte(0.5, 0.5, -40)[()] == 88
    assert meadow.tint_byte(1.0, 1.0, 500)[()] == 255  # clipped


def test_generated_tint_only_on_tint_kinds() -> None:
    m = _raster()
    m.tint_kinds = frozenset()
    assert all("tint" not in i for i in meadow.generate(m, 0, 4, 0, 4))
    m.tint_kinds = frozenset({"tuft"})
    got = meadow.generate(m, 0, 4, 0, 4)
    assert got and all(0 <= i["tint"] <= 255 for i in got)
    back = meadow.decode(meadow.encode(m))
    assert back.tint_kinds == {"tuft"} and meadow.generate(back, 0, 4, 0, 4) == got


def test_a_cells_mix_picks_its_kinds_and_dryness_biases_the_tint() -> None:
    m = _raster()
    plane = m.array((0, 0))
    plane[1, 1:3, 1:3] = (1 << 4) | 8  # mix 1: tall grass 3 in 4, neutral dryness
    tall = [i for i in meadow.generate(m, 0, 4, 0, 4) if i["kind"] == "tall"]
    assert tall and not [i for i in tall if "tint" in i]  # "tall" is not a tint kind here
    plane[1, 1:3, 1:3] = 0 | 15  # mix 0 (all tuft), driest: the tint rises by 7 steps
    dry = meadow.generate(m, 0, 4, 0, 4)
    plane[1, 1:3, 1:3] = 0 | 1
    fresh = meadow.generate(m, 0, 4, 0, 4)
    assert all(i["kind"] == "tuft" for i in dry) and len(dry) == len(fresh)
    assert sum(i["tint"] for i in dry) - sum(i["tint"] for i in fresh) > 100 * len(dry) // 2


def test_voronoi_patches_are_uniform_and_clumps_keep_the_mean() -> None:
    gx, gy = np.meshgrid(np.arange(0, 240), np.arange(0, 240))
    v = meadow.voronoi01(gx.ravel() + 0.5, gy.ravel() + 0.5, 3.0, 3000)
    assert abs(float(v.mean()) - 0.5) < 0.05
    assert len(np.unique(v)) < gx.size / 4  # many cells share a patch
    f = meadow.clump(gx.ravel() + 0.5, gy.ravel() + 0.5, 0.8, 3.0, 1500)
    assert 0.8 < float(f.mean()) < 1.2 and float(f.min()) < 0.4 < 1.5 < float(f.max())

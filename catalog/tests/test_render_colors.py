import time

import numpy as np

from asset_catalog.render.colors import color_name, dominant_colors


def _two_tone() -> tuple[np.ndarray, np.ndarray]:
    rgb = np.zeros((100, 100, 3), np.uint8)
    rgb[:, :50] = (200, 30, 30)
    rgb[:, 50:] = (220, 200, 160)
    mask = np.zeros((100, 100), bool)
    mask[10:90, 10:90] = True
    rgb[~mask] = np.random.default_rng(1).integers(0, 256, (int((~mask).sum()), 3))
    return rgb, mask


def test_red_and_beige_halves():
    rgb, mask = _two_tone()
    colors = dominant_colors(rgb, mask)
    assert {c.name for c in colors} == {"red", "beige"}
    assert len(colors) == 2
    assert all(abs(c.share - 0.5) < 0.05 for c in colors)


def test_flat_grey_and_low_chroma():
    rgb = np.full((20, 20, 3), 120, np.uint8)
    colors = dominant_colors(rgb, np.ones((20, 20), bool))
    assert [(c.name, c.hex, c.share) for c in colors] == [("grey", "#787878", 1.0)]
    assert color_name((128, 120, 120)) == "grey"


def test_empty_mask_and_determinism():
    rgb, mask = _two_tone()
    assert dominant_colors(rgb, np.zeros_like(mask)) == []
    assert dominant_colors(rgb, mask) == dominant_colors(rgb, mask)


def test_large_noisy_image_is_fast():
    rng = np.random.default_rng(2)
    rgb = rng.integers(0, 256, (768, 768, 3), dtype=np.uint8)
    mask = np.ones((768, 768), bool)
    mask[:, 400:] = False
    t = time.perf_counter()
    colors = dominant_colors(rgb, mask)
    assert time.perf_counter() - t < 0.5  # loose; real budget ~60 ms
    assert 1 <= len(colors) <= 5

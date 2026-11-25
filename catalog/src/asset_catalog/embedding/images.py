"""Picture preparation: the renders carry overlays (title, scale bar, human) that a picture
model would read as part of the object. Erase them, then optionally cut to the model."""

from __future__ import annotations

import numpy as np
from PIL import Image

from ..render.camera import HUMAN_GAP_M, HUMAN_M, HUMAN_WIDTH_M
from ..render.overlay import HUMAN_FILL, INK
from ..stages.render import ViewFrame

TITLE_ROWS = 50 / 768  # the title sits above this share of the height
BAR_ROWS = 696 / 768  # the scale bar sits below it; the model never reaches either
EDGE = 14  # colour distance from the background that counts as the model


def human_box(frame: ViewFrame, size: int) -> tuple[float, float, float, float]:
    """Where the front view's human figure was drawn, rebuilt from the layout rules."""
    k = size / 768
    left, right, top, bottom = 28 * k, size - 28 * k, 52 * k, size - 76 * k
    s = frame.px_per_m
    wm, hm = frame.extent_m
    wc, hc = wm + HUMAN_GAP_M + HUMAN_WIDTH_M, max(hm, HUMAN_M)
    cx0 = left + ((right - left) - wc * s) / 2
    ybot = top + ((bottom - top) + hc * s) / 2
    hx = cx0 + (wm + HUMAN_GAP_M) * s
    return hx, ybot - HUMAN_M * s, hx + HUMAN_WIDTH_M * s, ybot


def _near(a: np.ndarray, colour: tuple[int, int, int], tol: int) -> np.ndarray:
    return np.abs(a.astype(int) - np.array(colour)).max(axis=2) <= tol


def erase_overlays(img: Image.Image, frame: ViewFrame | None, view: str) -> np.ndarray:
    """RGB array with title, scale bar and human painted over with the background colour."""
    a = np.asarray(img.convert("RGB")).copy()
    h = a.shape[0]
    bg = a[0, 0].copy()
    a[: round(TITLE_ROWS * h)] = bg
    a[round(BAR_ROWS * h) :] = bg
    if view == "front" and frame is not None and frame.human_cue:
        x0, y0, x1, y1 = (round(v) for v in human_box(frame, h))
        a[max(y0 - 2, 0) : y1 + 3, max(x0 - 3, 0) : x1 + 4] = bg
        # the "1.8 m" label is wider than the figure: clear only its ink-coloured pixels
        cx, lx = (x0 + x1) // 2, slice(max((x0 + x1) // 2 - 30, 0), (x0 + x1) // 2 + 31)
        label = a[max(y0 - 32, 0) : y0, lx]
        ink = (
            _near(label, INK, 40)
            | _near(label, HUMAN_FILL, 24)
            | (np.abs(label.astype(int) - bg).max(axis=2) < 70) & (label.std(axis=2) < 6)
        )
        label[ink] = bg
        del cx
    return a


def crop_to_model(a: np.ndarray, margin: float) -> np.ndarray:
    """Square cut around the model with a margin, padded with the background colour."""
    bg = a[0, 0]
    ys, xs = np.nonzero(np.abs(a.astype(int) - bg.astype(int)).max(axis=2) > EDGE)
    if len(ys) == 0:
        return a
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    side = max(y1 - y0, x1 - x0)
    side = max(round(side * (1 + 2 * margin)), 32)
    cy, cx = (y0 + y1) // 2, (x0 + x1) // 2
    out = np.empty((side, side, 3), np.uint8)
    out[:] = bg
    top, left = cy - side // 2, cx - side // 2
    sy0, sx0 = max(top, 0), max(left, 0)
    sy1, sx1 = min(top + side, a.shape[0]), min(left + side, a.shape[1])
    out[sy0 - top : sy1 - top, sx0 - left : sx1 - left] = a[sy0:sy1, sx0:sx1]
    return out


def prepare(img: Image.Image, frame: ViewFrame | None, view: str, prep: str, margin: float):
    """The picture the model embeds. prep: raw | clean | crop."""
    if prep == "raw":
        return img.convert("RGB")
    a = erase_overlays(img, frame, view)
    if prep == "crop":
        a = crop_to_model(a, margin)
    return Image.fromarray(a, "RGB")

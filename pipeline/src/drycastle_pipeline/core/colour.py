"""Colour helpers: ``#rrggbb`` to RGB and the blend of two RGB colours."""


def hex_to_rgb(colour: str) -> list[int]:
    c = colour.lstrip("#")
    return [int(c[i : i + 2], 16) for i in (0, 2, 4)]


def blend(a: list[int], b: list[int], w: float) -> list[int]:
    """``a`` at ``w`` = 0, ``b`` at ``w`` = 1."""
    return [round(x * (1 - w) + y * w) for x, y in zip(a, b, strict=True)]

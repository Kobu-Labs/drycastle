"""What drafting and judging share: the API call, thumbnails, usage and cost accounting."""

from __future__ import annotations

import base64
import io
import json
import threading
from dataclasses import dataclass
from functools import cache
from pathlib import Path

import anthropic
from PIL import Image
from pydantic import BaseModel

from .. import config
from ..stages import measure, render
from ..store import Store
from . import io as evalio

MODEL = "claude-opus-5-5"
THUMB_PX = 512

# $ per million tokens: input, output, cache write (5 min, 1.25x input), cache read
PRICES: dict[str, tuple[float, float, float, float]] = {MODEL: (4.0, 20.0, 5.0, 0.2)}

_usage_lock = threading.Lock()


class Reply(BaseModel):
    data: dict
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_tokens: int = 0
    cache_write_tokens: int = 0


def new_client() -> anthropic.Anthropic:
    return anthropic.Anthropic(api_key=config.api_key(), max_retries=8)


def closed_object(props: dict) -> dict:
    """JSON schema for an object with every field required and no others allowed."""
    return {
        "type": "object",
        "additionalProperties": False,
        "required": list(props),
        "properties": props,
    }


def call_json(client: anthropic.Anthropic, params: dict) -> Reply | str:
    """The parsed JSON reply, or the reason there is none."""
    try:
        msg = client.messages.create(**params)
    except anthropic.RateLimitError as e:
        return f"rate limit: {e}"
    except anthropic.APIStatusError as e:
        return f"api {e.status_code}: {e.message}"
    except anthropic.APIConnectionError as e:
        return f"connection: {e}"
    if msg.stop_reason != "end_turn":
        return f"stop_reason {msg.stop_reason}"
    text = next((b.text for b in msg.content if b.type == "text"), "")
    try:
        data = json.loads(text)
    except ValueError as e:
        return f"bad output: {e}"
    u = msg.usage
    return Reply(
        data=data,
        input_tokens=u.input_tokens,
        output_tokens=u.output_tokens,
        cache_read_tokens=u.cache_read_input_tokens or 0,
        cache_write_tokens=u.cache_creation_input_tokens or 0,
    )


def cost_usd(model: str, r: Reply) -> float:
    pin, pout, pwrite, pread = PRICES[model]
    return (
        r.input_tokens * pin
        + r.output_tokens * pout
        + r.cache_write_tokens * pwrite
        + r.cache_read_tokens * pread
    ) / 1e6


def log_usage(kind: str, model: str, reply: Reply, root: Path | None = None) -> None:
    """One line per request, so the total spend on the set can always be added up."""
    path = (root or evalio.data_root()) / "usage.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    row = {"at": evalio.now(), "kind": kind, "model": model, **reply.model_dump(exclude={"data"})}
    row["usd"] = round(cost_usd(model, reply), 4)
    with _usage_lock, path.open("a") as f:
        f.write(json.dumps(row) + "\n")


def usage_totals(root: Path | None = None) -> dict[str, dict[str, float]]:
    """Per kind of request: requests, tokens and dollars logged so far."""
    path = (root or evalio.data_root()) / "usage.jsonl"
    out: dict[str, dict[str, float]] = {}
    if not path.exists():
        return out
    for line in path.read_text().splitlines():
        row = json.loads(line)
        t = out.setdefault(row["kind"], {"requests": 0, "in": 0, "out": 0, "cached": 0, "usd": 0})
        t["requests"] += 1
        t["in"] += row["input_tokens"] + row["cache_write_tokens"]
        t["cached"] += row["cache_read_tokens"]
        t["out"] += row["output_tokens"]
        t["usd"] += row["usd"]
    return out


@cache
def thumbnail_block(png: Path, px: int = THUMB_PX) -> dict:
    """The contact sheet downscaled to px on its long side, as an image content block."""
    with Image.open(png) as im:
        im = im.convert("RGB")
        scale = px / max(im.size)
        if scale < 1:
            im = im.resize(
                (round(im.width * scale), round(im.height * scale)), Image.Resampling.LANCZOS
            )
        buf = io.BytesIO()
        im.save(buf, "PNG", optimize=True)
    data = base64.standard_b64encode(buf.getvalue()).decode()
    return {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": data}}


@dataclass(frozen=True)
class AssetView:
    """What a judge or drafter may know of an asset: its picture and its measured size."""

    id: str
    size_m: tuple[float, float, float]
    size_class: str
    sheet: Path

    def size_text(self) -> str:
        x, y, z = self.size_m
        return (
            f"measured size {x:.2f} m wide (x) by {y:.2f} m tall (y) by {z:.2f} m deep (z); "
            f"size class {self.size_class}"
        )


def asset_view(asset_id: str, st: Store) -> AssetView | None:
    """None when the asset was not measured or rendered successfully."""
    m = st.get(measure.STAGE, asset_id, measure.Measure)
    r = st.get(render.STAGE, asset_id, render.RenderPayload)
    if m is None or m.geometry is None or r is None or r.status != "ok":
        return None
    g = m.geometry
    return AssetView(asset_id, tuple(g.size_m), g.size_class, st.root / r.contact_sheet)

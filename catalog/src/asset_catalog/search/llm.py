"""One structured-output call to Claude, shared by query parsing and reranking."""

from __future__ import annotations

import json
from functools import cache
from pathlib import Path

import anthropic

from .. import config
from ..store import make_key

# When set, answers are stored here by a hash of the whole request and reused. Evaluation
# runs set it so the same configuration scores the same twice and reruns cost nothing.
CACHE_DIR: Path | None = None


class LlmError(RuntimeError):
    """The call gave no usable answer; the caller decides how to carry on without it."""


@cache
def client() -> anthropic.Anthropic:
    return anthropic.Anthropic(api_key=config.api_key(), max_retries=4)


def ask(model: str, effort: str, system: str, user: str, schema: dict, max_tokens: int) -> dict:
    if CACHE_DIR is None:
        return _ask(model, effort, system, user, schema, max_tokens)
    path = CACHE_DIR / f"{make_key(model, effort, system, user, schema)}.json"
    if path.exists():
        return json.loads(path.read_text())
    answer = _ask(model, effort, system, user, schema, max_tokens)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(answer))
    return answer


def _ask(model: str, effort: str, system: str, user: str, schema: dict, max_tokens: int) -> dict:
    try:
        msg = client().messages.create(
            model=model,
            max_tokens=max_tokens,
            system=[{"type": "text", "text": system, "cache_control": {"type": "ephemeral"}}],
            output_config={"effort": effort, "format": {"type": "json_schema", "schema": schema}},
            messages=[{"role": "user", "content": user}],
        )
    except anthropic.RateLimitError as e:
        raise LlmError(f"rate limit: {e}") from e
    except anthropic.APIStatusError as e:
        raise LlmError(f"api {e.status_code}: {e.message}") from e
    except anthropic.APIConnectionError as e:
        raise LlmError(f"connection: {e}") from e
    if msg.stop_reason != "end_turn":
        raise LlmError(f"stop_reason {msg.stop_reason}")
    try:
        return json.loads(next(b.text for b in msg.content if b.type == "text"))
    except (StopIteration, ValueError) as e:
        raise LlmError(f"bad output: {e}") from e

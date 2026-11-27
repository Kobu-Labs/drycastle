"""The model's part of facade_pipeline, done cheaply: the questions of a run folder answered by one API call
(pictures + the ask file, no agent around it), or answers an agent replied with put into their folders.

    uv run python -m drycastle_pipeline.photos.facade_pipeline read  w378900371 w378900208 ...   (needs ANTHROPIC_API_KEY)
    uv run python -m drycastle_pipeline.photos.facade_pipeline answers REPLY.json                 ({"<run folder or id>": {...}, ...})

Which question a folder is waiting on: ``ends_ask.md`` without ``ends.json``, else ``walls_ask.md`` without
``names.json``. A folder with nothing open is skipped (``--again`` asks its last question once more).
"""

from __future__ import annotations

import base64
import json
import re
from pathlib import Path

MODEL = "claude-sonnet-5-5"
EFFORT = "medium"
STAGES = (("ends_ask.md", "ends.json"), ("walls_ask.md", "names.json"))


def pending(out: Path, again: bool = False) -> tuple[str, str] | None:
    """(ask file, answer file) the folder waits on; with ``again`` the last one that has an ask file."""
    have = [(a, g) for a, g in STAGES if (out / a).exists()]
    if again:
        return have[-1] if have else None
    return next(((a, g) for a, g in have if not (out / g).exists()), None)


def pictures(out: Path, ask: str) -> list[Path]:
    """The picture files the ask file names, in its order (names with no file, like ``wall_...png``, are prose)."""
    seen: list[Path] = []
    for name in re.findall(r"`([^`]+\.png)`", ask):
        p = Path(name) if name.startswith("/") else out / name
        if p.is_file() and p not in seen:
            seen.append(p)
    return seen


def template(ask: str) -> dict:
    return json.loads(ask[ask.rindex("\n{") :])


def check(answer: dict, ask: str) -> None:
    """The answer has the template's keys (and, per wall, the template's) and no ``"?"`` left."""
    want = template(ask)
    if set(answer) != set(want):
        raise ValueError(f"keys differ from the template: missing {sorted(set(want) - set(answer))}, extra {sorted(set(answer) - set(want))}")
    for wall, w in want.get("walls", {}).items() if isinstance(want.get("walls"), dict) else ():
        got = answer["walls"].get(wall)
        if not isinstance(got, dict) or set(got) != set(w) or set(got["boxes"]) != set(w["boxes"]):
            raise ValueError(f"wall {wall}: keys differ from the template")
    if '"?"' in json.dumps(answer):
        raise ValueError('a "?" was left unanswered')


def save(out: Path, answer: dict, again: bool = False, stage: str | None = None) -> Path:
    """Check the answer against its ask file and write it. ``stage`` ("ends" or "walls") names the question; else the open one."""
    ask_name, answer_name = _named(out, stage) if stage else _open(out, again)
    check(answer, (out / ask_name).read_text())
    (out / answer_name).write_text(json.dumps(answer, indent=1) + "\n")
    return out / answer_name


def _open(out: Path, again: bool) -> tuple[str, str]:
    stage = pending(out, again)
    if stage is None:
        raise ValueError(f"{out}: no question open")
    return stage


def _named(out: Path, stage: str) -> tuple[str, str]:
    """(ask file, answer file) of the named stage; its ask file must exist."""
    if stage not in ("ends", "walls"):
        raise ValueError(f"stage must be ends or walls, not {stage!r}")
    ask, answer = STAGES[0 if stage == "ends" else 1]
    if not (out / ask).exists():
        raise ValueError(f"{out}: no {ask} to answer")
    return ask, answer


def stage_answers(reply: Path, again: bool, run_dir) -> None:
    """Put an agent's reply ({folder or id: answer}) into the folders; a bad answer is reported and left out."""
    text = reply.read_text()
    data = json.loads(text[text.index("{") : text.rindex("}") + 1])
    for key, answer in data.items():
        out = run_dir(key)
        try:
            print(f"written: {save(out, answer, again)}")
        except (ValueError, KeyError, TypeError) as e:
            print(f"PROBLEM: {out}: {e}: not written")


def image_block(path: Path) -> dict:
    return {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": base64.standard_b64encode(path.read_bytes()).decode()}}


def json_object(text: str) -> dict:
    """The JSON object in a model's reply (anything before its first ``{`` and after its last ``}`` is dropped)."""
    return json.loads(text[text.index("{") : text.rindex("}") + 1])


def ask_model(client, content: list[dict], label: str) -> tuple[str, dict, str]:
    """One API call: (the reply's text, token usage, stop reason). An API error ends the run (SystemExit with its message)."""
    try:
        r = client.beta.messages.create(
            model=MODEL,
            max_tokens=16000,
            output_config={"effort": EFFORT},
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            messages=[{"role": "user", "content": content}],
        )
    except Exception as e:  # the SDK is an optional extra: its error classes are looked at only if it is loaded
        import sys

        anthropic = sys.modules.get("anthropic")
        if anthropic is not None and isinstance(e, anthropic.RateLimitError):
            raise SystemExit(f"{label}: rate limited after the SDK's retries: {e}") from e
        if anthropic is not None and isinstance(e, anthropic.APIStatusError):
            raise SystemExit(f"{label}: the API answered {e.status_code}: {e.message}") from e
        if anthropic is not None and isinstance(e, anthropic.APIConnectionError):
            raise SystemExit(f"{label}: no connection to the API: {e}") from e
        raise
    usage = {"in": r.usage.input_tokens, "out": r.usage.output_tokens}
    return "".join(b.text for b in r.content if b.type == "text"), usage, r.stop_reason


def read_one(client, out: Path, again: bool, stage: str | None = None) -> dict | None:
    """One API call for the folder's open question. Returns the token usage (plus ``problem``, why nothing was written, when
    the reply was no good), or None if nothing was open. ``stage`` ("ends" | "walls") asks that question though it is
    answered (its answer file is then overwritten)."""
    if stage:
        try:
            ask_name, _ = _named(out, stage)
        except ValueError as e:
            print(f"{out}: no question open ({e})")
            return None
    else:
        found = pending(out, again)
        if found is None:
            print(f"{out}: no question open")
            return None
        ask_name = found[0]
    text, usage, stop_reason = ask_model(client, question(out, ask_name), str(out))
    return take(out, text, usage, stop_reason, again, stage)


def question(out: Path, ask_name: str) -> list[dict]:
    """The message content of one question: its pictures, then the ask file."""
    ask = (out / ask_name).read_text()
    content: list[dict] = []
    for p in pictures(out, ask):
        content.append({"type": "text", "text": f"Picture `{p.name if p.parent == out else p}`:"})
        content.append(image_block(p))
    content.append({"type": "text", "text": ask + "\nReply with the JSON object only, nothing before or after it."})
    return content


def take(out: Path, text: str, usage: dict, stop_reason: str, again: bool, stage: str | None) -> dict:
    """Check a reply and write it (or report why not); the token usage, with ``problem`` when nothing was written."""
    if stop_reason != "end_turn":
        problem = f"the model stopped with {stop_reason}"
        print(f"PROBLEM: {out}: {problem}: not written")
        return {**usage, "problem": problem}
    try:
        print(f"written: {save(out, json_object(text), again, stage)}  ({usage['in']} in, {usage['out']} out)")
    except (ValueError, KeyError, TypeError) as e:
        print(f"PROBLEM: {out}: {e}: not written")
        return {**usage, "problem": str(e)}
    return usage


def stage_read_batch(outs: list[Path], again: bool, poll_s: float = 20.0) -> None:
    """The open question of every folder as one Message Batch (half the price; usually minutes, at most 24 h), then the
    replies checked and written as ``read`` does. No server-side fallback in a batch: a refusal is reported."""
    import time

    import anthropic

    from drycastle_pipeline.core.config import load_dotenv

    load_dotenv()
    client = anthropic.Anthropic()
    asks = {}
    for i, out in enumerate(outs):
        found = pending(out, again)
        if found is None:
            print(f"{out}: no question open")
            continue
        asks[f"q{i}"] = (out, found[0])
    if not asks:
        return
    batch = client.messages.batches.create(requests=[
        {"custom_id": cid, "params": {"model": MODEL, "max_tokens": 16000, "output_config": {"effort": EFFORT},
                                      "messages": [{"role": "user", "content": question(out, ask)}]}}
        for cid, (out, ask) in asks.items()])
    print(f"batch {batch.id}: {len(asks)} question(s) sent", flush=True)
    while (batch := client.messages.batches.retrieve(batch.id)).processing_status != "ended":
        time.sleep(poll_s)
    total = {"in": 0, "out": 0}
    for res in client.messages.batches.results(batch.id):
        out, _ = asks[res.custom_id]
        if res.result.type != "succeeded":
            print(f"PROBLEM: {out}: batch request {res.result.type}: not written")
            continue
        m = res.result.message
        usage = {"in": m.usage.input_tokens, "out": m.usage.output_tokens}
        take(out, "".join(b.text for b in m.content if b.type == "text"), usage, m.stop_reason, again, None)
        total = {k: total[k] + usage[k] for k in total}
    print(f"tokens: {total['in']} in, {total['out']} out over {len(asks)} question(s) (batch: half price)")


def stage_read(outs: list[Path], again: bool) -> None:
    import anthropic  # the pipeline runs without it; only this command needs it (uv sync --extra reader)

    from drycastle_pipeline.core.config import load_dotenv

    load_dotenv()
    client = anthropic.Anthropic()
    total = {"in": 0, "out": 0}
    for out in outs:
        u = read_one(client, out, again)
        if u:
            total = {k: total[k] + u[k] for k in total}
    print(f"tokens: {total['in']} in, {total['out']} out over {len(outs)} folder(s)")

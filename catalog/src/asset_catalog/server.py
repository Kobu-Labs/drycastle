"""The local search page and its JSON API. Serves 127.0.0.1 only: the assets are private.

Endpoints run in worker threads, so a search that waits on the model never blocks the page,
the pictures or another request.
"""

from __future__ import annotations

import json
import queue
import sys
import threading
import time
from pathlib import Path

import uvicorn
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field
from starlette.middleware.base import BaseHTTPMiddleware

from .search.engine import RETRIEVERS
from .search.parse import Parsed
from .search.types import Filters
from .service import Service

WEB_DIR = Path(__file__).parent / "web"
HOST = "127.0.0.1"
# Everything the page loads comes from this server: the browser enforces it.
CSP = (
    "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; "
    "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
)


class SearchRequest(BaseModel):
    query: str
    filters: Filters = Filters()
    k: int = Field(30, ge=1, le=100)
    use: list[str] = list(RETRIEVERS)
    parse: bool = True
    rerank: bool = True
    understanding: Parsed | None = None


class SimilarRequest(BaseModel):
    asset_id: str
    k: int = Field(30, ge=1, le=100)
    filters: Filters = Filters()


class BrowseRequest(BaseModel):
    filters: Filters = Filters()
    offset: int = Field(0, ge=0)
    limit: int = Field(48, ge=1, le=200)


class FacetsRequest(BaseModel):
    filters: Filters = Filters()


class _Guard(BaseHTTPMiddleware):
    """Refuses requests whose Host is not this machine (DNS rebinding) and adds the headers."""

    def __init__(self, app, port: int):
        super().__init__(app)
        self.hosts = {f"{h}:{port}" for h in (HOST, "localhost")}

    async def dispatch(self, request: Request, call_next):
        if request.headers.get("host") not in self.hosts:
            return _error(403, "This server only answers on localhost.", "Open the printed URL.")
        resp = await call_next(request)
        resp.headers["Content-Security-Policy"] = CSP
        resp.headers["X-Content-Type-Options"] = "nosniff"
        resp.headers["Referrer-Policy"] = "no-referrer"
        resp.headers.setdefault("Cache-Control", "no-cache")
        return resp


def _error(status: int, error: str, hint: str) -> JSONResponse:
    return JSONResponse({"error": error, "hint": hint}, status_code=status)


def create_app(service: Service, port: int) -> FastAPI:
    app = FastAPI(title="asset catalog", docs_url="/api/docs", openapi_url="/api/openapi.json")
    app.add_middleware(_Guard, port=port)

    @app.exception_handler(ValueError)
    def bad_input(_: Request, e: ValueError):
        return _error(422, str(e), "Change the request and try again.")

    @app.exception_handler(KeyError)
    def unknown(_: Request, e: KeyError):
        return _error(
            404, f"Unknown asset or view: {e.args[0]}", "Search again; the id may be stale."
        )

    @app.exception_handler(FileNotFoundError)
    def missing(_: Request, e: FileNotFoundError):
        return _error(404, f"File not found: {e}", "Re-run `catalog render` for this asset.")

    @app.exception_handler(RequestValidationError)
    def invalid(_: Request, e: RequestValidationError):
        first = e.errors()[0]
        where = ".".join(str(p) for p in first["loc"] if isinstance(p, str) and p != "body")
        return _error(
            422, f"Invalid request at {where or 'the body'}: {first['msg']}", "Fix that field."
        )

    @app.exception_handler(Exception)
    def failed(_: Request, e: Exception):
        print(f"[serve] {type(e).__name__}: {e}", file=sys.stderr)
        return _error(500, f"{type(e).__name__}: {e}", "See the terminal running `catalog serve`.")

    @app.get("/api/status")
    def status():
        return service.status()

    @app.get("/api/vocab")
    def vocab():
        return service.vocabulary()

    @app.post("/api/facets")
    def facets(req: FacetsRequest):
        return service.facets(req.filters)

    @app.post("/api/browse")
    def browse(req: BrowseRequest):
        return service.browse(req.filters, req.offset, req.limit)

    @app.post("/api/search")
    def search(req: SearchRequest):
        return service.search(**_kwargs(req))

    @app.post("/api/search/stream")
    def search_stream(req: SearchRequest):
        """Newline-delimited JSON: {"event": "stage", "stage": ...} as each stage starts, then
        {"event": "done", "response": ...} or {"event": "error", "error": ..., "hint": ...}."""
        events: queue.Queue = queue.Queue()

        def work() -> None:
            try:
                resp = service.search(
                    **_kwargs(req),
                    progress=lambda s: events.put({"event": "stage", "stage": s}),
                )
                events.put({"event": "done", "response": resp.model_dump(mode="json")})
            except Exception as e:  # noqa: BLE001 - reported to the page, which shows it
                print(f"[serve] {type(e).__name__}: {e}", file=sys.stderr)
                hint = "Change the request." if isinstance(e, ValueError) else "See the terminal."
                events.put({"event": "error", "error": f"{type(e).__name__}: {e}", "hint": hint})
            events.put(None)

        threading.Thread(target=work, daemon=True).start()

        def lines():
            while (item := events.get()) is not None:
                yield json.dumps(item) + "\n"

        return StreamingResponse(lines(), media_type="application/x-ndjson")

    @app.post("/api/similar")
    def similar(req: SimilarRequest):
        return service.similar(req.asset_id, req.k, req.filters)

    @app.get("/api/asset")
    def asset(id: str):
        return service.asset(id)

    @app.get("/api/image")
    def image(id: str, view: str):
        return FileResponse(
            service.image_path(id, view),
            media_type="image/png",
            headers={"Cache-Control": "private, max-age=300"},
        )

    app.mount("/", StaticFiles(directory=WEB_DIR, html=True), name="web")
    return app


def _kwargs(req: SearchRequest) -> dict:
    return {f: getattr(req, f) for f in SearchRequest.model_fields}


def serve(model: str, port: int) -> None:
    """Load everything, warm the models, then serve until interrupted."""
    start = time.perf_counter()
    try:
        service = Service.open(model)
    except (FileNotFoundError, RuntimeError) as e:
        raise SystemExit(f"cannot start: {e}") from e
    print(f"loading models for {service.index.size} assets ...", flush=True)
    try:
        warm = service.warm()
    except FileNotFoundError as e:
        raise SystemExit(f"cannot start: {e}") from e
    took = ", ".join(f"{n} {ms / 1000:.1f}s" for n, ms in warm.items())
    print(f"ready in {time.perf_counter() - start:.1f}s ({took})")
    if problem := service.llm_problem():
        print(f"warning: {problem}", file=sys.stderr)
    print(f"http://{HOST}:{port}/", flush=True)
    uvicorn.run(create_app(service, port), host=HOST, port=port, log_level="warning")

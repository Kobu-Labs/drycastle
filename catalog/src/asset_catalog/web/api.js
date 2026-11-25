// The JSON API. Errors carry what happened and what to do next.

export class ApiError extends Error {
  constructor(message, hint, status) {
    super(message);
    this.hint = hint;
    this.status = status;
  }
}

const UNREACHABLE = new ApiError(
  "Cannot reach the catalog server.",
  "Is `catalog serve` still running? Start it again with: uv run catalog serve",
  0,
);

async function failure(resp) {
  let body = {};
  try {
    body = await resp.json();
  } catch {
    /* not JSON */
  }
  return new ApiError(body.error ?? `The server answered ${resp.status}.`, body.hint ?? "Try again.", resp.status);
}

async function request(path, { body, signal } = {}) {
  let resp;
  try {
    resp = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: body === undefined ? {} : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch (e) {
    if (e.name === "AbortError") throw e;
    throw UNREACHABLE;
  }
  if (!resp.ok) throw await failure(resp);
  return resp.json();
}

export const status = () => request("/api/status");
export const vocab = () => request("/api/vocab");
export const facets = (filters, signal) => request("/api/facets", { body: { filters }, signal });
export const browse = (filters, offset, limit, signal) =>
  request("/api/browse", { body: { filters, offset, limit }, signal });
export const similar = (assetId, filters, k, signal) =>
  request("/api/similar", { body: { asset_id: assetId, filters, k }, signal });
export const asset = (id, signal) => request(`/api/asset?id=${encodeURIComponent(id)}`, { signal });

export const imageUrl = (id, view) => `/api/image?id=${encodeURIComponent(id)}&view=${view}`;

/** Search with a progress callback per stage: onStage("parse" | "retrieve" | "rerank"). */
export async function search(req, onStage, signal) {
  let resp;
  try {
    resp = await fetch("/api/search/stream", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(req),
      signal,
    });
  } catch (e) {
    if (e.name === "AbortError") throw e;
    throw UNREACHABLE;
  }
  if (!resp.ok) throw await failure(resp);
  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done });
    let nl;
    while ((nl = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, nl).trim();
      buffer = buffer.slice(nl + 1);
      if (!line) continue;
      const event = JSON.parse(line);
      if (event.event === "stage") onStage(event.stage);
      else if (event.event === "done") return event.response;
      else if (event.event === "error") throw new ApiError(event.error, event.hint, 500);
    }
    if (done) throw UNREACHABLE;
  }
}

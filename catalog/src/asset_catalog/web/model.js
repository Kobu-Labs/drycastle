// What the page knows: the filter vocabulary, the page state, its URL form and saved preferences.

export const FACETS = [
  { facet: "kind", field: "kinds", label: "Kind", icon: "tag" },
  { facet: "category", field: "categories", label: "Category", icon: "folder", tree: true },
  { facet: "material", field: "materials", label: "Material", icon: "brick" },
  { facet: "setting", field: "settings", label: "Setting", icon: "pin" },
  { facet: "placement", field: "placements", label: "Placement", icon: "anchor" },
  { facet: "function", field: "functions", label: "Function", icon: "bolt" },
  { facet: "style", field: "styles", label: "Style", icon: "sparkle" },
  { facet: "condition", field: "condition", label: "Condition", icon: "wrench" },
  { facet: "colour", field: "colours", label: "Colour", icon: "palette" },
  { facet: "pack", field: "packs", label: "Pack", icon: "cube" },
  { facet: "size_class", field: "size_classes", label: "Size class", icon: "ruler" },
];
export const FACET_BY_FIELD = Object.fromEntries(FACETS.map((f) => [f.field, f]));

export const SIZES = [
  { label: "Width", min: "min_width_m", max: "max_width_m", pmin: "minw", pmax: "maxw" },
  { label: "Height", min: "min_height_m", max: "max_height_m", pmin: "minh", pmax: "maxh" },
  { label: "Depth", min: "min_depth_m", max: "max_depth_m", pmin: "mind", pmax: "maxd" },
  { label: "Longest side", min: "min_longest_m", max: "max_longest_m", pmin: "minl", pmax: "maxl" },
];

export const RETRIEVERS = [
  { name: "keyword", label: "Keyword retriever", hint: "Words in names, aliases, descriptions and likely queries" },
  { name: "text-embedding", label: "Text embedding retriever", hint: "Meaning of the request against the written descriptions" },
  { name: "image-embedding", label: "Image embedding retriever", hint: "The request against the rendered pictures" },
];
export const DEFAULT_USE = RETRIEVERS.map((r) => r.name);

export const GRADES = { 3: "Ideal", 2: "Good", 1: "Usable" };
export const VIEWS = [
  { id: "contact_sheet", label: "Contact sheet" },
  { id: "three_quarter", label: "Three-quarter" },
  { id: "front", label: "Front" },
  { id: "top", label: "Top" },
  { id: "back", label: "Back" },
];
export const PAGE_SIZE = 48;
export const RESULT_COUNT = 30;

// ---------------------------------------------------------------- filters

export function emptyFilters() {
  const f = { rigged: null };
  for (const x of FACETS) f[x.field] = [];
  for (const s of SIZES) f[s.min] = f[s.max] = null;
  return f;
}

/** The filters as the API wants them: nothing that does not constrain. */
export function cleanFilters(f) {
  const out = {};
  for (const x of FACETS) if (f[x.field].length) out[x.field] = f[x.field];
  for (const s of SIZES) for (const k of [s.min, s.max]) if (f[k] != null) out[k] = f[k];
  if (f.rigged != null) out.rigged = f.rigged;
  return out;
}

export function filterCount(f) {
  const c = cleanFilters(f);
  return Object.values(c).reduce((n, v) => n + (Array.isArray(v) ? v.length : 1), 0);
}

// ---------------------------------------------------------------- preferences (localStorage)

const PREFS_KEY = "catalog.prefs.v1";
const PREF_DEFAULTS = {
  theme: "auto", // auto | light | dark
  density: "m", // s | m | l
  filtersOpen: null, // null: open when the window is wide
  use: DEFAULT_USE,
  parse: true,
  rerank: true,
  showEmpty: false,
  facetsOpen: { kind: true, category: true },
};

export function loadPrefs() {
  try {
    return { ...PREF_DEFAULTS, ...JSON.parse(localStorage.getItem(PREFS_KEY) ?? "{}") };
  } catch {
    return { ...PREF_DEFAULTS };
  }
}

export function savePrefs(prefs) {
  try {
    localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
  } catch {
    /* private mode: preferences last for this tab only */
  }
}

// ---------------------------------------------------------------- the page state and its URL

/** The state is the URL: everything a bookmark needs, nothing else. */
export function newState(prefs) {
  return {
    q: "",
    filters: emptyFilters(),
    use: [...prefs.use],
    parse: prefs.parse,
    rerank: prefs.rerank,
    understanding: null, // an edited reading of the query: {text, hard, soft}
    sim: null, // asset id: show assets similar to it
    asset: null, // asset id: details open
    page: 0,
  };
}

export function stateFromUrl(search, prefs) {
  const p = new URLSearchParams(search);
  const s = newState(prefs);
  s.q = p.get("q") ?? "";
  s.sim = p.get("sim");
  s.asset = p.get("asset");
  s.page = Math.max(0, (Number(p.get("page")) || 1) - 1);
  for (const x of FACETS) s.filters[x.field] = p.getAll(x.facet);
  for (const z of SIZES) {
    for (const [key, param] of [[z.min, z.pmin], [z.max, z.pmax]]) {
      const v = p.get(param);
      if (v !== null && v !== "" && Number.isFinite(Number(v))) s.filters[key] = Number(v);
    }
  }
  if (p.has("rigged")) s.filters.rigged = p.get("rigged") === "1";
  if (p.has("use")) {
    const names = p.get("use").split(",").filter((n) => DEFAULT_USE.includes(n));
    if (names.length) s.use = names;
  }
  if (p.has("parse")) s.parse = p.get("parse") !== "0";
  if (p.has("rerank")) s.rerank = p.get("rerank") !== "0";
  if (p.has("u")) {
    try {
      s.understanding = JSON.parse(p.get("u"));
    } catch {
      s.understanding = null;
    }
  }
  return s;
}

export function toQuery(s, { asset = true } = {}) {
  const p = new URLSearchParams();
  if (s.q) p.set("q", s.q);
  if (s.sim) p.set("sim", s.sim);
  for (const x of FACETS) for (const v of s.filters[x.field]) p.append(x.facet, v);
  for (const z of SIZES) {
    if (s.filters[z.min] != null) p.set(z.pmin, String(s.filters[z.min]));
    if (s.filters[z.max] != null) p.set(z.pmax, String(s.filters[z.max]));
  }
  if (s.filters.rigged != null) p.set("rigged", s.filters.rigged ? "1" : "0");
  if (s.q) {
    p.set("use", s.use.join(","));
    p.set("parse", s.parse ? "1" : "0");
    p.set("rerank", s.rerank ? "1" : "0");
    if (s.understanding) p.set("u", JSON.stringify(s.understanding));
  }
  if (!s.q && !s.sim && s.page > 0) p.set("page", String(s.page + 1));
  if (asset && s.asset) p.set("asset", s.asset);
  return p.toString();
}

export const hrefFor = (s, patch) => `?${toQuery({ ...s, ...patch })}`;

/** The constraints a parsed reading holds that the user did not state in the panel. */
export function parsedOnly(hard, filters) {
  const out = {};
  for (const [key, value] of Object.entries(hard)) {
    if (key === "exclude_ids") continue;
    const own = filters[key];
    if (Array.isArray(value)) {
      if (value.length && !(own && own.length)) out[key] = value;
    } else if (value != null && own == null) out[key] = value;
  }
  return out;
}

/** Display text for one constraint. */
export function constraintLabel(key, value) {
  const facet = FACET_BY_FIELD[key];
  if (facet) return `${facet.label} = ${value.replaceAll("/", " / ")}`;
  if (key === "rigged") return value ? "Rigged" : "Not rigged";
  for (const z of SIZES) {
    if (key === z.min) return `${z.label} at least ${value} m`;
    if (key === z.max) return `${z.label} at most ${value} m`;
  }
  return `${key} = ${value}`;
}

/** Flatten {field: [values] | scalar} into [{key, value}] chips. */
export function constraintList(c) {
  const out = [];
  for (const [key, value] of Object.entries(c)) {
    if (key === "exclude_ids") continue;
    if (Array.isArray(value)) for (const v of value) out.push({ key, value: v });
    else if (value != null) out.push({ key, value });
  }
  return out;
}

// The page: state in the URL, searches, filters, results, keyboard. Everything else is in modules.

import * as api from "./api.js";
import { byId, el, fileName, fmtMs, icon, isTyping, plural } from "./dom.js";
import { closeLightbox, hideDrawer, isDrawerOpen, isLightboxOpen, lightboxKey, showDrawer, showDrawerError, showDrawerLoading } from "./drawer.js";
import { buildSizeControls, renderFacets, renderStages, syncSizeControls } from "./filters.js";
import { attachGridKeys, renderCard, setActiveCard, skeletonCards } from "./grid.js";
import {
  DEFAULT_USE, PAGE_SIZE, RESULT_COUNT,
  cleanFilters, constraintLabel, constraintList, emptyFilters, filterCount, hrefFor, loadPrefs,
  parsedOnly, savePrefs, stateFromUrl, toQuery,
} from "./model.js";
import { banner, toast } from "./notify.js";

const SLOW_MS = 100; // show progress for anything slower than this
const NARROW = "(max-width: 60em)";

const S = {
  prefs: loadPrefs(),
  state: null, // the URL state, see model.js
  status: null,
  vocab: null,
  facets: null,
  facetKey: null,
  view: null, // {mode: "search" | "similar" | "browse", ...} what the grid shows
  timings: null,
  openTree: new Set(),
  showAll: new Set(),
  runId: 0,
  abort: null,
  facetAbort: null,
  drawerAsset: null,
  drawerAbort: null,
  opener: null,
  lastKey: null,
  focusFirst: false,
  steps: [],
  stepStart: 0,
  loadTimer: null,
  ticker: null,
  dismissedProblem: false,
  cache: new Map(), // search key -> response: Back and Forward do not ask the model again
  fresh: false, // the next run must not use the cache
};
const CACHE_MAX = 12;

const $q = () => byId("q");

// ---------------------------------------------------------------- navigation: the state is the URL

function navigate(patch, { push = true } = {}) {
  const before = { asset: S.state.asset };
  Object.assign(S.state, patch);
  const url = `${location.pathname}?${toQuery(S.state)}`;
  // only the step that opened the drawer is flagged, so Esc (history.back) undoes exactly it
  const opened = Boolean(S.state.asset) && !before.asset;
  const keep = Boolean(S.state.asset) && history.state?.drawer;
  if (url !== location.pathname + location.search) {
    if (push) history.pushState({ drawer: opened }, "", url);
    else history.replaceState({ drawer: opened || keep }, "", url);
  }
  sync();
}

function sync() {
  const key = toQuery(S.state, { asset: false });
  if (key !== S.lastKey) {
    S.lastKey = key;
    run();
  }
  syncDrawer();
  syncControls();
}

function syncControls() {
  const input = $q();
  if (document.activeElement !== input) input.value = S.state.q;
  byId("q-clear").hidden = input.value === "";
  const n = filterCount(S.state.filters);
  const badge = byId("filters-count");
  badge.hidden = n === 0;
  badge.textContent = n;
  byId("clear-all").disabled = n === 0;
  syncSizeControls(S.state.filters);
  renderStagePanel();
  markCurrentCard();
}

window.addEventListener("popstate", () => {
  S.state = stateFromUrl(location.search, S.prefs);
  openAncestors();
  sync();
});

// ---------------------------------------------------------------- preferences

function applyPrefs() {
  const root = document.documentElement;
  if (S.prefs.theme === "auto") delete root.dataset.theme;
  else root.dataset.theme = S.prefs.theme;
  byId("theme-label").textContent = { auto: "Auto", light: "Light", dark: "Dark" }[S.prefs.theme];
  root.dataset.density = S.prefs.density;
  for (const b of document.querySelectorAll("#density [data-density]")) b.setAttribute("aria-pressed", String(b.dataset.density === S.prefs.density));
  setFiltersOpen(S.prefs.filtersOpen ?? !matchMedia(NARROW).matches, false);
  byId("show-empty").checked = S.prefs.showEmpty;
}

function setPref(patch) {
  Object.assign(S.prefs, patch);
  savePrefs(S.prefs);
}

function setFiltersOpen(open, save = true) {
  byId("layout").dataset.filters = open ? "open" : "closed";
  byId("filters-toggle").setAttribute("aria-expanded", String(open));
  if (save) setPref({ filtersOpen: open });
}

const filtersOpen = () => byId("layout").dataset.filters === "open";

// ---------------------------------------------------------------- stages and filters

function keepFocus(fn) {
  const id = document.activeElement?.id;
  fn();
  if (id) byId(id)?.focus({ preventScroll: true });
}

function renderStagePanel() {
  keepFocus(() => renderStages(byId("stages"), { state: S.state, status: S.status, timings: S.timings, h: stageHandlers }));
}

const stageHandlers = {
  toggleRetriever(name) {
    const on = S.state.use.includes(name);
    const use = DEFAULT_USE.filter((n) => (n === name ? !on : S.state.use.includes(n)));
    if (!use.length) {
      toast({ kind: "warn", text: "At least one retriever has to stay on.", hint: "Switch another one on before switching this one off." });
      renderStagePanel();
      return;
    }
    setPref({ use });
    navigate({ use });
  },
  toggleParse() {
    setPref({ parse: !S.state.parse });
    navigate({ parse: !S.state.parse });
  },
  toggleRerank() {
    setPref({ rerank: !S.state.rerank });
    navigate({ rerank: !S.state.rerank });
  },
};

function openAncestors() {
  for (const path of S.state.filters.categories) {
    const parts = path.split("/");
    for (let i = 1; i < parts.length; i++) S.openTree.add(parts.slice(0, i).join("/"));
  }
}

const facetHandlers = {
  toggleTerm(field, term) {
    const list = S.state.filters[field];
    const i = list.indexOf(term);
    if (i >= 0) list.splice(i, 1);
    else list.push(term);
    if (field === "categories") openAncestors();
    navigate({ page: 0, asset: null });
  },
  toggleSection(facet) {
    setPref({ facetsOpen: { ...S.prefs.facetsOpen, [facet]: !S.prefs.facetsOpen[facet] } });
    renderFacetsNow();
  },
  toggleNode(path) {
    if (!S.openTree.delete(path)) S.openTree.add(path);
    renderFacetsNow();
  },
  toggleShowAll(facet) {
    if (!S.showAll.delete(facet)) S.showAll.add(facet);
    renderFacetsNow();
  },
};

const sizeHandlers = {
  setSize(key, value) {
    S.state.filters[key] = Number.isFinite(value) ? value : null;
    navigate({ page: 0 });
  },
  setRigged(value) {
    S.state.filters.rigged = value;
    navigate({ page: 0 });
  },
};

function renderFacetsNow() {
  renderFacets(byId("facet-sections"), { state: S.state, facets: S.facets, vocab: S.vocab, prefs: S.prefs, openTree: S.openTree, showAll: S.showAll }, facetHandlers);
}

async function refreshFacets() {
  const filters = cleanFilters(S.state.filters);
  const key = JSON.stringify(filters);
  if (key === S.facetKey && S.facets) return renderFacetsNow();
  S.facetKey = key;
  S.facetAbort?.abort();
  const ac = (S.facetAbort = new AbortController());
  renderFacetsNow();
  try {
    S.facets = await api.facets(filters, ac.signal);
    renderFacetsNow();
  } catch (e) {
    if (e.name === "AbortError") return;
    S.facetKey = null;
    byId("facet-sections").replaceChildren(banner({ kind: "error", text: `The filter counts did not load: ${e.message}`, hint: e.hint, actions: [{ label: "Try again", run: refreshFacets }] }));
  }
}

function clearAll() {
  const n = filterCount(S.state.filters);
  if (!n) return;
  S.state.filters = emptyFilters();
  navigate({ page: 0, asset: null });
  toast({ kind: "info", text: `Cleared ${plural(n, "filter")}.`, actions: [{ label: "Undo", run: () => history.back() }] });
}

// ---------------------------------------------------------------- running a search, a similar query or a browse

function run() {
  const id = ++S.runId;
  S.abort?.abort();
  const ac = (S.abort = new AbortController());
  refreshFacets();
  const st = S.state;
  const job = st.sim ? runSimilar(id, ac.signal) : st.q ? runSearch(id, ac.signal) : runBrowse(id, ac.signal);
  job.catch((e) => {
    if (e.name === "AbortError" || id !== S.runId) return;
    endLoading();
    showFailure(e);
  });
}

function beginLoading(searching) {
  clearTimeout(S.loadTimer);
  S.loadTimer = setTimeout(() => {
    byId("state").replaceChildren();
    byId("understood").hidden = true;
    byId("pager").hidden = true;
    byId("summary").replaceChildren(el("h2", {}, searching ? "Searching" : "Loading"));
    const grid = byId("results");
    grid.replaceChildren(...skeletonCards(8));
    grid.hidden = false;
    byId("progress").hidden = !searching;
    if (searching) {
      renderProgress();
      S.ticker = setInterval(tick, 100);
    }
  }, SLOW_MS);
  byId("results-main").setAttribute("aria-busy", "true");
}

function endLoading() {
  clearTimeout(S.loadTimer);
  clearInterval(S.ticker);
  byId("progress").hidden = true;
  byId("results-main").removeAttribute("aria-busy");
}

const started = () => S.stepStart;
function tick() {
  byId("progress-time").textContent = fmtMs(performance.now() - started());
}

function searchSteps(st) {
  const llm = !S.status?.llm_problem;
  return [
    st.parse && llm && !st.understanding && { key: "parse", label: "Understanding the query" },
    { key: "retrieve", label: `Retrieving candidates with ${plural(st.use.length, "retriever")}` },
    st.rerank && llm && { key: "rerank", label: "Reranking the candidates" },
  ]
    .filter(Boolean)
    .map((s) => ({ ...s, state: "waiting", ms: null }));
}

function renderProgress() {
  const running = S.steps.find((s) => s.state === "running");
  byId("progress-title").textContent = running ? `Searching: ${running.label.toLowerCase()}` : "Searching";
  byId("progress-steps").replaceChildren(
    ...S.steps.map((s) =>
      el(
        "li",
        { class: "step", "data-state": s.state },
        icon(s.state === "done" ? "check" : s.state === "running" ? "spinner" : "clock", s.state === "running" ? "spin" : ""),
        s.label,
        el("span", { class: "state" }, s.state === "done" ? fmtMs(s.ms) : s.state === "running" ? "running" : s.state === "skipped" ? "skipped" : "waiting"),
      ),
    ),
  );
}

function onStage(stage) {
  const now = performance.now();
  for (const s of S.steps) {
    if (s.state === "running") Object.assign(s, { state: "done", ms: now - s.begin });
    if (s.key === stage) Object.assign(s, { state: "running", begin: now });
  }
  if (!byId("progress").hidden) renderProgress();
}

async function runSearch(id, signal) {
  const st = S.state;
  S.steps = searchSteps(st);
  S.stepStart = performance.now();
  beginLoading(true);
  const key = toQuery(st, { asset: false });
  const cached = !S.fresh && S.cache.get(key);
  S.fresh = false;
  const resp =
    cached ||
    (await api.search(
      { query: st.q, filters: cleanFilters(st.filters), k: RESULT_COUNT, use: st.use, parse: st.parse, rerank: st.rerank, understanding: st.understanding },
      onStage,
      signal,
    ));
  if (id !== S.runId) return;
  S.cache.delete(key);
  S.cache.set(key, resp);
  if (S.cache.size > CACHE_MAX) S.cache.delete(S.cache.keys().next().value);
  endLoading();
  S.timings = resp.timings;
  S.view = { mode: "search", resp, results: resp.results, offset: 0, cached: Boolean(cached) };
  renderView();
}

async function runSimilar(id, signal) {
  beginLoading(false);
  const out = await api.similar(S.state.sim, cleanFilters(S.state.filters), RESULT_COUNT, signal);
  if (id !== S.runId) return;
  endLoading();
  S.timings = out.timings;
  S.view = { mode: "similar", results: out.results, offset: 0, asset: out.asset_id };
  renderView();
}

async function runBrowse(id, signal) {
  beginLoading(false);
  const page = await api.browse(cleanFilters(S.state.filters), S.state.page * PAGE_SIZE, PAGE_SIZE, signal);
  if (id !== S.runId) return;
  endLoading();
  S.timings = null;
  S.view = { mode: "browse", page, results: page.results, offset: page.offset };
  renderView();
}

// ---------------------------------------------------------------- showing the results

function renderView() {
  const v = S.view;
  renderSummary(v);
  renderUnderstood(v);
  renderBanners(v);
  renderStagePanel();
  const grid = byId("results");
  byId("state").replaceChildren();
  byId("pager").hidden = true;
  if (!v.results.length) {
    grid.hidden = true;
    grid.replaceChildren();
    byId("state").append(emptyState(v));
    return;
  }
  const ctx = { href: (id) => hrefFor(S.state, { asset: id }), open: openAsset, selected: S.state.asset };
  grid.replaceChildren(...v.results.map((r, i) => renderCard(r, v.offset + i + 1, ctx)));
  grid.hidden = false;
  const first = grid.querySelector(".card");
  setActiveCard(grid, first);
  if (S.focusFirst) {
    S.focusFirst = false;
    first.focus({ preventScroll: true });
  }
  if (v.mode === "browse") renderPager(v.page);
  byId("results-main").scrollTo({ top: 0 });
}

function renderSummary(v) {
  const box = byId("summary");
  if (v.mode === "search") {
    const r = v.resp;
    box.replaceChildren(
      el("h2", {}, plural(r.results.length, "result")),
      el("span", { class: "meta" }, `for “${r.query}” in ${fmtMs(r.timings.total)}, from ${plural(r.eligible, "asset")} that pass the filters${v.cached ? ". Shown again from this session; the model was not asked again." : ""}`),
    );
  } else if (v.mode === "similar") {
    box.replaceChildren(el("h2", {}, plural(v.results.length, "similar asset")), el("span", { class: "meta" }, `to ${fileName(v.asset)}, by pictures and descriptions, in ${fmtMs(S.timings?.total)}`));
  } else {
    const p = v.page;
    const first = p.total ? p.offset + 1 : 0;
    const last = Math.min(p.offset + p.limit, p.total);
    box.replaceChildren(
      el("h2", {}, "Browse"),
      el("span", { class: "meta" }, `${first}–${last} of ${plural(p.total, "entry", "entries")} (${plural(p.assets, "asset")}; variants fold into one entry)`),
    );
  }
}

function chip(label, { kind = "", iconName, onRemove, title }) {
  return el(
    "span",
    { class: `chip ${kind}`, title },
    iconName && icon(iconName),
    label,
    onRemove && el("button", { type: "button", class: "chip-remove", title: `Remove: ${label}`, onclick: onRemove }, icon("close"), el("span", { class: "vh" }, `Remove ${label}`)),
  );
}

function understoodRow(label, iconName, chips, empty) {
  return el("div", { class: "understood-row" }, el("span", { class: "understood-label" }, icon(iconName), label), chips.length ? chips : el("span", { class: "meta" }, empty));
}

function renderUnderstood(v) {
  const box = byId("understood");
  const rows = [];
  if (S.state.sim) {
    rows.push(understoodRow("Similar to", "compare", [chip(fileName(S.state.sim), { kind: "chip-text", onRemove: () => navigate({ sim: null, page: 0 }), title: "Stop looking for similar assets" })], ""));
  }
  if (v.mode === "search") {
    const r = v.resp;
    const rewritten = r.text !== r.query;
    rows.push(understoodRow("Searched", "text", [chip(`“${r.text}”`, { kind: "chip-text", title: rewritten ? "The query as rewritten by query understanding" : "The query as typed" }), rewritten && el("span", { class: "meta" }, "rewritten from your words")], ""));
    const hard = constraintList(r.hard).map((c) => constraintChip(c, "hard"));
    rows.push(understoodRow("Required", "lock", hard, "None: no result is removed by a constraint"));
    const soft = constraintList(r.soft).map((c) => constraintChip(c, "soft"));
    rows.push(understoodRow("Suggested", "bulb", soft, "None: nothing is ranked up by a suggestion"));
    if (S.state.understanding) {
      rows.push(el("div", { class: "understood-row" }, el("span", { class: "understood-label" }, icon("wrench"), "Edited"), el("span", {}, "You changed the reading of the query; it was not asked of the model again."), el("button", { type: "button", class: "text-button", onclick: () => navigate({ understanding: null }) }, icon("refresh"), "Read the query again")));
    }
  }
  box.replaceChildren(...rows);
  box.hidden = rows.length === 0;
}

function constraintChip({ key, value }, kind) {
  const own = S.state.filters[key];
  const fromPanel = kind === "hard" && (Array.isArray(own) ? own.includes(value) : own != null);
  const title = kind === "soft" ? "Suggested by query understanding: ranks matching assets up, removes none" : fromPanel ? "Required by a filter you set" : "Required by query understanding";
  return chip(constraintLabel(key, value), { kind: kind === "soft" ? "chip-suggested" : "", title, onRemove: () => removeConstraint(kind, key, value, fromPanel) });
}

function removeConstraint(kind, key, value, fromPanel) {
  if (fromPanel) {
    const own = S.state.filters[key];
    if (Array.isArray(own)) own.splice(own.indexOf(value), 1);
    else S.state.filters[key] = null;
    navigate({ page: 0 });
    return;
  }
  const resp = S.view.resp;
  const u = S.state.understanding ?? { text: resp.text, hard: parsedOnly(resp.hard, S.state.filters), soft: parsedOnly(resp.soft, emptyFilters()) };
  const side = u[kind];
  if (Array.isArray(side[key])) {
    side[key] = side[key].filter((x) => x !== value);
    if (!side[key].length) delete side[key];
  } else delete side[key];
  navigate({ understanding: u });
}

function renderBanners(v) {
  const box = byId("banners");
  const items = [];
  const problem = S.status?.llm_problem;
  if (problem && !S.dismissedProblem) {
    items.push(banner({ kind: "warn", title: "Model stages are off", text: problem, onClose: () => { S.dismissedProblem = true; renderBanners(S.view); } }));
  }
  for (const note of v?.resp?.notes ?? []) {
    if (note === problem) continue;
    items.push(banner({ kind: "warn", title: "A stage was skipped", text: note, hint: "The results below come from the stages that did run. Search again to retry." }));
  }
  box.replaceChildren(...items);
}

function emptyState(v) {
  const anyFilters = filterCount(S.state.filters) > 0;
  const clear = { label: "Clear all filters", run: clearAll };
  if (v.mode === "browse" && v.page.total > 0) {
    return panel("search", "This page is past the end", `There are ${plural(v.page.total, "entry", "entries")}, ${PAGE_SIZE} to a page.`, [{ label: "Go to the first page", run: () => navigate({ page: 0 }) }]);
  }
  if (v.mode === "browse") {
    return anyFilters
      ? panel("filter", "No assets match these filters", "Every selected filter is a hard constraint. Remove one, or clear them all.", [clear])
      : panel("cube", "The catalog is empty", `No assets are indexed for ${S.status?.model}.`, [], "Build the index with: uv run catalog index --model " + S.status?.model);
  }
  if (v.mode === "similar") {
    return panel("compare", "Nothing similar under these filters", "No other asset passes the filters, or this asset has no embeddings yet.", anyFilters ? [clear] : [], `If it has none: uv run catalog embed --model ${S.status?.model}`);
  }
  const r = v.resp;
  if (r.eligible === 0) {
    return panel("filter", "No assets under these filters", "The required constraints above remove every asset, so nothing was searched. They come from your filters and from query understanding.", [anyFilters && clear, !S.state.understanding && S.state.parse && { label: "Search without query understanding", run: () => { setPref({ parse: false }); navigate({ parse: false }); } }]);
  }
  const reranked = r.timings.rerank != null;
  return panel(
    "search",
    "No good match",
    reranked
      ? `${plural(r.eligible, "asset")} passed the filters and the retrievers found candidates, but the reranker graded every one as not an answer. Nothing was shown rather than something wrong.`
      : `${plural(r.eligible, "asset")} passed the filters, but the retrievers found no candidate for this request.`,
    [reranked && { label: "Show the candidates without reranking", run: () => { setPref({ rerank: false }); navigate({ rerank: false }); } }],
    "Try other words, name the kind of thing you want, or loosen a required constraint above.",
  );
}

function panel(iconName, title, text, actions = [], hint = "") {
  return el(
    "section",
    { class: "state-panel" },
    icon(iconName, "icon-xl"),
    el("h2", {}, title),
    el("p", {}, text),
    hint && el("p", {}, hint),
    actions.filter(Boolean).length > 0 && el("div", { class: "actions" }, actions.filter(Boolean).map((a) => el("button", { type: "button", class: "btn", onclick: a.run }, a.label))),
  );
}

function showFailure(e) {
  byId("results").hidden = true;
  byId("pager").hidden = true;
  byId("understood").hidden = true;
  byId("summary").replaceChildren(el("h2", {}, "Something went wrong"));
  byId("state").replaceChildren(banner({ kind: "error", title: e.message, hint: e.hint, actions: [{ label: "Try again", run: () => { S.lastKey = null; S.fresh = true; sync(); } }] }));
}

function renderPager(page) {
  const pages = Math.max(1, Math.ceil(page.total / page.limit));
  if (pages <= 1) return;
  const current = S.state.page;
  const go = (n) => navigate({ page: n });
  byId("pager").replaceChildren(
    el("button", { type: "button", class: "btn", disabled: current === 0, onclick: () => go(current - 1) }, icon("chevron-left"), "Previous page"),
    el("span", {}, `Page ${current + 1} of ${pages}`),
    el("button", { type: "button", class: "btn", disabled: current + 1 >= pages, onclick: () => go(current + 1) }, "Next page", icon("chevron-right")),
  );
  byId("pager").hidden = false;
}

// ---------------------------------------------------------------- the detail drawer

const drawerHandlers = {
  close: () => closeDrawer(),
  similar: (id) => showSimilar(id),
  open: (id) => openAsset(id),
  href: (id) => hrefFor(S.state, { asset: id }),
  addFilter(field, term) {
    const list = S.state.filters[field];
    if (list.includes(term)) return toast({ kind: "info", text: "That filter is already on.", ms: 2500 });
    list.push(term);
    if (field === "categories") openAncestors();
    navigate({ page: 0 }, { push: true });
    toast({ kind: "ok", text: `Filter added: ${constraintLabel(field, term)}`, ms: 2500 });
  },
};

function openAsset(id, opener) {
  S.opener = opener ?? document.activeElement;
  navigate({ asset: id }, { push: !S.state.asset });
}

function closeDrawer() {
  if (history.state?.drawer) history.back();
  else navigate({ asset: null }, { push: false });
}

function showSimilar(id) {
  S.focusFirst = true;
  S.opener = null;
  navigate({ sim: id, asset: null, page: 0 });
}

function syncDrawer() {
  const id = S.state.asset;
  if (id === S.drawerAsset) return;
  const wasOpen = Boolean(S.drawerAsset);
  S.drawerAsset = id;
  S.drawerAbort?.abort();
  if (!id) {
    hideDrawer();
    if (wasOpen) restoreFocus();
    return;
  }
  loadDrawer(id);
}

async function loadDrawer(id) {
  const ac = (S.drawerAbort = new AbortController());
  showDrawerLoading(closeDrawer);
  byId("drawer-close").focus({ preventScroll: true });
  try {
    const detail = await api.asset(id, ac.signal);
    if (S.state.asset !== id) return;
    showDrawer(detail, drawerHandlers);
  } catch (e) {
    if (e.name === "AbortError") return;
    showDrawerError(e, closeDrawer, () => loadDrawer(id));
  }
  markCurrentCard();
}

function restoreFocus() {
  const card = S.opener?.isConnected ? S.opener : null;
  (card ?? byId("results").querySelector(`.card[data-id="${CSS.escape(S.state.asset ?? "")}"]`) ?? byId("results").querySelector(".card") ?? $q()).focus({ preventScroll: true });
}

function markCurrentCard() {
  for (const c of byId("results").querySelectorAll(".card")) {
    if (c.dataset.id === S.state.asset) c.setAttribute("aria-current", "true");
    else c.removeAttribute("aria-current");
  }
}

// ---------------------------------------------------------------- keyboard

function onEscape(e) {
  if (isLightboxOpen()) closeLightbox();
  else if (isDrawerOpen()) closeDrawer();
  else if (matchMedia(NARROW).matches && filtersOpen()) setFiltersOpen(false);
  else if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
  else return;
  e.preventDefault();
}

document.addEventListener("keydown", (e) => {
  if (isLightboxOpen() && e.key !== "Escape") {
    lightboxKey(e);
    return;
  }
  if (e.key === "Escape") return onEscape(e);
  if (e.key === "/" && !isTyping(e.target) && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault();
    $q().focus();
    $q().select();
  } else if (e.key === "ArrowDown" && e.target === $q()) {
    const first = byId("results").querySelector(".card[tabindex='0']") ?? byId("results").querySelector(".card");
    if (first) {
      e.preventDefault();
      first.focus();
    }
  }
});

// ---------------------------------------------------------------- wiring and boot

function wire() {
  byId("search-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const q = $q().value.trim();
    const same = q === S.state.q && !S.state.sim && !S.state.understanding && !S.state.asset;
    if (same) {
      S.lastKey = null;
      S.fresh = true;
    }
    S.opener = null;
    navigate({ q, sim: null, understanding: null, asset: null, page: 0 }, { push: !same });
  });
  $q().addEventListener("input", () => (byId("q-clear").hidden = $q().value === ""));
  byId("q-clear").addEventListener("click", () => {
    $q().value = "";
    byId("q-clear").hidden = true;
    $q().focus();
  });
  byId("filters-toggle").addEventListener("click", () => setFiltersOpen(!filtersOpen()));
  byId("clear-all").addEventListener("click", clearAll);
  byId("show-empty").addEventListener("change", (e) => {
    setPref({ showEmpty: e.target.checked });
    renderFacetsNow();
  });
  byId("theme-btn").addEventListener("click", () => {
    const order = ["auto", "light", "dark"];
    setPref({ theme: order[(order.indexOf(S.prefs.theme) + 1) % order.length] });
    applyPrefs();
  });
  for (const b of document.querySelectorAll("#density [data-density]")) {
    b.addEventListener("click", () => {
      setPref({ density: b.dataset.density });
      applyPrefs();
    });
  }
  attachGridKeys(byId("results"), { open: openAsset, similar: showSimilar });
}

async function boot() {
  S.state = stateFromUrl(location.search, S.prefs);
  openAncestors();
  applyPrefs();
  wire();
  const sections = el("div", { id: "facet-sections" });
  const sizes = el("div", { id: "facet-sizes" });
  byId("facets").replaceChildren(sections, sizes);
  buildSizeControls(sizes, sizeHandlers);
  if (!S.state.asset) $q().focus();
  try {
    [S.status, S.vocab] = await Promise.all([api.status(), api.vocab()]);
  } catch (e) {
    showFailure(e);
    return;
  }
  S.lastKey = null;
  sync();
}

boot();

// The left panel: search stages with their timings, and the filters with live counts.

import { el, fmtMs, icon } from "./dom.js";
import { FACETS, RETRIEVERS, SIZES } from "./model.js";

const COLLAPSED_TERMS = 8;
const SHOW_ALL_ABOVE = 10;

// ---------------------------------------------------------------- stages

/**
 * Checkboxes for the stages, each with the time it took in the last search.
 * h: {toggleRetriever(name), toggleParse(), toggleRerank()}
 */
export function renderStages(host, { state, status, timings, h }) {
  const problem = status?.llm_problem;
  const edited = Boolean(state.understanding);
  const rows = RETRIEVERS.map((r) =>
    stageRow({
      key: r.name,
      label: r.label,
      hint: r.hint,
      checked: state.use.includes(r.name),
      ms: timings?.[r.name],
      onchange: () => h.toggleRetriever(r.name),
    }),
  );
  rows.push(
    stageRow({
      key: "parse",
      label: edited ? "Query understanding (edited)" : "Query understanding",
      hint: edited
        ? "Using your edited reading of the query; reset it in the chips above the results"
        : `Splits the request into required constraints, suggested facets and search text (${status?.parse_model ?? "model"})`,
      checked: state.parse && !problem,
      disabled: Boolean(problem),
      ms: edited ? null : timings?.parse,
      msText: edited ? "edited" : null,
      onchange: h.toggleParse,
    }),
    stageRow({
      key: "rerank",
      label: "Rerank",
      hint: `A model reads each candidate and grades it 0 to 3 (${status?.rerank_model ?? "model"})`,
      checked: state.rerank && !problem,
      disabled: Boolean(problem),
      ms: timings?.rerank,
      onchange: h.toggleRerank,
    }),
    el(
      "div",
      { class: "stage-total" },
      el("span", {}, "Total"),
      el("span", { class: "stage-ms" }, fmtMs(timings?.total)),
    ),
  );
  if (problem) rows.push(el("p", { class: "stage-note" }, "Model stages are unavailable: see the notice above the results."));
  host.replaceChildren(...rows);
}

function stageRow({ key, label, hint, checked, disabled, ms, msText, onchange }) {
  const input = el("input", { type: "checkbox", id: `stage-${key}`, disabled });
  input.checked = checked;
  input.addEventListener("change", onchange);
  return el(
    "div",
    { class: "stage-row" },
    el(
      "label",
      { class: disabled ? "check is-disabled" : "check", title: hint },
      input,
      el("span", { class: "grow" }, label),
    ),
    el("span", { class: "stage-ms", title: "Time in the last search" }, msText ?? fmtMs(ms)),
  );
}

// ---------------------------------------------------------------- facets

/**
 * ctx: {state, facets: {total, counts}, vocab, prefs, openTree: Set, showAll: Set}
 * h: {toggleTerm(field, term), toggleSection(facet), toggleNode(path), toggleShowAll(facet)}
 */
export function renderFacets(host, ctx, h) {
  const active = document.activeElement;
  const focusKey = host.contains(active) ? active.dataset.key : null;
  host.replaceChildren(...FACETS.map((f) => section(f, ctx, h)));
  if (focusKey) host.querySelector(`[data-key="${CSS.escape(focusKey)}"]`)?.focus({ preventScroll: true });
}

function section(f, ctx, h) {
  const selected = ctx.state.filters[f.field];
  const open = Boolean(ctx.prefs.facetsOpen[f.facet]);
  const head = el(
    "button",
    { type: "button", class: "facet-head", "aria-expanded": String(open), dataset: { key: `head:${f.facet}` }, onclick: () => h.toggleSection(f.facet) },
    icon("chevron-right", "chev"),
    icon(f.icon),
    el("span", { class: "grow" }, f.label),
    selected.length > 0 && el("span", { class: "badge-count" }, selected.length),
  );
  const body = el("div", { class: "facet-body", hidden: !open }, open && (f.tree ? tree(f, ctx, h) : list(f, ctx, h)));
  return el("section", { class: "facet" }, head, body);
}

function termsOf(f, ctx) {
  const counts = ctx.facets?.counts?.[f.facet] ?? {};
  const fromVocab = Object.keys(ctx.vocab?.[f.facet] ?? {});
  const extra = Object.keys(counts).filter((t) => !fromVocab.includes(t)).sort();
  const selected = ctx.state.filters[f.field].filter((t) => !fromVocab.includes(t) && !extra.includes(t));
  return { counts, terms: [...fromVocab, ...extra, ...selected] };
}

function list(f, ctx, h) {
  if (!ctx.facets) return skeletonRows();
  const { counts, terms } = termsOf(f, ctx);
  const selected = new Set(ctx.state.filters[f.field]);
  const shown = terms.filter((t) => selected.has(t) || ctx.prefs.showEmpty || (counts[t] ?? 0) > 0);
  if (!shown.length) return el("p", { class: "facet-none" }, "No assets have a term here under the other filters.");
  const all = ctx.showAll.has(f.facet) || shown.length <= SHOW_ALL_ABOVE;
  const visible = all ? shown : shown.filter((t, i) => i < COLLAPSED_TERMS || selected.has(t));
  const rows = visible.map((t) => termRow(f, t, counts[t] ?? 0, selected.has(t), ctx.vocab?.[f.facet]?.[t], h));
  const hidden = shown.length - visible.length;
  if (shown.length > SHOW_ALL_ABOVE) {
    rows.push(
      el(
        "button",
        { type: "button", class: "text-button more", dataset: { key: `more:${f.facet}` }, onclick: () => h.toggleShowAll(f.facet) },
        all ? "Show fewer" : `Show ${hidden} more`,
      ),
    );
  }
  return rows;
}

function termRow(f, term, count, selected, definition, h, label = term) {
  const input = el("input", { type: "checkbox", dataset: { key: `${f.facet}:${term}` } });
  input.checked = selected;
  input.addEventListener("change", () => h.toggleTerm(f.field, term));
  return el(
    "label",
    { class: count === 0 && !selected ? "check is-empty" : "check", title: definition ?? term },
    input,
    el("span", { class: "grow" }, label),
    el("span", { class: "count", title: "Assets with this term under the other filters" }, count),
  );
}

function skeletonRows() {
  return [0, 1, 2].map(() => el("div", { class: "skeleton skeleton-line" }));
}

// ---------------------------------------------------------------- category tree

function buildTree(paths) {
  const root = { children: new Map(), path: "" };
  for (const path of paths) {
    let node = root;
    let prefix = "";
    for (const part of path.split("/")) {
      prefix = prefix ? `${prefix}/${part}` : part;
      if (!node.children.has(part)) node.children.set(part, { name: part, path: prefix, children: new Map() });
      node = node.children.get(part);
    }
  }
  return root;
}

function tree(f, ctx, h) {
  if (!ctx.facets) return skeletonRows();
  const counts = ctx.facets.counts.category ?? {};
  const selected = new Set(ctx.state.filters.categories);
  const paths = new Set([...Object.keys(ctx.vocab?.category ?? {}), ...Object.keys(counts), ...selected]);
  const root = buildTree([...paths].sort());
  const visible = (node) =>
    (counts[node.path] ?? 0) > 0 || selected.has(node.path) || ctx.prefs.showEmpty || [...node.children.values()].some(visible);
  const draw = (node) => {
    const kids = [...node.children.values()].filter(visible);
    const open = ctx.openTree.has(node.path);
    const row = el(
      "div",
      { class: "tree-row" },
      kids.length
        ? el(
            "button",
            { type: "button", class: "twisty", "aria-expanded": String(open), title: open ? "Collapse" : "Expand", dataset: { key: `twisty:${node.path}` }, onclick: () => h.toggleNode(node.path) },
            icon("chevron-right"),
            el("span", { class: "vh" }, open ? `Collapse ${node.name}` : `Expand ${node.name}`),
          )
        : el("span", { class: "twisty-spacer" }),
      termRow(f, node.path, counts[node.path] ?? 0, selected.has(node.path), ctx.vocab?.category?.[node.path] ?? node.path, h, node.name),
    );
    const item = el("li", {}, row);
    if (kids.length && open) item.append(el("ul", {}, kids.map(draw)));
    return item;
  };
  const tops = [...root.children.values()].filter(visible);
  if (!tops.length) return el("p", { class: "facet-none" }, "No assets have a category under the other filters.");
  return el("ul", { class: "tree" }, tops.map(draw));
}

// ---------------------------------------------------------------- sizes and rigging (built once)

/** Number fields for the size limits and the rigged select; h.setSize(key, number|null), h.setRigged(null|bool). */
export function buildSizeControls(host, h) {
  const grid = el("fieldset", { class: "size-grid" }, el("legend", {}, "Size in metres"));
  grid.append(el("span", {}), el("span", { class: "size-head" }, "at least"), el("span", { class: "size-head" }, "at most"));
  for (const z of SIZES) {
    grid.append(el("label", { for: `size-${z.min}` }, z.label));
    for (const key of [z.min, z.max]) {
      const input = el("input", { type: "number", id: `size-${key}`, min: "0", step: "0.1", inputmode: "decimal", placeholder: "any", "aria-label": `${z.label}, ${key === z.min ? "at least" : "at most"}, metres` });
      input.addEventListener("change", () => h.setSize(key, input.value === "" ? null : Math.max(0, Number(input.value))));
      grid.append(input);
    }
  }
  const select = el(
    "select",
    { id: "rigged", class: "select" },
    el("option", { value: "" }, "Any"),
    el("option", { value: "1" }, "Rigged"),
    el("option", { value: "0" }, "Not rigged"),
  );
  select.addEventListener("change", () => h.setRigged(select.value === "" ? null : select.value === "1"));
  host.replaceChildren(
    el("section", { class: "facet" }, grid),
    el("section", { class: "facet" }, el("label", { class: "size-grid", for: "rigged" }, el("span", {}, "Rigged"), select)),
  );
}

export function syncSizeControls(filters) {
  for (const z of SIZES) {
    for (const key of [z.min, z.max]) {
      const input = document.getElementById(`size-${key}`);
      if (input && document.activeElement !== input) input.value = filters[key] ?? "";
    }
  }
  const select = document.getElementById("rigged");
  if (select) select.value = filters.rigged == null ? "" : filters.rigged ? "1" : "0";
}

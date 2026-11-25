// The detail drawer and the full-size picture viewer.

import { imageUrl } from "./api.js";
import { byId, el, fileName, fmtM, fmtSize, icon } from "./dom.js";
import { picture } from "./grid.js";
import { FACET_BY_FIELD, FACETS, VIEWS } from "./model.js";
import { banner, copyText } from "./notify.js";

const drawer = () => byId("drawer");
const lightbox = () => byId("lightbox");

export const isDrawerOpen = () => !drawer().hidden;
export const isLightboxOpen = () => !lightbox().hidden;

// ---------------------------------------------------------------- the drawer

function header(title, subtitle, onClose) {
  return el(
    "div",
    { class: "drawer-head" },
    el("div", { class: "titles" }, el("h2", { id: "drawer-title", tabindex: "-1" }, title), el("div", { class: "sub" }, subtitle)),
    el("button", { type: "button", class: "btn", id: "drawer-close", title: "Close the details (Esc)", onclick: onClose }, icon("close"), "Close"),
  );
}

export function showDrawerLoading(onClose) {
  const d = drawer();
  d.hidden = false;
  d.replaceChildren(
    header("Loading asset", "Reading the record", onClose),
    el("div", { class: "drawer-body" }, el("div", { class: "skeleton skeleton-pic" }), [0, 1, 2, 3, 4].map(() => el("div", { class: "skeleton skeleton-line" }))),
  );
}

export function showDrawerError(error, onClose, onRetry) {
  const d = drawer();
  d.hidden = false;
  d.replaceChildren(
    header("Could not load the asset", "The details did not arrive", onClose),
    el("div", { class: "drawer-body" }, banner({ kind: "error", text: error.message, hint: error.hint, actions: [{ label: "Try again", run: onRetry }] })),
  );
}

export function hideDrawer() {
  drawer().hidden = true;
  drawer().replaceChildren();
}

const none = () => el("span", { class: "none" }, "None");

function chips(values, field, h) {
  if (!values.length) return none();
  return values.map((v) => {
    const label = field === "categories" ? v.replaceAll("/", " / ") : v;
    return el("button", { type: "button", class: "chip", title: `Add the filter ${FACET_BY_FIELD[field].label} = ${v}`, onclick: () => h.addFilter(field, v) }, label);
  });
}

function plain(values) {
  if (Array.isArray(values)) return values.length ? values.join(", ") : none();
  return values === "" || values == null ? none() : String(values);
}

function copyButton(text, what) {
  return el("button", { type: "button", class: "btn btn-small copy", title: `Copy the ${what}`, onclick: () => copyText(text, what) }, icon("copy"), "Copy");
}

function row(label, iconName, ...value) {
  return el("div", { class: "row" }, el("dt", {}, icon(iconName), label), el("dd", {}, ...value));
}

function group(title, iconName, ...rows) {
  return el("section", {}, el("h3", { class: "group-title" }, icon(iconName), title), el("dl", { class: "rows" }, ...rows));
}

function gallery(rec) {
  const main = el("img", { src: imageUrl(rec.id, "contact_sheet"), alt: "Contact sheet: all four views of the asset" });
  let current = "contact_sheet";
  const button = el("button", { type: "button", class: "gallery-main", title: "Show full size" }, main);
  button.addEventListener("click", () => openLightbox(rec, current));
  const tabs = VIEWS.map((v) => {
    const tab = el(
      "button",
      { type: "button", class: "btn", "aria-pressed": String(v.id === current), title: `Show the ${v.label.toLowerCase()}` },
      el("img", { src: imageUrl(rec.id, v.id), alt: "", loading: "lazy" }),
      v.label,
    );
    tab.addEventListener("click", () => {
      current = v.id;
      main.src = imageUrl(rec.id, v.id);
      main.alt = `${v.label} of ${rec.plain_name}`;
      for (const t of tabs) t.setAttribute("aria-pressed", String(t === tab));
    });
    return tab;
  });
  return el("section", { class: "gallery", "aria-label": "Pictures" }, button, el("div", { class: "gallery-tabs" }, tabs));
}

/** h: {close, similar(id), open(id), addFilter(field, term)}. */
export function showDrawer({ record: r, siblings }, h) {
  const d = drawer();
  d.hidden = false;
  const [w, hh, dd] = r.size_m;
  const facetRows = FACETS.map((f) => {
    const values = f.facet === "kind" ? [r.kind] : f.facet === "category" ? [r.category, ...r.secondary_categories] : f.facet === "size_class" ? [r.size_class] : f.facet === "pack" ? [`${r.source}/${r.pack}`] : r[f.field];
    return row(f.label, f.icon, chips(values, f.field, h));
  });
  d.replaceChildren(
    header(r.plain_name, r.name, h.close),
    el(
      "div",
      { class: "drawer-body" },
      el(
        "div",
        { class: "drawer-actions" },
        el("button", { type: "button", class: "btn btn-primary", id: "similar-btn", onclick: () => h.similar(r.id) }, icon("compare"), "Similar to this"),
        el("button", { type: "button", class: "btn", onclick: () => copyText(r.id, "asset id") }, icon("copy"), "Copy asset id"),
        el("button", { type: "button", class: "btn", onclick: () => copyText(r.path, "file path") }, icon("copy"), "Copy file path"),
      ),
      gallery(r),
      group(
        "Identity",
        "hash",
        row("Asset id", "hash", el("span", { class: "mono" }, r.id), copyButton(r.id, "asset id")),
        row("File name", "file", r.name),
        row("File path", "folder", el("span", { class: "mono" }, r.path), copyButton(r.path, "file path")),
        row("Pack", "cube", `${r.source} / ${r.pack}`),
        row("Variant group", "layers", plain(r.variant_group)),
        row("Described by", "text", r.described_by),
      ),
      group(
        "What it is",
        "info",
        row("Plain name", "text", r.plain_name),
        row("Description", "text", plain(r.description)),
        row("Usage", "bolt", plain(r.usage)),
        row("Also called", "tag", plain(r.aliases)),
        row("Distinguishing", "sparkle", plain(r.distinguishing)),
        row("Snaps with", "layers", plain(r.snaps_with)),
        row("Likely queries", "search", plain(r.likely_queries.map((q) => `“${q}”`))),
        row("Doubts", "warning", plain(r.doubts)),
        row("Real front", "crosshair", plain(r.real_front)),
      ),
      group("Facets", "filter", ...facetRows),
      group(
        "Measured",
        "ruler",
        row("Size", "ruler", `${fmtSize(r.size_m)} (width × height × depth)`),
        row("Longest side", "ruler", `${fmtM(r.longest_m)} m`),
        row("Form", "cube", r.form),
        row("Flat faces", "grid", plain(r.flat_faces)),
        row("Pivot", "crosshair", r.pivot),
        row("Triangles", "mesh", r.tris.toLocaleString("en")),
        row("Rigged", "bone", r.has_rig ? "Yes" : "No"),
        row("Animations", "bone", plain(r.animations)),
      ),
      group(
        "Files",
        "file",
        row("Contact sheet", "file", el("span", { class: "mono" }, r.contact_sheet), copyButton(r.contact_sheet, "contact sheet path")),
        ...VIEWS.slice(1).map((v) => (r.views[v.id] ? row(v.label, "file", el("span", { class: "mono" }, r.views[v.id]), copyButton(r.views[v.id], `${v.label.toLowerCase()} path`)) : row(v.label, "file", none()))),
      ),
      el(
        "section",
        {},
        el("h3", { class: "group-title" }, icon("layers"), `Variants (${siblings.length})`),
        siblings.length
          ? el("div", { class: "variant-grid" }, siblings.map((s) => el("a", { href: h.href(s.id), title: `Open the details of ${s.name}`, onclick: (e) => { e.preventDefault(); h.open(s.id); } }, el("img", { src: imageUrl(s.id, "three_quarter"), alt: "", loading: "lazy" }), fileName(s.id))))
          : el("p", { class: "none" }, "This asset has no variants."),
      ),
    ),
  );
  byId("drawer-title").focus({ preventScroll: true });
  d.scrollTop = 0;
}

// ---------------------------------------------------------------- full-size viewer

let lightboxIndex = 0;
let lightboxRecord = null;
let lightboxReturn = null;

export function openLightbox(rec, view) {
  lightboxRecord = rec;
  lightboxIndex = Math.max(0, VIEWS.findIndex((v) => v.id === view));
  lightboxReturn = document.activeElement;
  drawLightbox();
  lightbox().hidden = false;
  byId("lightbox-close").focus();
}

export function closeLightbox() {
  lightbox().hidden = true;
  lightbox().replaceChildren();
  lightboxReturn?.focus?.({ preventScroll: true });
}

function stepLightbox(by) {
  lightboxIndex = (lightboxIndex + by + VIEWS.length) % VIEWS.length;
  drawLightbox();
  byId("lightbox-close").focus();
}

function drawLightbox() {
  const v = VIEWS[lightboxIndex];
  lightbox().replaceChildren(
    el(
      "div",
      { class: "lightbox-bar" },
      el("div", { class: "title" }, `${lightboxRecord.plain_name}: ${v.label} (${lightboxIndex + 1} of ${VIEWS.length})`),
      el("button", { type: "button", class: "btn", title: "Previous picture (Left arrow)", onclick: () => stepLightbox(-1) }, icon("chevron-left"), "Previous"),
      el("button", { type: "button", class: "btn", title: "Next picture (Right arrow)", onclick: () => stepLightbox(1) }, "Next", icon("chevron-right")),
      el("button", { type: "button", class: "btn", id: "lightbox-close", title: "Close the picture (Esc)", onclick: closeLightbox }, icon("close"), "Close"),
    ),
    el("div", { class: "lightbox-stage" }, picture(lightboxRecord.id, v.id, `${v.label} of ${lightboxRecord.plain_name}`)),
  );
  const stage = lightbox().querySelector(".lightbox-stage");
  stage.addEventListener("click", (e) => e.target === stage && closeLightbox());
}

/** Arrow keys and Tab inside the viewer; returns true when the key was used. */
export function lightboxKey(e) {
  if (e.key === "ArrowLeft") stepLightbox(-1);
  else if (e.key === "ArrowRight") stepLightbox(1);
  else if (e.key === "Tab") {
    const items = [...lightbox().querySelectorAll("button")];
    const first = items[0];
    const last = items[items.length - 1];
    if (e.shiftKey && document.activeElement === first) last.focus();
    else if (!e.shiftKey && document.activeElement === last) first.focus();
    else return false;
  } else return false;
  e.preventDefault();
  return true;
}

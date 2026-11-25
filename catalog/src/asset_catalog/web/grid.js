// The results grid: one card per result, the same lines in the same order, keyboard-navigable.

import { imageUrl } from "./api.js";
import { el, fileName, fmtSize, icon, plural, smoothBehavior } from "./dom.js";
import { GRADES } from "./model.js";

/** Three dots, as many filled as the grade: a shape, not only a colour. */
function pips(n) {
  return el("span", { class: "pips", "aria-hidden": "true" }, [1, 2, 3].map((i) => el("span", { class: i <= n ? "pip on" : "pip" })));
}

function gradeBadge(grade) {
  if (grade == null) {
    return el(
      "span",
      { class: "pic-badge grade", "data-grade": "none", title: "The rerank stage did not grade this result" },
      pips(0),
      "No grade",
    );
  }
  return el(
    "span",
    { class: "pic-badge grade", "data-grade": String(grade), title: `Reranker grade ${grade} of 3: ${GRADES[grade]}` },
    pips(grade),
    `${grade}/3 ${GRADES[grade]}`,
  );
}

export function picture(id, view, alt) {
  const img = el("img", { src: imageUrl(id, view), alt, loading: "lazy", decoding: "async", width: "768", height: "768" });
  img.addEventListener("error", () => img.replaceWith(el("div", { class: "pic-missing" }, icon("image-off", "icon-lg"), "No picture")), { once: true });
  return img;
}

function fact(name, iconName, value, title) {
  return el(
    "li",
    { class: "fact", title },
    icon(iconName),
    el("span", { class: "vh" }, `${name}: `),
    el("span", {}, value),
  );
}

function disclosure(label, iconName, count, emptyText) {
  const on = count > 0;
  return el(
    "button",
    { type: "button", class: "disclosure", "aria-expanded": "false", disabled: !on },
    icon(iconName),
    on ? label : emptyText,
    on && icon("chevron-right", "chev"),
  );
}

function openLink(ctx, id, className, ...kids) {
  const link = el("a", { href: ctx.href(id), class: className, title: "Open the details of this asset" }, ...kids);
  link.addEventListener("click", (e) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    ctx.open(id, link.closest(".card"));
  });
  return link;
}

/** ctx: {href(id), open(id, cardEl), selected: id|null}. */
export function renderCard(res, rank, ctx) {
  const r = res.record;
  const card = el("article", { class: "card", tabindex: "-1", dataset: { id: r.id }, "aria-label": `Result ${rank}: ${r.plain_name}` });
  if (ctx.selected === r.id) card.setAttribute("aria-current", "true");

  const pic = el(
    "div",
    { class: "pic" },
    openLink(ctx, r.id, "pic-link", picture(r.id, "three_quarter", `${r.plain_name}, three-quarter view`)),
    gradeBadge(res.grade),
    el("span", { class: "pic-badge pic-rank", title: `Rank ${rank}` }, `#${rank}`),
  );
  pic.firstElementChild.setAttribute("tabindex", "-1");
  pic.firstElementChild.setAttribute("aria-hidden", "true");

  const variants = disclosure(`+${plural(res.variants.length, "variant")}`, "layers", res.variants.length, "No variants");
  const why = disclosure("Why it matched", "info", res.why.length, "No match notes");
  const variantsPanel = el("ul", { class: "sub-list", hidden: true }, res.variants.map((id) => el("li", {}, openLink(ctx, id, "variant-link", el("img", { src: imageUrl(id, "three_quarter"), alt: "", loading: "lazy" }), fileName(id)))));
  const whyPanel = el("ul", { class: "sub-list why-list", hidden: true }, res.why.map((w) => el("li", {}, w)));
  wireDisclosure(variants, variantsPanel);
  wireDisclosure(why, whyPanel);

  card.append(
    pic,
    el(
      "div",
      { class: "card-body" },
      el("h3", { class: "card-title" }, openLink(ctx, r.id, "title-link", r.plain_name)),
      el(
        "ul",
        { class: "facts" },
        fact("File name", "file", r.name, "File name"),
        fact("Category", "folder", r.category.replaceAll("/", " / "), "Category"),
        fact("Size", "ruler", fmtSize(r.size_m), "Width × height × depth"),
        fact("Pack", "cube", `${r.source} / ${r.pack}`, "Pack"),
      ),
      variants,
      variantsPanel,
      why,
      whyPanel,
    ),
  );
  return el("li", {}, card);
}

function wireDisclosure(button, panel) {
  button.addEventListener("click", () => toggleDisclosure(button, panel));
  button.panel = panel;
}

function toggleDisclosure(button, panel) {
  const open = button.getAttribute("aria-expanded") !== "true";
  button.setAttribute("aria-expanded", String(open));
  panel.hidden = !open;
}

export function skeletonCards(n) {
  return Array.from({ length: n }, () =>
    el("li", {}, el("div", { class: "card", "aria-hidden": "true" }, el("div", { class: "skeleton skeleton-pic" }), el("div", { class: "card-body" }, [0, 1, 2, 3].map(() => el("div", { class: "skeleton skeleton-line" }))))),
  );
}

// ---------------------------------------------------------------- keyboard

const cards = (grid) => [...grid.querySelectorAll(".card")];

/** Only the active card and its buttons are tab stops; arrows move between cards. */
export function setActiveCard(grid, card) {
  for (const c of cards(grid)) {
    const active = c === card;
    c.tabIndex = active ? 0 : -1;
    for (const b of c.querySelectorAll("button, .title-link")) b.tabIndex = active ? 0 : -1;
  }
}

function columns(list) {
  if (!list.length) return 1;
  const top = list[0].offsetTop;
  const n = list.findIndex((c) => c.offsetTop > top);
  return n < 0 ? list.length : n;
}

function focusCard(grid, card) {
  setActiveCard(grid, card);
  card.focus({ preventScroll: true });
  card.scrollIntoView({ block: "nearest", behavior: smoothBehavior() });
}

/** h: {open(id, card), similar(id)}. */
export function attachGridKeys(grid, h) {
  grid.addEventListener("focusin", (e) => {
    const card = e.target.closest(".card");
    if (card && card.tabIndex !== 0) setActiveCard(grid, card);
  });
  grid.addEventListener("keydown", (e) => {
    const card = e.target.closest(".card");
    if (!card || e.target !== card || e.altKey || e.ctrlKey || e.metaKey) return;
    const list = cards(grid);
    const i = list.indexOf(card);
    const cols = columns(list);
    const lastRowStart = list.length - (list.length % cols || cols);
    const moves = {
      ArrowRight: Math.min(i + 1, list.length - 1),
      ArrowLeft: Math.max(i - 1, 0),
      ArrowDown: i + cols < list.length ? i + cols : i < lastRowStart ? list.length - 1 : i,
      ArrowUp: Math.max(i - cols, 0),
      PageDown: Math.min(i + cols * 3, list.length - 1),
      PageUp: Math.max(i - cols * 3, 0),
      Home: 0,
      End: list.length - 1,
    };
    if (e.key in moves) {
      e.preventDefault();
      focusCard(grid, list[moves[e.key]]);
    } else if (e.key === "Enter") {
      e.preventDefault();
      h.open(card.dataset.id, card);
    } else if (e.key.toLowerCase() === "s") {
      h.similar(card.dataset.id);
    } else if (e.key.toLowerCase() === "v" || e.key.toLowerCase() === "w") {
      const which = e.key.toLowerCase() === "v" ? 0 : 1;
      const button = card.querySelectorAll(".disclosure")[which];
      if (button && !button.disabled) toggleDisclosure(button, button.panel);
    }
  });
}

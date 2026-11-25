// Small DOM helpers shared by every module. No framework.

const SVG_NS = "http://www.w3.org/2000/svg";

/** el("div", {class: "x", onclick: fn, dataset: {id: 1}}, child, "text", [more]) */
export function el(tag, attrs = {}, ...kids) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value == null || value === false) continue;
    if (key === "dataset") Object.assign(node.dataset, value);
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? "" : String(value));
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    node.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return node;
}

/** The one icon set: symbols defined in index.html, drawn flat in the current text colour. */
export function icon(name, extra = "") {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("class", extra ? `icon ${extra}` : "icon");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  const use = document.createElementNS(SVG_NS, "use");
  use.setAttribute("href", `#i-${name}`);
  svg.append(use);
  return svg;
}

export const byId = (id) => document.getElementById(id);

export function isTyping(target) {
  return target instanceof HTMLElement && target.matches("input, textarea, select, [contenteditable]");
}

export function fmtMs(ms) {
  if (ms == null) return "—";
  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;
}

export const fmtM = (v) => v.toFixed(2);

export const fmtSize = ([w, h, d]) => `${fmtM(w)} × ${fmtM(h)} × ${fmtM(d)} m`;

export const plural = (n, one, many = `${one}s`) => `${n.toLocaleString("en")} ${n === 1 ? one : many}`;

export const fileName = (id) => id.split("/").pop();

export const smoothBehavior = () =>
  matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";

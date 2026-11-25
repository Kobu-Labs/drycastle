// One banner pattern: toasts and inline notes are the same component.

import { byId, el, icon } from "./dom.js";

const ICONS = { info: "info", warn: "warning", error: "error", ok: "check" };
const MAX_TOASTS = 3;
const TOAST_MS = 5000;

/** kind: info | warn | error | ok. actions: [{label, run}]. */
export function banner({ kind = "info", title = "", text = "", hint = "", actions = [], onClose = null }) {
  return el(
    "div",
    { class: `banner banner-${kind}`, role: kind === "error" ? "alert" : "status" },
    icon(ICONS[kind], "icon-lg"),
    el(
      "div",
      { class: "banner-body" },
      title && el("div", { class: "banner-title" }, title),
      text && el("div", {}, text),
      hint && el("div", { class: "banner-hint" }, hint),
      actions.length > 0 &&
        el(
          "div",
          { class: "banner-actions" },
          actions.map((a) => el("button", { type: "button", class: "btn btn-small", onclick: a.run }, a.label)),
        ),
    ),
    onClose &&
      el(
        "button",
        { type: "button", class: "btn btn-quiet btn-small", title: "Dismiss", onclick: onClose },
        icon("close"),
        el("span", { class: "vh" }, "Dismiss"),
      ),
  );
}

/** A transient banner at the bottom. Errors stay until dismissed. */
export function toast(options) {
  const host = byId("toasts");
  const node = banner({ ...options, onClose: () => node.remove() });
  host.append(node);
  while (host.children.length > MAX_TOASTS) host.firstElementChild.remove();
  if (options.kind !== "error") setTimeout(() => node.remove(), options.ms ?? TOAST_MS);
  return node;
}

/** Copy text and say so. Falls back to a hidden field where the clipboard API is blocked. */
export async function copyText(text, what) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const field = el("textarea", { class: "vh" }, text);
    document.body.append(field);
    field.select();
    const ok = document.execCommand("copy");
    field.remove();
    if (!ok) {
      toast({ kind: "error", text: `Could not copy the ${what}.`, hint: "Select the text and copy it by hand." });
      return;
    }
  }
  toast({ kind: "ok", text: `Copied the ${what}.`, ms: 2500 });
}

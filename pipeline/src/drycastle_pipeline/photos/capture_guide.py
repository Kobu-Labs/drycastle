"""A guide drawn over the Street View tab while capturing for ``photo-to-building``: which walls of the building in
the middle of the view a capture from here can measure, and, after the capture key, the wall's ends marked by hand.

    uv run python -m drycastle_pipeline.photos.streetview_follow --launch --capture

Look: the building's walls are outlined on the panorama as the map has them (a metre or two off, a capture's own place
being that rough). A solid outline with a tick is a wall this view measures well (both ends in view, at most
``GOOD_OFF_DEG`` off square); dashed with a triangle is usable but steep; dotted with a cross is not usable, with the
reason in the panel. Capture (C): the view is held, and for each corner in view a blue line follows the mouse (or the
arrow keys): put it where the wall itself ends and click. The outline is then refitted to the marks and shown: Enter
saves the picture, its camera and the marks (``ends`` in the sidecar: corner number to picture column), which
``facade_pipeline`` uses instead of asking a model where the walls end.

The overlay is plain DOM put into the tab through Chrome's debugging port (no ``innerHTML``: the page forbids it); all
geometry is done here and sent as pixels. UI rules: ``docs/standards/ui-ux.md``.
"""

import base64
import json
import math
import sys
from pathlib import Path

import numpy as np

from drycastle_pipeline.photos import facade_rectify as fr
from drycastle_pipeline.photos.landmark_photo import project
from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR, Cdp, guess_building, parse_view, save_capture, tabs

GOOD_OFF_DEG = 35.0
MAX_OFF_DEG = 78.0
MIN_WALL_M = 2.0
WALL_TOP_M = 3.0  # how high the outline is drawn: about a storey
MAX_MOVE_M = 6.0
OVERLAY_JS = r"""(() => { const V = 3;  // raise with every change here: a tab holding an older guide reloads itself to get this one
  if (window.__dc && window.__dc.v === V) return 1;
  if (window.__dc) { location.reload(); return 0; }
  // tokens: every colour, size and spacing of the guide (docs/standards/ui-ux.md section 4)
  const T = { bg: 'rgba(16,20,26,.94)', ink: '#f4f6f8', dim: '#c3ccd6', good: '#5fe39a', poor: '#ffd166', bad: '#ff8a7a', accent: '#6cc5ff',
    line: 'rgba(255,255,255,.22)', radius: '8px', pad: '12px', gap: '8px', body: '16px', small: '14px', title: '18px', ease: '140ms' };
  const NS = 'http://www.w3.org/2000/svg';
  const GLYPH = { good: '✓', poor: '△', no: '✕' }, WORD = { good: 'good', poor: 'usable', no: 'not usable' };
  const el = (tag, css, text) => { const e = document.createElement(tag); if (css) e.style.cssText = css; if (text != null) e.textContent = text; return e; };
  const svgEl = (tag, attrs) => { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); return e; };
  const font = 'font-family:system-ui,sans-serif;';
  const root = el('div', 'position:fixed;inset:0;z-index:2147483646;pointer-events:none;' + font);
  const shield = el('div', 'position:absolute;inset:0;pointer-events:none;cursor:crosshair');
  const svg = svgEl('svg', { width: '100%', height: '100%' }); svg.style.cssText = 'position:absolute;inset:0;pointer-events:none';
  const box = 'background:' + T.bg + ';color:' + T.ink + ';border:1px solid ' + T.line + ';border-radius:' + T.radius + ';padding:' + T.pad + ';box-shadow:0 2px 12px rgba(0,0,0,.45);';
  const panel = el('div', 'position:absolute;right:16px;top:88px;width:336px;' + box + 'font-size:' + T.body + ';line-height:1.4');
  const prompt = el('div', 'position:absolute;left:50%;top:88px;transform:translateX(-50%);max-width:56%;text-align:center;' + box + 'font-size:' + T.title + ';font-weight:600;display:none');
  const hint = el('div', 'position:absolute;left:50%;bottom:40px;transform:translateX(-50%);white-space:nowrap;' + box + 'font-size:' + T.body);
  const toast = el('div', 'position:absolute;left:50%;top:24px;transform:translateX(-50%);' + box + 'font-size:' + T.body + ';font-weight:600;opacity:0;transition:opacity ' + T.ease);
  root.append(shield, svg, panel, prompt, hint, toast); document.body.appendChild(root);
  const dc = window.__dc = { v: V, mode: 'look', guide: null, frozen: null, fit: null, events: [], steps: [], vals: {}, i: 0, mx: 0, my: 0, z: 3, sel: 0, show: true, busy: false };
  const key = (k, what) => { const s = el('span', 'margin:0 ' + T.gap); const b = el('b', 'border:1px solid ' + T.line + ';border-radius:4px;padding:1px 7px;margin-right:6px;color:' + T.accent, k); s.append(b, document.createTextNode(what)); return s; };
  const setHint = pairs => { hint.replaceChildren(...pairs.map(p => key(p[0], p[1]))); };
  const say = (m, ok) => { toast.textContent = m; toast.style.borderColor = ok ? T.good : T.bad; toast.style.opacity = 1; clearTimeout(dc.tt); dc.tt = setTimeout(() => { toast.style.opacity = 0; }, 4500); console.log('[drycastle] ' + m); };
  const row = (text, colour, weight) => el('div', 'color:' + (colour || T.ink) + ';font-weight:' + (weight || 400), text);
  const cur = () => dc.steps[dc.i];
  // a wall end at height z, in page pixels (the camera's numbers come with the fit)
  const P = (e, z) => { const f = dc.fit, d = e.d0 + e.d1 * z; return { x: f.w / 2 + f.f * (e.x0 + e.x1 * z) / d, y: f.h / 2 - f.f * (e.y0 + e.y1 * z) / d }; };
  const lineY = (w, z, x) => { const a = P(w.p, z), b = P(w.q, z); return a.y + (b.y - a.y) * (x - a.x) / ((b.x - a.x) || 1e-9); };
  const zAt = (w, x, y) => { let lo = -1, hi = 15; for (let k = 0; k < 24; k++) { const m = (lo + hi) / 2; if (lineY(w, m, x) > y) lo = m; else hi = m; } return Math.round((lo + hi) * 50) / 100; };
  const HOUSE = [['roof', 'Roof form'], ['roof_colour', 'Roof colour'], ['wall_colour', 'Wall colour'], ['frame_colour', 'Window frame colour']];
  const houseOf = () => { const h = Object.assign({}, (dc.frozen && dc.frozen.house) || {}); dc.steps.forEach((s, k) => { if (s.kind === 'pick' && dc.vals[k] != null) h[s.field] = dc.vals[k]; }); return h; };
  function drawPanel() {
    const g = dc.mode === 'look' ? dc.guide : dc.frozen, kids = [];
    const chip = el('span', 'float:right;font-size:' + T.small + ';border:1px solid ' + T.accent + ';color:' + T.accent + ';border-radius:4px;padding:0 6px', { look: 'LOOK', mark: 'MARK', confirm: 'DISCARD?' }[dc.mode]);
    const head = row('Capture guide', T.ink, 700); head.style.fontSize = T.title; head.prepend(chip); kids.push(head);
    if (!g) kids.push(row('Starting...', T.dim));
    else if (g.message) kids.push(row(g.message, T.poor));
    else {
      kids.push(row('Building ' + g.building + (g.parcel ? ' (' + g.parcel + ')' : '')));
      kids.push(row('Saved captures: ' + g.captures, T.dim));
      kids.push(el('div', 'border-top:1px solid ' + T.line + ';margin:' + T.gap + ' 0'));
      if (!g.walls.length) kids.push(row('No wall of it faces you from here', T.poor));
      for (const w of g.walls) {
        kids.push(row(GLYPH[w.status] + ' Wall ' + w.edge + ' (' + w.length + ' m): ' + WORD[w.status], T[w.status === 'no' ? 'bad' : w.status], 600));
        kids.push(row(w.reason, T.dim));
      }
      kids.push(el('div', 'border-top:1px solid ' + T.line + ';margin:' + T.gap + ' 0'));
      const h = dc.mode === 'look' ? (g.house || {}) : houseOf();
      if (g.full) for (const [f, name] of HOUSE) kids.push(row(name + ': ' + (h[f] || 'not set yet'), h[f] ? T.ink : T.dim));
      else kids.push(row('One capture square in front of the street wall is enough: the rest of the house is made up by rule', T.dim));
    }
    panel.replaceChildren(...kids);
  }
  function draw() {
    const kids = [], g = dc.mode === 'look' ? dc.guide : dc.frozen, s = dc.mode === 'look' ? null : cur();
    const walls = dc.mode !== 'look' && dc.fit ? dc.fit.walls : (g && g.walls) || [];
    const text = (x, y, c, label, anchor) => { const t = svgEl('text', { x: x, y: y, 'text-anchor': anchor || 'start', fill: c, 'font-size': 16, 'font-weight': 700, stroke: '#000', 'stroke-width': 4, 'paint-order': 'stroke' }); t.textContent = label; kids.push(t); };
    const seg = (a, b, c, dash, wide) => { kids.push(svgEl('line', { x1: a.x, y1: a.y, x2: b.x, y2: b.y, stroke: '#000', 'stroke-width': (wide || 2) + 2, 'stroke-opacity': .5 }));
      kids.push(svgEl('line', { x1: a.x, y1: a.y, x2: b.x, y2: b.y, stroke: c, 'stroke-width': wide || 2, 'stroke-dasharray': dash || '' })); };
    if (dc.show) for (const w of walls) {
      if (!w.quad) continue;
      const on = s && s.wall && s.wall.edge === w.edge, c = T[w.status === 'no' ? 'bad' : w.status], pts = w.quad.map(p => p[0] + ',' + p[1]).join(' ');
      kids.push(svgEl('polygon', { points: pts, fill: 'none', stroke: '#000', 'stroke-width': on ? 7 : 5, 'stroke-opacity': .55 }));
      kids.push(svgEl('polygon', { points: pts, fill: c, 'fill-opacity': on ? .16 : .07, stroke: c, 'stroke-width': on ? 5 : w.status === 'good' ? 3 : 2, 'stroke-dasharray': w.status === 'good' ? '' : w.status === 'poor' ? '12 7' : '3 6' }));
      text((w.quad[2][0] + w.quad[3][0]) / 2, Math.min(w.quad[2][1], w.quad[3][1]) - 10, c, GLYPH[w.status] + ' wall ' + w.edge, 'middle');
    }
    const H = innerHeight, W = innerWidth, vline = (x, c, dash, label, top) => { seg({ x: x, y: 0 }, { x: x, y: H }, c, dash); text(x + 8, top, c, label); };
    if (dc.mode !== 'look') {
      dc.steps.forEach((t, k) => { const v = dc.vals[k]; if (v == null || k === dc.i) return;
        if (t.kind === 'corner') vline(v, T.good, '', GLYPH.good + ' ' + t.name, 200 + 26 * (k % 3));
        if (t.kind === 'top') { seg(v.a, v.b, T.good, '', 3); text((v.a.x + v.b.x) / 2, (v.a.y + v.b.y) / 2 - 8, T.good, GLYPH.good + ' wall ' + t.wall.edge + ' top', 'middle'); }
        if (t.kind === 'ridge') { seg({ x: v[0] - 14, y: v[1] }, { x: v[0] + 14, y: v[1] }, T.good, '', 3); seg({ x: v[0], y: v[1] - 14 }, { x: v[0], y: v[1] + 14 }, T.good, '', 3); text(v[0] + 18, v[1] - 8, T.good, GLYPH.good + ' ridge'); } });
      if (dc.mode === 'mark' && s) {
        if (s.kind === 'corner') { vline(s.x, T.dim, '4 8', "map's guess", H - 150); vline(dc.mx, T.accent, '', s.name, H / 2); }
        if (s.kind === 'top' && dc.fit) { const a = P(s.wall.p, dc.z), b = P(s.wall.q, dc.z); seg(a, b, T.accent, '', 3); text((a.x + b.x) / 2, (a.y + b.y) / 2 - 10, T.accent, 'wall top', 'middle'); }
        if (s.kind === 'ridge') { seg({ x: 0, y: dc.my }, { x: W, y: dc.my }, T.accent); seg({ x: dc.mx, y: 0 }, { x: dc.mx, y: H }, T.accent); text(dc.mx + 10, dc.my - 10, T.accent, 'ridge'); }
      }
    }
    svg.replaceChildren(...kids);
  }
  function refresh() {
    shield.style.pointerEvents = dc.mode === 'look' ? 'none' : 'auto';
    prompt.style.display = dc.mode === 'look' ? 'none' : 'block';
    const s = cur(), undo = [['Backspace', 'step back'], ['Esc', 'discard capture']];
    if (dc.mode === 'look') setHint([['C', 'capture this view'], ['H', dc.show ? 'hide the outlines' : 'show the outlines']]);
    else if (dc.mode === 'confirm') { prompt.textContent = 'Discard this capture and everything marked on it? Nothing has been saved yet.'; setHint([['Enter', 'discard'], ['Esc', 'keep marking']]); }
    else { const n = 'Step ' + (dc.i + 1) + ' of ' + dc.steps.length + (dc.fit ? '' : '+') + ': ';
      if (s.kind === 'corner') { prompt.textContent = n + 'put the blue line on the ' + s.name + ', where the wall itself ends (not the roof edge)';
        setHint([['Click / Enter', 'place'], ['← →', 'nudge (Shift: 10 px)'], ['S', 'hidden, skip'], ...undo]); }
      else if (s.kind === 'fit') { prompt.textContent = n + (dc.fit ? dc.fit.message : 'fitting the outline to your marks...'); setHint([['Enter', dc.frozen.full ? 'it fits, go on' : 'it fits, save capture'], ...undo]); }
      else if (s.kind === 'edge') { prompt.textContent = n + 'what does the roof do above wall ' + s.wall.edge + ' (highlighted)?';
        setHint([['E', 'eave: the gutter runs along it'], ['G', 'gable: a triangle of wall'], ['N', 'neither / cannot tell'], ...undo]); }
      else if (s.kind === 'top') { prompt.textContent = n + 'put the blue line where wall ' + s.wall.edge + ' ends at the top: where the wall meets the underside of the roof at its corners, not the gutter';
        setHint([['Click / Enter', 'place'], ['↑ ↓', 'nudge (Shift: larger)'], ['S', 'hidden, skip'], ...undo]); }
      else if (s.kind === 'ridge') { prompt.textContent = n + 'put the cross on the highest line of the roof (the ridge), anywhere along it';
        setHint([['Click / Enter', 'place'], ['← ↑ ↓ →', 'nudge'], ['S', 'not in view, skip'], ...undo]); }
      else if (s.kind === 'house') { const h = dc.frozen.house; prompt.textContent = n + 'this house is already described: ' + HOUSE.map(p => p[1].toLowerCase() + ' ' + h[p[0]]).join(', ') + '. Still right?';
        setHint([['Enter', 'yes, keep'], ['E', 'change it'], ...undo]); }
      else if (s.kind === 'pick') { const kids = [el('div', 'margin-bottom:' + T.gap, n + s.title)];
        s.options.forEach((o, k) => { const r = el('div', 'display:flex;align-items:center;gap:' + T.gap + ';padding:3px 10px;font-size:' + T.body + ';font-weight:' + (k === dc.sel ? 700 : 400) + ';border:2px solid ' + (k === dc.sel ? T.accent : 'transparent') + ';border-radius:6px;text-align:left');
          r.append(el('span', 'width:14px;color:' + T.accent, k === dc.sel ? '▶' : ''));
          if (o.hex) r.append(el('span', 'width:28px;height:18px;border:1px solid ' + T.line + ';border-radius:3px;background:' + o.hex));
          r.append(el('span', '', o.name)); kids.push(r); });
        prompt.replaceChildren(...kids); setHint([['↑ ↓', 'choose'], ['Enter', 'take it'], ...undo]); }
      else { prompt.textContent = n + 'everything is marked. Save this capture?'; setHint([['Enter', 'save capture'], ...undo]); }
    }
    drawPanel(); draw();
  }
  function tail() {  // the steps after the fit: they need the fitted camera
    dc.steps.length = dc.i + 1;
    for (const w of dc.fit.walls) if (w.status !== 'no' && w.p) dc.steps.push({ kind: 'edge', wall: w }, { kind: 'top', wall: w });
    dc.steps.push({ kind: 'ridge' });
    const g = dc.frozen, have = g.house && HOUSE.every(p => g.house[p[0]]);
    if (have) dc.steps.push({ kind: 'house' }); else for (const [f, name] of HOUSE) dc.steps.push({ kind: 'pick', field: f, title: name, options: g.options[f] });
    dc.steps.push({ kind: 'done' });
  }
  function enter() { const s = cur();
    if (s.kind === 'corner') dc.mx = s.x;
    if (s.kind === 'fit' && !dc.fit) dc.events.push({ type: 'solve', marks: corners() });
    if (s.kind === 'top') { const prev = dc.steps.findIndex((t, k) => t.kind === 'top' && dc.vals[k]); dc.z = prev >= 0 ? dc.vals[prev].z : 3; }
    if (s.kind === 'ridge') { dc.mx = innerWidth / 2; dc.my = innerHeight / 3; }
    if (s.kind === 'pick') { const h = houseOf(); dc.sel = Math.max(0, s.options.findIndex(o => o.name === h[s.field])); }
    refresh(); }
  const corners = () => { const m = {}; dc.steps.forEach((s, k) => { if (s.kind === 'corner' && k in dc.vals) m[s.corner] = dc.vals[k]; }); return m; };
  const go = v => { dc.vals[dc.i] = v; dc.i++; enter(); };
  const back = () => { if (dc.i === 0) return; delete dc.vals[dc.i]; dc.i--; delete dc.vals[dc.i];
    if (cur().kind === 'corner') { dc.fit = null; dc.steps = dc.steps.filter(s => s.kind === 'corner' || s.kind === 'fit'); }
    enter(); };
  const leave = () => { dc.mode = 'look'; dc.frozen = null; dc.fit = null; dc.steps = []; dc.vals = {}; dc.i = 0; refresh(); };
  const place = () => { const s = cur();
    if (s.kind === 'corner') go(Math.round(dc.mx));
    else if (s.kind === 'top') go({ z: dc.z, a: P(s.wall.p, dc.z), b: P(s.wall.q, dc.z) });
    else if (s.kind === 'ridge') go([Math.round(dc.mx), Math.round(dc.my)]); };
  function save() { const walls = {}; let ridge = null;
    dc.steps.forEach((s, k) => { const v = dc.vals[k];
      if (s.kind === 'edge') (walls[s.wall.edge] = walls[s.wall.edge] || {}).roof_edge = v;
      if (s.kind === 'top' && v) (walls[s.wall.edge] = walls[s.wall.edge] || {}).top = [[v.a.x, v.a.y], [v.b.x, v.b.y]];
      if (s.kind === 'ridge' && v) ridge = v; });
    dc.events.push({ type: 'save', marks: { corners: corners(), walls: walls, ridge: ridge, house: houseOf() } }); prompt.textContent = 'Saving...'; }
  addEventListener('keydown', e => { const t = e.target && e.target.tagName; if (t === 'INPUT' || t === 'TEXTAREA' || e.ctrlKey || e.metaKey || e.altKey) return;
    const k = e.key.length === 1 ? e.key.toLowerCase() : e.key, big = e.shiftKey ? 10 : 1;
    if (dc.mode === 'look') {
      if (k === 'h') { dc.show = !dc.show; refresh(); }
      else if (k === 'c' && !dc.busy) {
        if (!dc.guide || dc.guide.message) { say(dc.guide ? dc.guide.message : 'The guide is still starting', false); return; }
        if (!dc.guide.full && !(dc.guide.corners || []).length) { say('No wall is usable from here: stand in front of the street wall with both its ends in view', false); return; }
        dc.busy = true; dc.frozen = dc.guide; dc.fit = null; dc.vals = {}; dc.i = 0; dc.events.push({ type: 'capture' });
        dc.steps = (dc.guide.corners || []).map(c => Object.assign({ kind: 'corner' }, c)); dc.steps.push({ kind: 'fit' });
        dc.mode = 'mark'; enter();
      } else return;
    } else if (dc.mode === 'confirm') {
      if (k === 'Enter') { dc.events.push({ type: 'discard' }); dc.busy = false; leave(); say('Capture discarded, nothing saved', false); }
      else if (k === 'Escape') { dc.mode = 'mark'; refresh(); }
    } else { const s = cur();
      if (k === 'Escape') { dc.mode = 'confirm'; refresh(); }
      else if (k === 'Backspace') back();
      else if (s.kind === 'corner') {
        if (k === 'Enter' || k === ' ') place();
        else if (k === 'ArrowLeft' || k === 'ArrowRight') { dc.mx = Math.max(0, Math.min(innerWidth, dc.mx + (k === 'ArrowLeft' ? -big : big))); draw(); }
        else if (k === 's') go(null);
      } else if (s.kind === 'fit') { if (k === 'Enter' && dc.fit) { if (dc.frozen.full) { tail(); go(true); } else save(); } }
      else if (s.kind === 'edge') { const v = { e: 'eave', g: 'gable', n: 'none' }[k]; if (v) go(v); }
      else if (s.kind === 'top') {
        if (k === 'Enter' || k === ' ') place();
        else if (k === 'ArrowUp' || k === 'ArrowDown') { dc.z = Math.round((dc.z + (k === 'ArrowUp' ? .01 : -.01) * big) * 100) / 100; draw(); }
        else if (k === 's') go(null);
      } else if (s.kind === 'ridge') {
        if (k === 'Enter' || k === ' ') place();
        else if (k.startsWith('Arrow')) { dc.mx += { ArrowLeft: -big, ArrowRight: big }[k] || 0; dc.my += { ArrowUp: -big, ArrowDown: big }[k] || 0; draw(); }
        else if (k === 's') go(null);
      } else if (s.kind === 'house') {
        if (k === 'Enter') go('keep');
        else if (k === 'e') { dc.steps.splice(dc.i, 1, ...HOUSE.map(p => ({ kind: 'pick', field: p[0], title: p[1], options: dc.frozen.options[p[0]] }))); enter(); }
      } else if (s.kind === 'pick') {
        if (k === 'ArrowUp' || k === 'ArrowDown') { dc.sel = (dc.sel + (k === 'ArrowUp' ? -1 : 1) + s.options.length) % s.options.length; refresh(); }
        else if (k === 'Enter') go(s.options[dc.sel].name);
      } else if (s.kind === 'done') { if (k === 'Enter') save(); }
    }
    e.preventDefault(); e.stopImmediatePropagation();
  }, true);
  shield.addEventListener('mousemove', e => { if (dc.mode !== 'mark') return; const s = cur(); dc.mx = e.clientX; dc.my = e.clientY;
    if (s.kind === 'top' && dc.fit) dc.z = zAt(s.wall, e.clientX, e.clientY);
    if (s.kind === 'corner' || s.kind === 'top' || s.kind === 'ridge') draw(); });
  shield.addEventListener('click', e => { if (dc.mode !== 'mark') return; const s = cur(); dc.mx = e.clientX; dc.my = e.clientY;
    if (s.kind === 'top' && dc.fit) dc.z = zAt(s.wall, e.clientX, e.clientY);
    if (s.kind === 'corner' || s.kind === 'top' || s.kind === 'ridge') place(); });
  for (const ev of ['mousedown', 'mouseup', 'wheel', 'dblclick', 'contextmenu']) shield.addEventListener(ev, e => { e.preventDefault(); e.stopPropagation(); }, { passive: false });
  dc.pull = () => ({ events: dc.events.splice(0), w: innerWidth, h: innerHeight, mode: dc.mode });
  dc.setGuide = g => { dc.guide = g; if (dc.mode === 'look') { drawPanel(); draw(); } };
  dc.shot = on => { root.style.display = on ? 'none' : ''; return new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))); };
  dc.setFit = r => { if (dc.mode !== 'look' && cur() && cur().kind === 'fit') { dc.fit = r; refresh(); } };
  dc.saved = (m, ok) => { dc.busy = false; leave(); say(m, ok); };
  addEventListener('resize', () => draw());
  refresh(); return 1; })()"""


def wall_status(b: dict, others: list[dict], cam: dict) -> list[dict]:
    """Every outer wall of ``b`` that faces the camera, with what a capture from this camera is worth for it:
    {edge, length, status good|poor|no, reason, off, quad (pixels: foot left, foot right, top right, top left as the
    map has them, or None behind the camera), ends: [(corner, x)]}."""
    from drycastle_pipeline.photos.landmark_tools import _edges, _seg_dist

    from shapely.geometry import LineString, Polygon

    ring = b["footprint"]["exterior"]
    body = Polygon(ring)
    out = []
    for e in _edges(b):
        mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
        if e["length"] < MIN_WALL_M:
            continue
        if any(_seg_dist(mid, tuple(o["footprint"]["exterior"][j]), tuple(o["footprint"]["exterior"][(j + 1) % len(o["footprint"]["exterior"])])) < 0.1
               for o in others for j in range(len(o["footprint"]["exterior"]))):  # fmt: skip
            continue
        dx, dy = cam["x"] - mid[0], cam["y"] - mid[1]
        cos = (dx * e["normal"][0] + dy * e["normal"][1]) / max(math.hypot(dx, dy), 1e-9)
        if cos <= 0.02:
            continue
        sight = LineString([(cam["x"], cam["y"]), (mid[0] + e["normal"][0] * 0.2, mid[1] + e["normal"][1] * 0.2)])
        if sight.intersection(body).length > 0.3:  # another part of the building stands in front of it
            continue
        off = round(math.degrees(math.acos(min(1.0, cos))))
        i, j = e["i"], (e["i"] + 1) % len(ring)
        px = project(cam, np.array([[*ring[i], 0.0], [*ring[j], 0.0], [*ring[j], WALL_TOP_M], [*ring[i], WALL_TOP_M], [*ring[i], cam["z"]], [*ring[j], cam["z"]]]))
        w = {"edge": i, "length": round(e["length"], 1), "off": off, "quad": None, "ends": []}
        if not np.isfinite(px).all():
            out.append({**w, "status": "no", "reason": "Partly behind you: turn towards it"})
            continue
        w["quad"] = [[round(float(x), 1), round(float(y), 1)] for x, y in px[:4]]
        w["ends"] = [(i, float(px[4, 0])), (j, float(px[5, 0]))]
        lo, hi = float(px[4:, 0].min()), float(px[4:, 0].max())
        if off > MAX_OFF_DEG:
            w.update(status="no", reason=f"Seen {off} degrees off square: far too steep")
        elif lo < 0 or hi > cam["w"]:
            side = "Both ends are" if lo < 0 and hi > cam["w"] else "Its left end is" if lo < 0 else "Its right end is"
            w.update(status="no", reason=f"{side} out of view: turn or step back")
        elif off > GOOD_OFF_DEG:
            w.update(status="poor", reason=f"Seen {off} degrees off square: stand more in front of it")
        else:
            w.update(status="good", reason=f"Both ends in view, {off} degrees off square")
        out.append(w)
    return out


def corners_to_mark(walls: list[dict], only_good: bool = False) -> list[dict]:
    """The corners to mark in a capture: the ends of its usable walls (``only_good``: of its well seen walls), each
    corner once, left to right."""
    seen: dict[int, dict] = {}
    if only_good and not any(w["status"] == "good" for w in walls):
        # no wall is well seen as the map has it, and the map's place is a metre or two off: take the squarest usable one
        walls, only_good = sorted((w for w in walls if w["status"] == "poor"), key=lambda w: w["off"])[:1], False
    for w in walls:
        if w["status"] == "no" or (only_good and w["status"] != "good"):
            continue
        (a, ax), (b, bx) = w["ends"]
        for corner, x, side in ((a, ax, "left" if ax < bx else "right"), (b, bx, "left" if bx < ax else "right")):
            if corner in seen:
                seen[corner]["walls"].append(w["edge"])
            else:
                seen[corner] = {"corner": corner, "x": round(x, 1), "walls": [w["edge"]], "side": side}
    out = sorted(seen.values(), key=lambda c: c["x"])
    for c in out:
        c["name"] = f"corner between walls {c['walls'][0]} and {c['walls'][1]}" if len(c["walls"]) > 1 else f"{c['side']} end of wall {c['walls'][0]}"
    return out


class Guide:
    """Keeps the guide of every Street View tab of one Chrome up to date and files its captures; ``poll()`` once per
    look at the tabs."""

    def __init__(self, port: int, photos: Path = PHOTOS_DIR, full: bool = False) -> None:
        self.full = full  # also ask for each wall's roof edge and top, the ridge and the house's colours
        from drycastle_pipeline.photos.landmark_tools import WORK

        self.port, self.photos = port, photos
        self.buildings = {b["id"]: b for b in json.loads((WORK / "buildings.json").read_text())["buildings"]}
        self.origin = json.loads((WORK / "parcels.json").read_text())["origin_epsg5514"]
        try:
            from drycastle_pipeline.terrain.terrain import load_terrain

            self.terrain, self.elevations = load_terrain(WORK, mmap=True)
        except Exception as e:  # noqa: BLE001 - the guide still works on flat ground
            print(f"capture guide: no terrain ({e}): the camera is taken 2.5 m above the building's base", file=sys.stderr)
            self.terrain, self.elevations = None, {}
        self.pending: dict[str, dict] = {}
        self.sent: dict[str, str] = {}

    def camera(self, view: dict, w: int, h: int, bid: str | None) -> dict:
        from drycastle_pipeline.photos.streetview_follow import _TO_JTSK

        ex, ey = _TO_JTSK.transform(view["lon"], view["lat"])
        x, y = ex - self.origin[0], ey - self.origin[1]
        cam = fr.capture_camera({**view, "x": x, "y": y, "width": w, "height": h})
        if self.terrain is not None and bid is not None:
            ground = float(self.terrain.surface(np.array([x]), np.array([y]))[0])
            cam["z"] = ground + 2.5 - float(self.elevations.get(bid, ground))
        return cam

    def look(self, url: str, w: int, h: int) -> dict:
        """What the tab shows now: {building, parcel, captures, walls, corners, cam} or {message}."""
        view = parse_view(url)
        if view is None:
            return {"message": "Not in Street View yet: drop the yellow figure on a street"}
        flat = self.camera(view, w, h, None)
        bid, _, _ = guess_building(flat["x"], flat["y"], view["heading"])
        if bid is None:
            return {"message": "No building in the middle of the view: aim at a house"}
        b = self.buildings[bid]
        cam = self.camera(view, w, h, bid)
        near = [o for o in self.buildings.values() if o["id"] != bid and math.dist(o["footprint"]["exterior"][0], b["footprint"]["exterior"][0]) < 80]
        walls = wall_status(b, near, cam)
        return {"building": bid, "parcel": b.get("parcel"), "captures": len(list((self.photos / bid).glob("*.json"))), "walls": walls,
                "corners": corners_to_mark(walls, only_good=not self.full), "cam": cam, "house": self.house(bid), "options": house_options(), "full": self.full}  # fmt: skip

    def house(self, bid: str) -> dict:
        """What was last said of the whole house (roof form and colours) at a capture of it."""
        for side in sorted((self.photos / bid).glob("*.json"), key=lambda p: int(p.stem) if p.stem.isdigit() else -1, reverse=True):
            got = (json.loads(side.read_text()).get("marks") or {}).get("house")
            if got:
                return got
        return {}

    def fit(self, held: dict, marks: dict) -> dict:
        """The outline refitted to the marked corners: {walls, message, cam}."""
        from drycastle_pipeline.photos.facade_pipeline import move_camera

        g = held["guide"]
        b, cam = self.buildings[g["building"]], g["cam"]
        ring = b["footprint"]["exterior"]
        picks = [(tuple(ring[int(k)]), float(x)) for k, x in marks.items() if x is not None]
        near = [o for o in self.buildings.values() if o["id"] != b["id"] and math.dist(o["footprint"]["exterior"][0], ring[0]) < 80]
        if len(picks) < 2:
            return self._fitted(b, g["walls"], cam, "fewer than two corners are marked, so the outline stays where the map put it. Heights marked next will be rougher")
        moved = move_camera(cam, picks)
        step = math.dist((moved["x"], moved["y"]), (cam["x"], cam["y"]))
        if not math.isfinite(step) or step > MAX_MOVE_M:
            return self._fitted(b, g["walls"], cam, f"these marks would put the camera {step:.0f} m away, which cannot be: press Backspace and mark again")
        return self._fitted(b, wall_status(b, near, moved), moved, f"the outline is refitted to your marks (camera moved {step:.1f} m). Does it sit on the walls?")

    def _fitted(self, b: dict, walls: list[dict], cam: dict, message: str) -> dict:
        """The fit as the tab needs it: each wall's two ends as the numbers that give their pixel at any height."""
        ring = b["footprint"]["exterior"]
        f, r, u = fr._axes(cam)

        def end(k: int) -> dict:
            rel = np.array([ring[k][0] - cam["x"], ring[k][1] - cam["y"], -cam["z"]])
            return {"d0": float(rel @ f), "d1": float(f[2]), "x0": float(rel @ r), "x1": float(r[2]), "y0": float(rel @ u), "y1": float(u[2])}

        out = [{**w, **({"p": end(w["edge"]), "q": end((w["edge"] + 1) % len(ring))} if w["quad"] else {})} for w in walls]
        return {"walls": out, "cam": cam, "message": message, "f": cam["f"], "w": cam["w"], "h": cam["h"]}

    def poll(self) -> None:
        try:
            for t in tabs(self.port):
                url = t.get("url", "")
                if "google." not in url or "/maps" not in url:
                    continue
                cdp = Cdp(t["webSocketDebuggerUrl"])
                try:
                    self._tab(cdp, t["id"], url)
                finally:
                    cdp.close()
        except (OSError, RuntimeError, KeyError, ValueError) as e:
            print(f"capture guide: {e}", file=sys.stderr)

    def _eval(self, cdp: Cdp, expr: str, wait: bool = False):
        return cdp.call("Runtime.evaluate", {"expression": expr, "returnByValue": True, "awaitPromise": wait})["result"].get("value")

    def _tab(self, cdp: Cdp, tab: str, url: str) -> None:
        self._eval(cdp, OVERLAY_JS)
        st = self._eval(cdp, "window.__dc.pull()")
        if not st:
            return
        w, h = int(st["w"]), int(st["h"])
        for ev in st["events"]:
            if ev["type"] == "capture":
                guide = self.look(url, w, h)
                self._eval(cdp, "window.__dc.shot(true)", wait=True)
                shot = cdp.call("Page.captureScreenshot", {"format": "png"})
                self._eval(cdp, "window.__dc.shot(false)", wait=True)
                self.pending[tab] = {"png": base64.b64decode(shot["data"]), "url": url, "guide": guide, "w": w}
            elif ev["type"] == "solve" and tab in self.pending:
                held = self.pending[tab]
                got = self.fit(held, ev["marks"])
                held["cam_fit"] = got["cam"]
                self._eval(cdp, f"window.__dc.setFit({json.dumps({**{k: v for k, v in got.items() if k != 'cam'}, 'walls': _plain(got['walls'])})})")
            elif ev["type"] == "save" and tab in self.pending:
                self._save(cdp, tab, ev["marks"])
            elif ev["type"] == "discard":
                self.pending.pop(tab, None)
        if st["mode"] == "look":
            guide = self.look(url, w, h)
            doc = json.dumps({**{k: v for k, v in guide.items() if k != "cam"}, "walls": _plain(guide.get("walls", []))})
            if self.sent.get(tab) != doc:
                self._eval(cdp, f"window.__dc.setGuide({doc})")
                self.sent[tab] = doc

    def _save(self, cdp: Cdp, tab: str, marks: dict) -> None:
        from io import BytesIO

        from PIL import Image

        held = self.pending.pop(tab)
        side = save_capture(held["png"], held["url"], self.photos)
        if side is None:
            self._eval(cdp, f"window.__dc.saved({json.dumps('Not saved: this tab is not in Street View')}, false)")
            return
        scale = Image.open(BytesIO(held["png"])).width / held["w"]  # picture pixels per page pixel (the screen's scaling)
        doc = json.loads(side.read_text())
        ends = {str(k): round(float(x) * scale, 1) for k, x in (marks.get("corners") or {}).items() if x is not None}
        g = held["guide"]
        if g.get("building") and doc.get("building") == g["building"]:
            doc["ends"] = ends  # corner number of the footprint -> picture column where the wall ends, marked by hand
            # the rest marked by hand, in picture pixels: per wall what the roof does above it (eave, gable, none) and
            # the two ends of the line along its top; a point on the ridge; the whole house's roof form and colours
            doc["marks"] = {
                "walls": {str(e): {**({"roof_edge": v["roof_edge"]} if v.get("roof_edge") else {}),
                                   **({"top": [[round(x * scale, 1), round(y * scale, 1)] for x, y in v["top"]]} if v.get("top") else {})}
                          for e, v in (marks.get("walls") or {}).items()},
                "ridge": [round(v * scale, 1) for v in marks["ridge"]] if marks.get("ridge") else None,
                "house": marks.get("house") or {},
            }  # fmt: skip
            doc["walls_seen"] = [{"edge": w["edge"], "status": w["status"], "off_square_deg": w["off"]} for w in g["walls"]]
            side.write_text(json.dumps(doc, indent=1))
        n = len(list(side.parent.glob("*.json")))
        good = [w["edge"] for w in g.get("walls", []) if w["status"] == "good"]
        text = (f"Saved {doc['building']} #{side.stem} with {len(ends)} marked corner(s); walls measured well: {good or 'none'} "
                f"({n} capture{'s' if n != 1 else ''} of it)") if doc.get("building") else f"Saved as unknown/{side.stem}: no building in the middle of the view"  # fmt: skip
        print(f"capture guide: {text}", flush=True)
        self._eval(cdp, f"window.__dc.saved({json.dumps(text)}, {'true' if doc.get('building') else 'false'})")
        self.sent.pop(tab, None)


def house_options() -> dict:
    """What can be said of the whole house, as the pipeline's own lists have it: {field: [{name, hex?}]}."""
    from drycastle_pipeline.photos import facade_pipeline as fp

    return {"roof": [{"name": n} for n in fp.ROOFS], "roof_colour": [{"name": n, "hex": h} for n, h in fp.ROOF_COLOURS.items()],
            "wall_colour": [{"name": n, "hex": h} for n, h in fp.WALL_COLOURS.items()], "frame_colour": [{"name": n, "hex": h} for n, h in fp.FRAME_COLOURS.items()]}  # fmt: skip


def _plain(walls: list[dict]) -> list[dict]:
    return [{k: v for k, v in w.items() if k != "ends"} for w in walls]

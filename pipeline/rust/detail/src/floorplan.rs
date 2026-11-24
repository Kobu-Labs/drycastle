//! The floor plan of a hollow house: the search behind ``buildings/floorplan.py`` (which describes the rules; ADR 0041,
//! 0042). Python turns the building into the *entry frame* and back; everything between is here: the programme, the hall,
//! the rooms, the doorways, the scoring and the pick of the best plan.
//!
//! Geometry is GEOS (as shapely's), random numbers are Python's. Speed comes from threads (houses, and the candidates of
//! one house), from not asking GEOS what plain arithmetic answers, and from not computing the same shared wall twice.

use crate::geom::{self, Pt, PolyC, G};
use crate::planting::PyRandom;
use crate::pyrand::{py_hypot, py_round};
use geos::{CapStyle, Geom, GeometryTypes, JoinStyle};
use rayon::prelude::*;
use serde_json::{json, Value};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

const DOOR_W_M: f64 = 0.9;
const HALL_W_M: (f64, f64) = (1.2, 1.5);
const SLIVER_M: f64 = 1.7; // a strip narrower than this between the hall and a wall is made part of the hall
const MIN_PIECE_M2: f64 = 5.0; // floor beside the hall smaller than this is hall
const CLEAR_M: f64 = 0.15; // a partition ends at least this far from a window's or a door's side
const CLEAR_LOOSE_M: f64 = 0.07; // when nothing else fits: nearer,
const POST_M: f64 = 0.04; // or on a window's post (within this of its middle)
const SPINE_MIN_DEPTH_M: f64 = 6.5; // a corridor along the house needs rooms on both sides of it
const SNAP_M: f64 = 0.4; // a cut this near a corner of the piece is moved onto it
const SLIM: f64 = 2.4; // a room is not longer than this many times its width;
const SLIM_LOOSE: f64 = 3.6; // when nothing else fits
const SIDE_ROOM_DEPTH_M: f64 = 2.6; // a corridor along the door's wall needs rooms this deep behind it
const SMALL_MOST_M2: f64 = 6.5; // a bathroom or a closet is asked for no larger than this when floor is cut a second time
const HALL_MOST_W_M: f64 = 2.8; // a stem widened past this (a narrow wing, wall to wall) is no hall: the door opens into a room
const CORRIDOR_ROOM_DEPTH_M: f64 = 2.2; // a corridor to rooms nothing else reaches makes do with less
const NO_HALL_BELOW_M2: f64 = 80.0; // an open house smaller than this has no hall: the door opens into the living space
const TRIES: usize = 4; // plans drawn at most for each kind of hall; the one with the fewest faults is kept
const GOOD_FAULTS: f64 = 0.8; // a plan with so few ends the search
const HALL_SHARE: f64 = 0.12; // the floor a hall may take before it counts as waste
const HALL_FREE_M2: f64 = 4.0;
const HALL_PER_DOOR_M2: f64 = 3.0;
const MIN_PIECE_AREA: f64 = 1e-6;
const CAR_M: (f64, f64) = (2.4, 4.8); // the floor a parked car needs, across and in from its garage door
const GARAGE_W_M: [f64; 4] = [3.0, 3.3, 2.7, 3.8]; // widths and depths tried for a garage, the usual first
const GARAGE_D_M: [f64; 5] = [5.5, 5.2, 6.0, 5.0, 100.0]; // (the last: the whole depth of the building)
const GARAGE_MOST_M2: f64 = 18.0; // floor past this is taken from the house for nothing
const MIN_HOUSE_M2: f64 = 14.0; // less than this left beside a garage is no house: the whole building is the garage

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Kind {
    Living,
    Kitchen,
    LivingKitchen,
    Dining,
    Bedroom,
    Bathroom,
    Storage,
    Study,
    Hall,
    Garage,
}
use Kind::*;

impl Kind {
    fn name(self) -> &'static str {
        match self {
            Living => "living",
            Kitchen => "kitchen",
            LivingKitchen => "living_kitchen",
            Dining => "dining",
            Bedroom => "bedroom",
            Bathroom => "bathroom",
            Storage => "storage",
            Study => "study",
            Hall => "hall",
            Garage => "garage",
        }
    }
    /// Rooms that keep their size however large the house is.
    fn small(self) -> bool {
        matches!(self, Bathroom | Storage)
    }
    fn living_space(self) -> bool {
        matches!(self, Living | LivingKitchen | Kitchen | Dining)
    }
    fn private(self) -> bool {
        matches!(self, Bedroom | Study)
    }
    fn min_side(self) -> f64 {
        match self {
            Living | LivingKitchen => 3.0,
            Kitchen => 2.2,
            Dining | Bedroom => 2.4,
            Study => 2.0,
            Bathroom => 1.5,
            Storage => 1.1,
            Hall | Garage => 0.0,
        }
    }
    fn min_area(self) -> f64 {
        match self {
            Living => 12.0,
            LivingKitchen => 14.0,
            Kitchen => 5.0,
            Dining | Bedroom => 7.0,
            Study => 6.0,
            Bathroom => 3.0,
            Storage => 1.5,
            Hall | Garage => 0.0,
        }
    }
    fn most_area(self) -> f64 {
        match self {
            Living => 45.0,
            LivingKitchen => 60.0,
            Kitchen | Dining => 20.0,
            Bedroom => 22.0,
            Study => 16.0,
            Bathroom => 9.0,
            Storage => 7.0,
            Hall | Garage => f64::INFINITY,
        }
    }
    /// Who a room is entered from, best first (a hall: a corridor further in).
    fn hosts(self) -> &'static [Kind] {
        match self {
            Living | LivingKitchen => &[Hall],
            Kitchen => &[Living, Dining, Hall],
            Dining => &[Kitchen, Living, LivingKitchen, Hall],
            Bathroom => &[Hall, Bedroom, LivingKitchen, Living],
            Storage => &[Hall, Kitchen, LivingKitchen, Bedroom, Living, Dining, Study],
            Bedroom | Study => &[Hall, LivingKitchen, Living, Dining],
            Hall => &[Hall, LivingKitchen, Living, Dining, Kitchen],
            Garage => &[Hall, Kitchen, Storage, LivingKitchen, Living, Dining, Study, Bedroom],
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Style {
    Closed,
    Mixed,
    Open,
}

/// CPython's ``sum`` of floats (Neumaier's compensated summation).
fn py_sum(values: impl Iterator<Item = f64>) -> f64 {
    let (mut s, mut c) = (0.0f64, 0.0f64);
    for x in values {
        let t = s + x;
        if s.abs() >= x.abs() {
            c += (s - t) + x;
        } else {
            c += (x - t) + s;
        }
        s = t;
    }
    if c != 0.0 && c.is_finite() {
        s + c
    } else {
        s
    }
}

/// Python's ``min(items, key=key)``: the first of the smallest.
fn first_min<T, K: PartialOrd>(items: impl IntoIterator<Item = T>, mut key: impl FnMut(&T) -> K) -> Option<T> {
    let mut best: Option<(K, T)> = None;
    for item in items {
        let k = key(&item);
        if best.as_ref().map_or(true, |(b, _)| k < *b) {
            best = Some((k, item));
        }
    }
    best.map(|b| b.1)
}

/// Python's ``max(items, key=key)``: the first of the largest.
fn first_max<T, K: PartialOrd>(items: impl IntoIterator<Item = T>, mut key: impl FnMut(&T) -> K) -> Option<T> {
    let mut best: Option<(K, T)> = None;
    for item in items {
        let k = key(&item);
        if best.as_ref().map_or(true, |(b, _)| k > *b) {
            best = Some((k, item));
        }
    }
    best.map(|b| b.1)
}

fn cmpf(a: f64, b: f64) -> std::cmp::Ordering {
    a.partial_cmp(&b).unwrap_or(std::cmp::Ordering::Equal)
}

fn dist(a: Pt, b: Pt) -> f64 {
    py_hypot(a[0] - b[0], a[1] - b[1])
}

// --- the programme (ADR 0042) -----------------------------------------------------------------------------------

#[derive(Clone, Copy)]
struct Want {
    kind: Kind,
    area: f64,
}

fn want(kind: Kind, area: f64) -> Want {
    Want { kind, area }
}

/// The same rooms with their sizes adding up to ``area``: the small ones keep theirs (within a quarter), the others share the rest.
fn scaled(wants: &[Want], area: f64) -> Vec<Want> {
    let total = py_sum(wants.iter().map(|w| w.area));
    if total <= 0.0 || wants.is_empty() {
        return wants.to_vec();
    }
    let k = area / total;
    let all = |k: f64| wants.iter().map(|w| want(w.kind, w.area * k)).collect::<Vec<_>>();
    let small = py_sum(wants.iter().filter(|w| w.kind.small()).map(|w| w.area));
    let (any_small, any_big) = (wants.iter().any(|w| w.kind.small()), wants.iter().any(|w| !w.kind.small()));
    if !any_big || !any_small {
        return all(k);
    }
    let ks = k.max(0.8).min(1.25);
    let kb = (area - ks * small) / py_sum(wants.iter().filter(|w| !w.kind.small()).map(|w| w.area));
    if kb <= 0.3 {
        return all(k);
    }
    wants.iter().map(|w| want(w.kind, w.area * if w.kind.small() { ks } else { kb })).collect()
}

/// The rooms for ``area`` m² of floor (the hall not counted) in a house of this style, front door first.
fn programme(area: f64, style: Style, rng: &mut PyRandom) -> Vec<Want> {
    if area < 20.0 {
        return vec![want(LivingKitchen, area)];
    }
    if area < 34.0 {
        return scaled(&[want(LivingKitchen, area - 4.5), want(Bathroom, 4.5)], area);
    }
    let beds = if area < 52.0 { 1 } else if area < 82.0 { 2 } else if area < 125.0 { 3 } else { 4 };
    let mut wants = vec![];
    if area > 75.0 {
        wants.push(want(Storage, rng.uniform(2.2, 3.6))); // the closet by the door
    }
    if style == Style::Open {
        wants.push(want(LivingKitchen, (area * rng.uniform(0.38, 0.48)).min(58.0)));
    } else {
        let share = if style == Style::Mixed { rng.uniform(0.26, 0.32) } else { rng.uniform(0.22, 0.28) };
        wants.push(want(Living, (area * share).min(40.0)));
        wants.push(want(Kitchen, (area * rng.uniform(0.10, 0.14)).max(7.0).min(16.0)));
        if area > 105.0 && (style == Style::Closed || rng.random() < 0.5) {
            wants.push(want(Dining, rng.uniform(10.0, 14.0)));
        }
    }
    let bath = want(Bathroom, rng.uniform(4.5, 6.5));
    if area > 60.0 && rng.random() < 0.7 {
        wants.push(want(Storage, rng.uniform(2.5, 5.0))); // pantry or utility room, next to the kitchen
    }
    wants.push(bath);
    let mut private: Vec<Want> = (0..beds - 1).map(|_| want(Bedroom, rng.uniform(9.0, 12.5))).collect();
    if area > 100.0 && rng.random() < 0.6 {
        private.insert(0, want(Study, rng.uniform(8.0, 11.5)));
    }
    if beds >= 3 && rng.random() < 0.6 {
        private.push(want(Bathroom, rng.uniform(3.5, 4.8))); // beside the last bedroom: its own, where the plan lets it
    }
    private.push(want(Bedroom, rng.uniform(13.0, 17.0)));
    wants.extend(private);
    while py_sum(wants.iter().map(|w| w.area)) < 0.88 * area {
        // a large house: more rooms before larger ones
        let extra = if rng.random() < 0.5 {
            want(Bedroom, rng.uniform(10.0, 14.0))
        } else if rng.random() < 0.5 {
            want(Study, rng.uniform(9.0, 13.0))
        } else {
            want(Storage, rng.uniform(4.0, 7.0))
        };
        wants.insert(wants.len() - 1, extra);
    }
    scaled(&wants, area)
}

// --- geometry, as shapely does it -------------------------------------------------------------------------------

fn buffered(g: &G, d: f64, join: JoinStyle) -> G {
    g.buffer_with_style(d, 16, CapStyle::Round, join, 5.0).unwrap_or_else(|_| geom::empty())
}

/// Every polygon of a geometry larger than a crumb.
fn solid(g: &G) -> Vec<G> {
    geom::polys_g(g).into_iter().filter(|p| geom::area(p) > MIN_PIECE_AREA).collect()
}

/// The parts of a geometry, or the geometry itself.
fn parts(g: &G) -> Vec<G> {
    match g.geometry_type() {
        Ok(GeometryTypes::MultiPoint | GeometryTypes::MultiLineString | GeometryTypes::MultiPolygon | GeometryTypes::GeometryCollection) => {
            let n = g.get_num_geometries().unwrap_or(0);
            (0..n).filter_map(|i| g.get_geometry_n(i).ok().map(|c| geom::dup(&c))).collect()
        }
        _ => vec![geom::dup(g)],
    }
}

fn is_line(g: &G) -> bool {
    matches!(g.geometry_type(), Ok(GeometryTypes::LineString))
}

fn poly_of(g: &G) -> PolyC {
    geom::polys(g).into_iter().next().unwrap_or_default()
}

/// ``orient(polygon, 1.0)``
fn oriented(g: &G) -> G {
    geom::from_polyc(&geom::orient(&poly_of(g)))
}

fn rect(x0: f64, y0: f64, x1: f64, y1: f64) -> G {
    geom::box_ccw(x0, y0, x1, y1)
}

/// The area of a ring on the side ``coordinate <= c`` of a line square to ``axis``.
fn ring_below(ring: &[Pt], axis: usize, c: f64) -> f64 {
    let mut first: Option<Pt> = None;
    let mut last: Pt = [0.0, 0.0];
    let mut twice = 0.0;
    let mut push = |p: Pt| {
        // (relative to the first point kept: the sum stays small)
        match first {
            None => first = Some(p),
            Some(o) => twice += (last[0] - o[0]) * (p[1] - o[1]) - (p[0] - o[0]) * (last[1] - o[1]),
        }
        last = p;
    };
    for w in ring.windows(2) {
        let (a, b) = (w[0], w[1]);
        let (ina, inb) = (a[axis] <= c, b[axis] <= c);
        if ina {
            push(a);
        }
        if ina != inb {
            let t = (c - a[axis]) / (b[axis] - a[axis]);
            let mut p = [a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])];
            p[axis] = c;
            push(p);
        }
    }
    (twice / 2.0).abs()
}

fn area_below(p: &PolyC, axis: usize, c: f64) -> f64 {
    ring_below(&p.ext, axis, c) - p.holes.iter().map(|h| ring_below(h, axis, c)).sum::<f64>()
}

/// The bounds of what of a ring lies on one side (`below`: at or before) of that line, the points on the line included.
fn side_bounds(ring: &[Pt], axis: usize, c: f64, below: bool) -> [f64; 4] {
    let mut b = [f64::INFINITY, f64::INFINITY, f64::NEG_INFINITY, f64::NEG_INFINITY];
    let mut add = |p: Pt| b = [b[0].min(p[0]), b[1].min(p[1]), b[2].max(p[0]), b[3].max(p[1])];
    let inside = |p: Pt| if below { p[axis] <= c } else { p[axis] >= c };
    for w in ring.windows(2) {
        let (p, q) = (w[0], w[1]);
        if inside(p) {
            add(p);
        }
        if inside(p) != inside(q) {
            let t = (c - p[axis]) / (q[axis] - p[axis]);
            let mut at = [p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])];
            at[axis] = c;
            add(at);
        }
    }
    b
}

/// No edge shorter than a millimetre and no corner that near another edge: cutting such an outline leaves no crumbs, so
/// its corners tell how far each side of a cut reaches.
fn clean_outline(p: &PolyC) -> bool {
    let rings: Vec<&Vec<Pt>> = std::iter::once(&p.ext).chain(&p.holes).collect();
    let edges: Vec<(Pt, Pt)> = rings.iter().flat_map(|r| r.windows(2).map(|w| (w[0], w[1]))).collect();
    edges.iter().all(|&(a, b)| {
        let (dx, dy) = (b[0] - a[0], b[1] - a[1]);
        let len2 = dx * dx + dy * dy;
        len2 > 1e-6
            && edges.iter().all(|&(v, _)| {
                if v == a || v == b {
                    return true;
                }
                let t = (((v[0] - a[0]) * dx + (v[1] - a[1]) * dy) / len2).max(0.0).min(1.0);
                let (ex, ey) = (v[0] - a[0] - t * dx, v[1] - a[1] - t * dy);
                ex * ex + ey * ey > 1e-6
            })
    })
}

/// Is a room of this area, at least `least_wh` and at most `most_wh` in extent, too small, too large, too shallow or too
/// long for `group`? `slack`: how far past a limit it must be to count.
fn misfit(area: f64, least_wh: (f64, f64), most_wh: (f64, f64), group: &[Want], loose: bool, slack: f64) -> bool {
    let side_m = group.iter().map(|w| w.kind.min_side()).fold(f64::MIN, f64::max); // deep enough for every room it is to hold
    let asked = py_sum(group.iter().map(|w| w.area));
    let least = py_sum(group.iter().map(|w| w.kind.min_area())).max(if loose { 0.3 } else { 0.55 } * asked);
    let slim_most = if loose { SLIM_LOOSE } else { SLIM };
    let narrow = most_wh.0.min(most_wh.1);
    area < least - slack
        || area > if loose { 3.0 } else { 1.7 } * asked + slack
        || narrow < side_m - slack
        || least_wh.0.max(least_wh.1) / narrow > slim_most * group.len() as f64 + slack // a room like a corridor, or a row of them
}

/// A rectangle square to the frame at least `side` wide has no narrow arm: nothing to ask GEOS.
fn plain_rect(g: &G, side: f64) -> bool {
    if g.get_num_interior_rings().unwrap_or(1) != 0 {
        return false;
    }
    let b = geom::bounds(g);
    let (w, h) = (b[2] - b[0], b[3] - b[1]);
    w.min(h) >= side && (geom::area(g) - w * h).abs() < 1e-9
}

type Seg = (Pt, Pt);

/// A piece of floor, with what is asked of it again and again.
struct Cell {
    id: u32,
    g: G,
    area: f64,
    bounds: [f64; 4],
}

fn pt_less(a: Pt, b: Pt) -> bool {
    a[0] < b[0] || (a[0] == b[0] && a[1] < b[1])
}

fn seg_cmp(a: &Seg, b: &Seg) -> std::cmp::Ordering {
    cmpf(a.0[0], b.0[0]).then(cmpf(a.0[1], b.0[1])).then(cmpf(a.1[0], b.1[0])).then(cmpf(a.1[1], b.1[1]))
}

/// The straight pieces of the boundary two cells share.
fn shared_walls(a: &G, b: &G) -> Vec<Seg> {
    let line = geom::inter(&geom::boundary(a), &buffered(&geom::boundary(b), 1e-4, JoinStyle::Round));
    let mut out: Vec<Seg> = vec![];
    for g in parts(&line) {
        if !is_line(&g) || geom::length(&g) < 0.05 {
            continue;
        }
        let cs = geom::coords(&geom::simplify(&g, 1e-3));
        for w in cs.windows(2) {
            if dist(w[0], w[1]) >= 0.05 {
                // (each from its lesser end: pieces that follow one another then meet end to start)
                out.push(if pt_less(w[1], w[0]) { (w[1], w[0]) } else { (w[0], w[1]) });
            }
        }
    }
    out.sort_by(seg_cmp);
    // pieces that follow one another on one line are one wall
    let mut merged: Vec<Seg> = vec![];
    for (p, q) in out {
        if let Some(m) = merged.last_mut() {
            if dist(m.1, p) < 1e-3 && ((m.1[0] - m.0[0]) * (q[1] - p[1]) - (m.1[1] - m.0[1]) * (q[0] - p[0])).abs() < 1e-3 {
                m.1 = q;
                continue;
            }
        }
        merged.push((p, q));
    }
    merged
}

/// The parts of a shape too narrow to be (part of) a room: what a square of ``side`` cannot be pushed into.
fn slivers(g: &G, side: f64) -> Vec<G> {
    let r = side / 2.0 - 0.05;
    let opened = buffered(&buffered(&buffered(g, -r, JoinStyle::Mitre), r, JoinStyle::Mitre), 0.01, JoinStyle::Mitre);
    solid(&geom::diff_exact(g, &opened)).into_iter().filter(|s| geom::area(s) > 0.3).collect()
}

fn sliver_area(g: &G, side: f64) -> f64 {
    py_sum(slivers(g, side).iter().map(geom::area))
}

// --- the house in its entry frame -------------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq)]
enum Hole {
    Window,
    Door,
    GarageDoor, // shut: nothing walks through it
}

#[derive(Clone)]
struct Opening {
    id: String,
    kind: Hole,
    seg: [Pt; 2],
    posts: Vec<Pt>,
}

#[derive(Clone)]
struct House {
    inner: PolyC, // the floor to plan: what the garages leave of it
    garages: Vec<PolyC>,
    ops: Vec<Opening>,
    entry: Option<Pt>,
    seed: u32,
    style: Style,
}

/// A house's geometry on one thread.
struct Maker<'a> {
    house: &'a House,
    f: G,
    f_area: f64,
    edge: G,
    outline: G,
    segs: Vec<G>,
    posts: Vec<Vec<G>>,
    next_id: u32,
    walls: RefCell<HashMap<(u32, u32), Rc<Vec<Seg>>>>,
}

#[derive(Clone)]
struct Link {
    a: usize,
    b: usize,
    kind: &'static str,
    p: Pt,
    q: Pt,
}

impl<'a> Maker<'a> {
    fn new(house: &'a House) -> Maker<'a> {
        let f = geom::from_polyc(&house.inner);
        let outline = geom::boundary(&f);
        Maker {
            house,
            f_area: geom::area(&f),
            edge: buffered(&outline, 1e-4, JoinStyle::Round),
            outline,
            f,
            segs: house.ops.iter().map(|o| geom::line(&o.seg)).collect(),
            posts: house.ops.iter().map(|o| o.posts.iter().map(|p| geom::point(p[0], p[1])).collect()).collect(),
            next_id: 0,
            walls: RefCell::new(HashMap::new()),
        }
    }

    fn cell(&mut self, g: G) -> Rc<Cell> {
        self.next_id += 1;
        Rc::new(Cell { id: self.next_id, area: geom::area(&g), bounds: geom::bounds(&g), g })
    }

    fn shared(&self, a: &Cell, b: &Cell) -> Rc<Vec<Seg>> {
        if let Some(s) = self.walls.borrow().get(&(a.id, b.id)) {
            return s.clone();
        }
        let (x, y) = (a.bounds, b.bounds);
        let apart = x[0] > y[2] + 1e-3 || y[0] > x[2] + 1e-3 || x[1] > y[3] + 1e-3 || y[1] > x[3] + 1e-3;
        let s = Rc::new(if apart { vec![] } else { shared_walls(&a.g, &b.g) });
        self.walls.borrow_mut().insert((a.id, b.id), s.clone());
        s
    }

    fn shared_len(&self, a: &Cell, b: &Cell) -> f64 {
        self.shared(a, b).iter().map(|s| dist(s.0, s.1)).fold(0.0, f64::max)
    }

    /// Does no end of these partition lines land on a window or a door (or within CLEAR_M of its side)? ``loose``: a
    /// partition may also meet a window at one of its posts, and come nearer its side.
    fn clear(&self, cut: &G, loose: bool) -> bool {
        let ends = geom::diff_exact(cut, &self.edge);
        for g in parts(&ends) {
            if !is_line(&g) || geom::length(&g) < 0.05 {
                continue;
            }
            let cs = geom::coords(&g);
            for end in [cs[0], cs[cs.len() - 1]] {
                let pt = geom::point(end[0], end[1]);
                if geom::distance(&self.outline, &pt) > 0.02 {
                    continue; // it ends on another partition
                }
                for (seg, posts) in self.segs.iter().zip(&self.posts) {
                    if geom::distance(seg, &pt) < if loose { CLEAR_LOOSE_M } else { CLEAR_M }
                        && !(loose && posts.iter().any(|post| geom::distance(post, &pt) < POST_M))
                    {
                        return false;
                    }
                }
            }
        }
        true
    }

    /// Does a partition of this outline end in a door to the outside?
    fn cuts_a_door(&self, cut: &G) -> bool {
        let ends = geom::diff_exact(cut, &self.edge);
        parts(&ends).into_iter().filter(|g| is_line(g) && geom::length(g) >= 0.05).any(|g| {
            let cs = geom::coords(&g);
            [cs[0], cs[cs.len() - 1]].iter().any(|end| {
                let pt = geom::point(end[0], end[1]);
                geom::distance(&self.outline, &pt) <= 0.02
                    && self.house.ops.iter().zip(&self.segs).any(|(o, s)| o.kind == Hole::Door && geom::distance(s, &pt) < CLEAR_LOOSE_M)
            })
        })
    }

    /// How far the room goes from ``c`` in direction ``d``.
    fn run(&self, c: Pt, d: Pt) -> f64 {
        let ray = geom::inter(&geom::line(&[[c[0] + d[0] * 0.02, c[1] + d[1] * 0.02], [c[0] + d[0] * 500.0, c[1] + d[1] * 500.0]]), &self.f);
        let at = geom::point(c[0], c[1]);
        match first_min(parts(&ray), |g| geom::distance(&at, g)) {
            Some(g) if !geom::is_empty(&g) => geom::length(&g),
            _ => 0.0,
        }
    }

    fn runs(&self, e: Pt) -> (f64, f64, f64) {
        (self.run(e, [1.0, 0.0]), self.run([e[0] + 0.3, e[1]], [0.0, -1.0]), self.run([e[0] + 0.3, e[1]], [0.0, 1.0]))
    }

    /// Can the hall be a corridor along the front door's wall? A closed house too shallow for a spine, with rooms of a
    /// fair depth left behind the corridor.
    fn side_fits(&self) -> bool {
        let Some(e) = self.house.entry else { return false };
        if self.house.style != Style::Closed {
            return false;
        }
        let (reach, left, right) = self.runs(e);
        reach < 0.75 * (left + right) && reach < SPINE_MIN_DEPTH_M && reach - HALL_W_M.1 >= SIDE_ROOM_DEPTH_M
    }

    /// The hall of one kind: ``stem``, ``side`` (a corridor along the door's wall), ``vestibule`` (an entry only), or ``none``.
    fn hall(&self, kind: &str, rng: &mut PyRandom) -> Option<G> {
        let side = kind == "side";
        let style = self.house.style;
        let e = self.house.entry?;
        if kind == "none" || (style == Style::Open && self.f_area < NO_HALL_BELOW_M2) {
            return None;
        }
        let [cx, cy] = e;
        let (reach, left, right) = self.runs(e);
        let along = left + right;
        let door = geom::point(cx, cy);
        let door_w = first_min(&self.segs, |s| geom::distance(s, &door)).map(geom::length).unwrap_or(DOOR_W_M);
        let w = rng.uniform(HALL_W_M.0, HALL_W_M.1).max(door_w + 2.0 * CLEAR_M + 0.1); // (its sides clear of the door's own)
        let end_door = reach >= 0.75 * along; // the door is in an end wall: the hall runs down the house
        let (base_stem, spine) = if side {
            (w, true) // the spine alone, against the door's wall
        } else if style == Style::Open || kind == "vestibule" {
            ((reach * 0.2).max(1.8).min(3.2), false)
        } else if end_door {
            (reach * if style == Style::Closed { rng.uniform(0.6, 0.85) } else { rng.uniform(0.4, 0.6) }, false)
        } else if reach >= SPINE_MIN_DEPTH_M {
            (reach / 2.0 + w / 2.0, true)
        } else {
            ((reach * 0.5).min(2.4), false)
        };
        // each end of the spine: at the end wall, or short of it by a room that then takes the whole depth of the house
        let mut ends: Vec<Vec<f64>> = vec![];
        for run in [left, right] {
            let first = rng.uniform(2.6, 3.4);
            let second = if style == Style::Mixed { rng.uniform(1.0, 3.0) } else { 0.0 };
            let short = run - first - second;
            let mut opts = if short >= 2.0 { vec![run + 0.5, short] } else { vec![run + 0.5] };
            if !(rng.random() < if style == Style::Closed { 0.5 } else { 0.15 }) {
                opts.reverse();
            }
            ends.push(opts);
        }
        const NUDGES: [f64; 9] = [0.0, 0.15, -0.15, 0.3, -0.3, 0.45, -0.45, 0.6, -0.6];
        let mut combos: Vec<[f64; 4]> = vec![];
        if spine {
            let pushes: &[f64] = if side { &[0.0] } else { &NUDGES };
            for &l in &ends[0] {
                for &r in &ends[1] {
                    for &a in &NUDGES {
                        for &b in pushes {
                            combos.push([a, b, l, r]);
                        }
                    }
                }
            }
        } else {
            combos.extend(NUDGES.iter().map(|&a| [a, 0.0, 0.0, 0.0]));
        }
        let key = |t: &[f64; 4]| {
            let at = |i: usize, v: f64| ends[i].iter().position(|&x| x == v).unwrap_or(0) as f64;
            t[0].abs() + t[1].abs() + 2.0 * if spine { at(0, t[2]) + at(1, t[3]) } else { 0.0 }
        };
        combos.sort_by(|a, b| cmpf(key(a), key(b)));
        let mut first_try: Option<G> = None;
        for [shift, push, end_l, end_r] in combos {
            let stem = base_stem + push; // (moves the spine across the house)
            let (mut y0, mut y1) = (cy - w / 2.0 + shift, cy + w / 2.0 + shift);
            if left - (cy - y0) < SLIVER_M {
                y0 = cy - left - 0.5;
            }
            if right - (y1 - cy) < SLIVER_M {
                y1 = cy + right + 0.5;
            }
            if !spine && y1.min(cy + right) - y0.max(cy - left) > HALL_MOST_W_M {
                continue;
            }
            let mut boxes = vec![rect(cx - 3.0, y0, cx + stem, y1)]; // (from behind the door: its wall may stand at a slant in the frame)
            if spine {
                boxes.push(rect(cx + stem - w, cy - end_l, cx + stem, cy + end_r));
            }
            let strip = geom::inter(&geom::union_py(boxes), &self.f);
            let Some(mut h) = first_min(solid(&strip), |g| geom::distance(g, &door)) else { continue };
            if geom::area(&h) < 2.0 {
                continue;
            }
            let rest = geom::diff_exact(&self.f, &h);
            let mut crumbs: Vec<G> = solid(&rest).into_iter().filter(|r| geom::area(r) < MIN_PIECE_M2).collect();
            crumbs.extend(slivers(&rest, 1.8)); // too small or too narrow for a room: hall
            if !crumbs.is_empty() {
                crumbs.insert(0, geom::dup(&h));
                let grown = buffered(&buffered(&geom::union_py(crumbs), 0.01, JoinStyle::Mitre), -0.01, JoinStyle::Mitre); // (closes the seams)
                match first_min(solid(&geom::inter(&grown, &self.f)), |g| geom::distance(g, &door)) {
                    Some(g) => h = g,
                    None => continue,
                }
            }
            let area = geom::area(&h);
            if area > 0.6 * self.f_area || (!spine && area > HALL_MOST_W_M * (stem + 0.5)) {
                continue; // (a hall that is most of the house, or a whole wing of it, is no hall)
            }
            if self.clear(&geom::boundary(&h), false) {
                return Some(h);
            }
            first_try.get_or_insert(h);
        }
        first_try
    }

    /// A corridor into ``region``, from ``contact``: a strip along one long side of the region, the side with fewer windows,
    /// stopping a room short of the far end. None when the region is too shallow to keep rooms beside the strip.
    fn corridor(&self, region: &G, contact: Seg, rng: &mut PyRandom) -> Option<G> {
        let [x0, y0, x1, y1] = geom::bounds(region);
        let axis = if x1 - x0 >= y1 - y0 { 0 } else { 1 }; // the corridor runs along the region
        let (lo, hi) = if axis == 0 { (x0, x1) } else { (y0, y1) };
        let (c0, c1) = if axis == 0 { (y0, y1) } else { (x0, x1) };
        let w = rng.uniform(HALL_W_M.0, HALL_W_M.1);
        if c1 - c0 - w < CORRIDOR_ROOM_DEPTH_M || hi - lo < 5.0 {
            return None;
        }
        let (p, q) = contact;
        let mid = [(p[0] + q[0]) / 2.0, (p[1] + q[1]) / 2.0];
        let near_lo = (mid[axis] - lo).abs() <= (mid[axis] - hi).abs(); // the end the reached room is at
        let stop = rng.uniform(2.6, 3.4);
        let (a0, a1) = if near_lo { (lo - 0.5, hi - stop) } else { (lo + stop, hi + 0.5) };
        let reached = geom::line(&[p, q]);
        let mut found: Vec<((bool, usize, f64), G)> = vec![];
        for low_side in [true, false] {
            for shift in [0.0, 0.15, 0.3, 0.45, 0.6] {
                // (wider when its partition would end in a window)
                let (b0, b1) = if low_side { (c0 - 0.5, c0 + w + shift) } else { (c1 - w - shift, c1 + 0.5) };
                let strip = if axis == 0 { rect(a0, b0, a1, b1) } else { rect(b0, a0, b1, a1) };
                let Some(got) = first_max(solid(&geom::inter(&strip, region)), geom::area) else { continue };
                if geom::area(&got) < 3.0 || geom::distance(&got, &reached) > 0.05 {
                    continue;
                }
                let edge = geom::boundary(&got);
                let windows = self.house.ops.iter().zip(&self.segs).filter(|(o, s)| o.kind == Hole::Window && near_middle(&edge, s)).count();
                found.push(((!self.clear(&edge, false), windows, shift), got));
                break;
            }
        }
        first_min(found, |t| t.0).map(|t| t.1)
    }

    /// ``piece`` cut into the rooms of ``wants`` (in order from the front door), each about its wanted share.
    fn fill(&mut self, piece: Rc<Cell>, wants: &[Want], hall: Option<&Cell>, out: &mut Vec<(Rc<Cell>, Kind)>) {
        let wants = scaled(wants, piece.area);
        if wants.len() == 1 {
            out.push((piece, wants[0].kind));
            return;
        }
        let total = py_sum(wants.iter().map(|w| w.area));
        let k = first_min(1..wants.len(), |&k| (py_sum(wants[..k].iter().map(|w| w.area)) / total - 0.5).abs()).unwrap_or(1);
        let groups = [&wants[..k], &wants[k..]];
        let share = py_sum(groups[0].iter().map(|w| w.area)) / total;
        let door = match self.house.entry {
            Some(e) => geom::point(e[0], e[1]),
            None => {
                let c = geom::centroid(&piece.g);
                geom::point(c[0], c[1])
            }
        };
        let shape = poly_of(&piece.g);
        let [x0, y0, x1, y1] = piece.bounds;
        let clean = clean_outline(&shape);
        let mut whole_slivers: Vec<(f64, f64)> = vec![]; // (side, area) of the piece's own
        let mut best: Option<(f64, [(G, usize); 2])> = None;
        for (loose, axis) in [(false, 0), (false, 1), (true, 0), (true, 1)] {
            if loose && best.is_some() {
                break;
            }
            let (lo, hi) = if axis == 0 { (x0, x1) } else { (y0, y1) };
            let halves = |c: f64| {
                if axis == 0 {
                    [rect(x0 - 1.0, y0 - 1.0, c, y1 + 1.0), rect(c, y0 - 1.0, x1 + 1.0, y1 + 1.0)]
                } else {
                    [rect(x0 - 1.0, y0 - 1.0, x1 + 1.0, c), rect(x0 - 1.0, c, x1 + 1.0, y1 + 1.0)]
                }
            };
            let n = (((hi - lo) / 0.1) as usize).max(1);
            let below: Vec<(f64, f64)> = (0..=n)
                .map(|i| {
                    let c = lo + (hi - lo) * i as f64 / n as f64;
                    (c, area_below(&shape, axis, c) / piece.area)
                })
                .collect();
            let mut corners: Vec<f64> = shape.ext.iter().map(|p| py_round(p[axis], 6)).collect();
            corners.sort_by(|a, b| cmpf(*a, *b));
            corners.dedup();
            for first_low in [true, false] {
                let frac = if first_low { share } else { 1.0 - share };
                let target = first_min(&below, |t| (t.1 - frac).abs()).map(|t| t.0).unwrap_or(lo);
                let reach = if loose { hi - lo } else { (0.3 * (hi - lo)).max(1.0) };
                let mut tried: Vec<f64> = vec![];
                let mut order: Vec<f64> = below.iter().map(|t| t.0).collect();
                order.sort_by(|a, b| cmpf((a - target).abs(), (b - target).abs()));
                for mut c in order {
                    if (c - target).abs() > reach {
                        break;
                    }
                    let near = first_min(corners.iter().copied(), |v| (v - c).abs()).unwrap_or(c);
                    if (near - c).abs() < SNAP_M && lo < near && near < hi {
                        c = near;
                    }
                    if tried.contains(&c) {
                        continue;
                    }
                    tried.push(c);
                    // most cuts are out by their rooms' size alone: seen from the outline, without cutting anything
                    let under = area_below(&shape, axis, c);
                    let hopeless = [true, false].into_iter().any(|low| {
                        let group = groups[usize::from(low != first_low)];
                        // (extents from a line a little inside the cut and one a little past it: the room's lie between)
                        let step = if low { 1e-4 } else { -1e-4 };
                        let extent = |at: f64| {
                            if !clean {
                                return None;
                            }
                            let b = side_bounds(&shape.ext, axis, at, low);
                            Some(((b[2] - b[0]).max(0.0), (b[3] - b[1]).max(0.0)))
                        };
                        let area = if low { under } else { piece.area - under };
                        // (without them: extents that fail nothing, and the area alone speaks)
                        let any = (0.0, 0.0);
                        let endless = (f64::MAX, f64::MAX);
                        misfit(area, extent(c - step).unwrap_or(any), extent(c + step).unwrap_or(endless), group, loose, 1e-3)
                    });
                    if hopeless {
                        continue;
                    }
                    let [h0, h1] = halves(c);
                    let mut kids = solid(&geom::inter(&piece.g, &h0));
                    if kids.len() > 1 {
                        continue;
                    }
                    kids.extend(solid(&geom::inter(&piece.g, &h1)));
                    if kids.len() != 2 {
                        continue;
                    }
                    let second = kids.pop().unwrap();
                    let first = kids.pop().unwrap();
                    let (low, high) = if geom::bounds(&first)[axis] <= geom::bounds(&second)[axis] { (first, second) } else { (second, first) };
                    let pair = if first_low { [(low, 0usize), (high, 1usize)] } else { [(high, 0usize), (low, 1usize)] };
                    // the cheap tests first; the narrow arms, the dearest, last
                    let sized = pair.iter().all(|(kid, g)| {
                        let b = geom::bounds(kid);
                        let wh = (b[2] - b[0], b[3] - b[1]);
                        !misfit(geom::area(kid), wh, wh, groups[*g], loose, 0.0)
                    });
                    if !sized {
                        continue;
                    }
                    let across = if axis == 0 { geom::line(&[[c, y0 - 1.0], [c, y1 + 1.0]]) } else { geom::line(&[[x0 - 1.0, c], [x1 + 1.0, c]]) };
                    if !self.clear(&geom::inter(&across, &piece.g), loose) {
                        continue;
                    }
                    let armless = pair.iter().all(|(kid, g)| {
                        let side = groups[*g].iter().map(|w| w.kind.min_side()).fold(f64::MIN, f64::max).min(1.8);
                        if plain_rect(kid, side) {
                            return true;
                        }
                        let arms = sliver_area(kid, side);
                        if arms <= 0.2 {
                            return true; // (no more than the piece's own, whatever that is)
                        }
                        let own = match whole_slivers.iter().find(|s| s.0 == side) {
                            Some(s) => s.1,
                            None => {
                                let a = sliver_area(&piece.g, side);
                                whole_slivers.push((side, a));
                                a
                            }
                        };
                        !(arms > own + 0.2) // the cut would leave a room with a narrow arm
                    });
                    if !armless {
                        continue;
                    }
                    let touch = match hall {
                        Some(h) => pair
                            .iter()
                            .filter(|(kid, _)| shared_walls(kid, &h.g).iter().map(|s| dist(s.0, s.1)).fold(0.0, f64::max) >= DOOR_W_M + 0.3)
                            .count(),
                        None => 0,
                    };
                    let slim = py_sum(pair.iter().filter(|(_, g)| groups[*g].len() == 1).map(|(kid, _)| {
                        let b = geom::bounds(kid);
                        let (kw, kh) = (b[2] - b[0], b[3] - b[1]);
                        kw.max(kh) / kw.min(kh)
                    }));
                    let order_pts = if geom::distance(&pair[0].0, &door) <= geom::distance(&pair[1].0, &door) { 2.0 } else { 0.0 };
                    let score = (touch * 10) as f64 + order_pts - slim - (c - target).abs() * 2.0;
                    if best.as_ref().map_or(true, |b| score > b.0) {
                        best = Some((score, pair));
                    }
                    break;
                }
            }
        }
        let Some((_, pair)) = best else {
            // no cut fits: one room less (a small one first, never the living room), the others share its floor
            let drop = first_min(0..wants.len(), |&i| (matches!(wants[i].kind, Living | LivingKitchen), !wants[i].kind.small(), wants[i].area)).unwrap_or(0);
            let mut rest = wants.clone();
            rest.remove(drop);
            return self.fill(piece, &rest, hall, out);
        };
        for (kid, g) in pair {
            let cell = self.cell(oriented(&kid));
            self.fill(cell, groups[g], hall, out);
        }
    }

    /// One way into every cell, as ``Kind::hosts`` says; returns (links, the cells nothing reaches). ``by_the_book``: no way in
    /// through a room it does not name (a bedroom behind a bedroom), so such rooms count as not reached.
    fn links(&self, cells: &[Rc<Cell>], types: &mut [Kind], root: usize, rng: &mut PyRandom, by_the_book: bool) -> (Vec<Link>, Vec<usize>) {
        let n = cells.len();
        let style = self.house.style;
        // (every pair, lower index first)
        let mut shared: Vec<Option<Rc<Vec<Seg>>>> = vec![None; n * n];
        for i in 0..n {
            for j in i + 1..n {
                shared[i * n + j] = Some(self.shared(&cells[i], &cells[j]));
            }
        }
        let longest = |i: usize, j: usize| -> Option<Seg> {
            let segs = shared[i.min(j) * n + i.max(j)].as_ref()?;
            first_max(segs.iter().copied(), |s| dist(s.0, s.1))
        };
        let mut links: Vec<Link> = vec![];
        let join = |i: usize, j: usize, types: &[Kind], links: &mut Vec<Link>, rng: &mut PyRandom| -> bool {
            let Some((p, q)) = longest(i, j) else { return false };
            let length = dist(p, q);
            if length < DOOR_W_M + 0.3 {
                return false;
            }
            let (a, b) = (types[i], types[j]);
            let has = |k: Kind| a == k || b == k;
            let kind = if a.living_space() && b.living_space() && length >= 2.2 {
                // the rooms one lives in open into each other
                if style == Style::Open {
                    "open"
                } else if rng.random() < if style == Style::Mixed { 0.85 } else { 0.35 } {
                    "arch"
                } else {
                    "door"
                }
            } else if has(Hall) && (has(Living) || has(LivingKitchen)) && length >= 2.2 {
                let odds = match style {
                    Style::Closed => 0.15,
                    Style::Mixed => 0.6,
                    Style::Open => 0.9,
                };
                if rng.random() < odds {
                    "arch"
                } else {
                    "door"
                }
            } else {
                "door"
            };
            let (s0, s1) = match kind {
                "open" => (0.0, length),
                "arch" => {
                    let w = (length - 0.6).min(rng.uniform(1.5, 2.6));
                    let s0 = (length - w) / 2.0;
                    (s0, s0 + w)
                }
                _ => {
                    let s0 = if rng.random() < 0.5 { 0.2 } else { length - DOOR_W_M - 0.2 };
                    (s0, s0 + DOOR_W_M)
                }
            };
            let (ux, uy) = ((q[0] - p[0]) / length, (q[1] - p[1]) / length);
            links.push(Link { a: i, b: j, kind, p: [p[0] + ux * s0, p[1] + uy * s0], q: [p[0] + ux * s1, p[1] + uy * s1] });
            true
        };

        let mut entered: Vec<Option<usize>> = vec![None; n]; // room -> the room it is entered from
        let mut reached = vec![false; n];
        reached[root] = true;
        let mut grew = true;
        while grew {
            grew = false;
            let rounds: &[bool] = if by_the_book { &[true] } else { &[true, false] }; // first by the book; then any room that is reached, rather than none
            for &strict in rounds {
                let mut todo: Vec<usize> = (0..n).filter(|&j| !reached[j]).collect();
                todo.sort_by(|&a, &b| (!types[a].living_space()).cmp(&!types[b].living_space()).then(cmpf(-cells[a].area, -cells[b].area)));
                for j in todo {
                    let wanted = types[j].hosts();
                    // (a bedroom may lie behind one other room, itself entered from the hall or a room one lives in, never deeper)
                    let mut hosts: Vec<usize> = (0..n)
                        .filter(|&i| reached[i])
                        .filter(|&i| {
                            if strict {
                                wanted.contains(&types[i])
                            } else {
                                !types[i].small() && types[i] != Garage && !(types[i].private() && types[j].private() && types[entered[i].unwrap_or(root)].private())
                            }
                        })
                        .collect();
                    let key = |i: usize| {
                        (wanted.iter().position(|&k| k == types[i]).unwrap_or(99), -longest(i, j).map(|s| dist(s.0, s.1)).unwrap_or(0.0))
                    };
                    hosts.sort_by(|&a, &b| {
                        let (x, y) = (key(a), key(b));
                        x.0.cmp(&y.0).then(cmpf(x.1, y.1))
                    });
                    let via = hosts.into_iter().find(|&i| join(i, j, types, &mut links, rng));
                    if let Some(via) = via {
                        entered[j] = Some(via);
                        reached[j] = true;
                        grew = true;
                        if !strict && types[via] == Bedroom && types[j].private() && types.iter().filter(|&&t| t == Bedroom).count() > 1 {
                            types[via] = Study; // a room one walks through to another is no bedroom
                        }
                    }
                }
                if grew {
                    break;
                }
            }
        }
        if style == Style::Closed {
            // a kitchen entered from the living room often has its own door to the hall as well
            for j in 0..n {
                if types[j] == Kitchen
                    && types[root] == Hall
                    && !links.iter().any(|l| (l.a == root && l.b == j) || (l.a == j && l.b == root))
                    && rng.random() < 0.5
                {
                    join(root, j, types, &mut links, rng);
                }
            }
        }
        let lost = (0..n).filter(|&j| !reached[j]).collect();
        (links, lost)
    }

    /// What is wrong with a plan, in points: floor wasted on halls, rooms a house cannot do without, a front door into the
    /// wrong room, rooms too large or too thin, rooms without daylight, rooms walked through.
    fn faults(&self, cells: &[Rc<Cell>], types: &[Kind], links: &[Link], has_hall: bool) -> f64 {
        let floor = py_sum(cells.iter().zip(types).filter(|(_, &t)| t != Garage).map(|(c, _)| c.area));
        let windows: Vec<G> = self
            .house
            .ops
            .iter()
            .zip(&self.segs)
            .filter(|(o, _)| o.kind == Hole::Window)
            .map(|(_, s)| {
                let m = geom::interp_norm(s, 0.5);
                geom::point(m[0], m[1])
            })
            .collect();
        let halls: Vec<usize> = (0..types.len()).filter(|&i| types[i] == Hall).collect();
        let mut score = 15.0 * f64::max(0.0, py_sum(halls.iter().map(|&i| cells[i].area)) / floor - HALL_SHARE);
        for &i in &halls {
            let doors = links.iter().filter(|l| l.a == i || l.b == i).count();
            score += 0.15 * f64::max(0.0, cells[i].area - HALL_FREE_M2 - HALL_PER_DOOR_M2 * doors as f64);
        }
        let any = |ks: &[Kind]| types.iter().any(|t| ks.contains(t));
        score += if any(&[Living, LivingKitchen]) { 0.0 } else { 5.0 };
        score += if any(&[Kitchen, LivingKitchen]) { 0.0 } else { 4.0 };
        score += if floor >= 34.0 && !any(&[Bathroom]) { 4.0 } else { 0.0 };
        if let Some(e) = self.house.entry {
            let at = geom::point(e[0], e[1]);
            let first = first_min(0..cells.len(), |&i| geom::distance(&cells[i].g, &at)).unwrap_or(0);
            score += if matches!(types[first], Hall | Living | LivingKitchen) { 0.0 } else { 4.0 };
            score += if self.house.style == Style::Closed && !has_hall && floor >= 45.0 { 1.0 } else { 0.0 };
        }
        for (cell, &kind) in cells.iter().zip(types) {
            if kind == Hall || kind == Garage {
                continue;
            }
            score += 0.2 * f64::max(0.0, cell.area - kind.most_area());
            let c = geom::min_rotated_rect(&cell.g);
            if c.len() >= 3 {
                let (a, b) = (dist(c[0], c[1]), dist(c[1], c[2]));
                let (short, long) = (a.min(b), a.max(b));
                score += 0.6 * f64::max(0.0, long / short.max(0.1) - 2.2);
            }
            if !kind.small() {
                let edge = geom::boundary(&cell.g);
                if !windows.iter().any(|w| geom::distance(&edge, w) < 0.05) {
                    score += match kind {
                        Bedroom => 1.5,
                        Living | LivingKitchen => 2.0,
                        _ => 0.6,
                    };
                }
            }
        }
        for l in links {
            // (links lead away from the root: l.a is the room walked through)
            if types[l.a].private() && types[l.b] != Bathroom {
                score += 1.0;
            }
        }
        score
    }
}

/// Is the middle of this opening on the edge?
fn near_middle(edge: &G, seg: &G) -> bool {
    let m = geom::interp_norm(seg, 0.5);
    geom::distance(edge, &geom::point(m[0], m[1])) < 0.05
}

// --- one candidate, and the best of them ------------------------------------------------------------------------

/// A finished candidate, as plain data (GEOS geometries stay on their thread).
struct Candidate {
    score: f64,
    cells: Vec<PolyC>,
    types: Vec<Kind>,
    links: Vec<Link>,
}

/// The rooms each piece gets: the largest rooms into the pieces with the most floor left. `in_order`: each piece keeps
/// the order of `wants` (the programme's), else the largest comes first.
fn deal(areas: &[f64], wants: &[Want], in_order: bool) -> Vec<Vec<Want>> {
    let mut shares: Vec<Vec<usize>> = vec![vec![]; areas.len()];
    let mut left = areas.to_vec();
    let mut order: Vec<usize> = (0..wants.len()).collect();
    order.sort_by(|&a, &b| cmpf(-wants[a].area, -wants[b].area));
    for w in order {
        let k = first_max(0..areas.len(), |&k| left[k]).unwrap_or(0);
        shares[k].push(w);
        left[k] -= wants[w].area;
    }
    shares
        .into_iter()
        .map(|mut s| {
            if in_order {
                s.sort_unstable();
            }
            s.into_iter().map(|w| wants[w]).collect()
        })
        .collect()
}

fn root_of(house: &House, cells: &[Rc<Cell>], types: &[Kind], has_hall: bool) -> usize {
    if has_hall {
        return 0;
    }
    let living: Vec<usize> = (0..types.len()).filter(|&i| matches!(types[i], Living | LivingKitchen)).collect();
    let from = if living.is_empty() { (0..cells.len()).collect() } else { living };
    match house.entry {
        Some(e) => {
            let at = geom::point(e[0], e[1]);
            first_min(from, |&i| geom::distance(&cells[i].g, &at)).unwrap_or(0)
        }
        None => from[0],
    }
}

fn build(house: &House, variant: &str, attempt: usize) -> Option<Candidate> {
    let mut rng = PyRandom::from_text(&format!("{}:{variant}:{attempt}", house.seed));
    let mut mk = Maker::new(house);
    let hall: Option<Rc<Cell>> = mk.hall(variant, &mut rng).map(|h| mk.cell(h));
    let mut pieces: Vec<G> = match &hall {
        Some(h) => solid(&geom::diff_exact(&mk.f, &h.g)),
        None => vec![geom::dup(&mk.f)],
    };
    pieces.sort_by(|a, b| cmpf(-geom::area(a), -geom::area(b)));
    if pieces.is_empty() {
        return None;
    }
    let areas: Vec<f64> = pieces.iter().map(geom::area).collect();
    let wants = programme(py_sum(areas.iter().copied()), house.style, &mut rng);
    let mut found: Vec<(Rc<Cell>, Kind)> = vec![];
    if let Some(h) = &hall {
        found.push((mk.cell(oriented(&h.g)), Hall));
    }
    for ((piece, area), mut given) in pieces.iter().zip(&areas).zip(deal(&areas, &wants, true)) {
        if given.is_empty() {
            // more pieces than rooms: what its size suits
            given = vec![want(if *area < 7.0 { Storage } else if *area < 10.0 { Study } else { Bedroom }, *area)];
        }
        let cell = mk.cell(oriented(piece));
        mk.fill(cell, &given, hall.as_deref(), &mut found);
    }
    let (mut cells, mut types): (Vec<Rc<Cell>>, Vec<Kind>) = found.into_iter().unzip();
    let has_hall = hall.is_some();

    // rooms nothing reaches (a row of bedrooms behind one another): a corridor into their floor, and that floor cut again
    let root = root_of(house, &cells, &types, has_hall);
    let (_, lost) = mk.links(&cells, &mut types, root, &mut PyRandom::new(0), true);
    if !lost.is_empty() {
        let n = cells.len();
        let corridor_from = |k: Kind| Hall.hosts().contains(&k);
        let mut taken = vec![false; n];
        for &j in &lost {
            taken[j] = true;
        }
        let hosts: Vec<usize> = (0..n).filter(|&i| corridor_from(types[i])).collect();
        for _ in 0..3 {
            // the room between them and the rooms one lives in is theirs: the corridor goes through it
            let count = taken.iter().filter(|&&t| t).count();
            if count >= 2 && (0..n).any(|j| taken[j] && hosts.iter().any(|&i| !taken[i] && mk.shared_len(&cells[j], &cells[i]) >= DOOR_W_M + 0.3)) {
                break;
            }
            let more: Vec<usize> = (0..n)
                .filter(|&i| !taken[i] && !corridor_from(types[i]) && (0..n).any(|j| taken[j] && mk.shared_len(&cells[i], &cells[j]) > 0.5))
                .collect();
            for i in more {
                taken[i] = true;
            }
        }
        let union = geom::union_py((0..n).filter(|&j| taken[j]).map(|j| geom::dup(&cells[j].g)).collect());
        if let Some(region) = first_max(solid(&union), geom::area) {
            let around = buffered(&region, 0.05, JoinStyle::Round);
            let inside: Vec<usize> = (0..n)
                .filter(|&j| {
                    let p = geom::rep_point(&cells[j].g);
                    taken[j] && geom::point(p[0], p[1]).within(&around).unwrap_or(false)
                })
                .collect();
            let oriented_region = mk.cell(oriented(&region));
            let region_cell = mk.cell(geom::dup(&region));
            let mut contacts: Vec<(f64, Seg)> = vec![];
            for &i in hosts.iter().filter(|&&i| !taken[i]) {
                for s in mk.shared(&region_cell, &cells[i]).iter() {
                    contacts.push((dist(s.0, s.1), *s));
                }
            }
            let longest = first_max(contacts, |c| (c.0, c.1 .0[0], c.1 .0[1], c.1 .1[0], c.1 .1[1]));
            let strip = match longest {
                Some((length, seg)) if length >= DOOR_W_M + 0.3 => mk.corridor(&oriented_region.g, seg, &mut rng),
                _ => None,
            };
            if let Some(strip) = strip.filter(|_| inside.len() >= 2) {
                let redo: Vec<Want> = inside
                    .iter()
                    .map(|&j| want(types[j], if types[j].small() { cells[j].area.min(SMALL_MOST_M2) } else { cells[j].area }))
                    .collect();
                for &j in inside.iter().rev() {
                    cells.remove(j);
                    types.remove(j);
                }
                let strip_cell = mk.cell(oriented(&strip));
                cells.push(strip_cell);
                types.push(Hall);
                let mut left_over = solid(&geom::diff_exact(&region, &strip));
                left_over.sort_by(|a, b| cmpf(-geom::area(a), -geom::area(b)));
                let areas: Vec<f64> = left_over.iter().map(geom::area).collect();
                if !areas.is_empty() {
                    let along = mk.cell(geom::dup(&strip));
                    for ((part, area), mut given) in left_over.iter().zip(&areas).zip(deal(&areas, &redo, false)) {
                        if given.is_empty() {
                            given = vec![want(Storage, *area)];
                        }
                        let cell = mk.cell(oriented(part));
                        let mut got = vec![];
                        mk.fill(cell, &given, Some(&along), &mut got);
                        for (c, k) in got {
                            cells.push(c);
                            types.push(k);
                        }
                    }
                }
            }
        }
    }
    for g in &house.garages {
        let cell = mk.cell(geom::from_polyc(g));
        cells.push(cell);
        types.push(Garage);
    }
    let mut links = vec![];
    for _ in 0..cells.len() {
        // a cell no doorway reaches still: it joins a neighbour (a room, not a closet), which keeps its use
        let root = root_of(house, &cells, &types, has_hall);
        let state = rng.clone();
        let (got, lost) = mk.links(&cells, &mut types, root, &mut rng, false);
        links = got;
        let Some(&j) = lost.iter().find(|&&j| types[j] != Garage) else { break };
        let together = |i: usize| py_sum(mk.shared(&cells[i], &cells[j]).iter().map(|s| dist(s.0, s.1)));
        let host = first_max((0..cells.len()).filter(|&i| i != j && !matches!(types[i], Hall | Garage)), |&i| {
            let t = together(i);
            (t > 0.5, !types[i].small(), types[i] == types[j], t)
        })?;
        let merged = geom::union_py(vec![geom::dup(&cells[host].g), geom::dup(&cells[j].g)]);
        if !matches!(merged.geometry_type(), Ok(GeometryTypes::Polygon)) {
            break;
        }
        if types[host].small() && !types[j].small() {
            types[host] = types[j]; // (a bedroom that swallows a closet is a bedroom)
        }
        cells[host] = mk.cell(oriented(&merged));
        cells.remove(j);
        types.remove(j);
        rng = state;
    }
    if !types.contains(&Bathroom) {
        // the merging can cost the house its bathroom: a closet or a study of the right size becomes it
        let spare = (0..types.len()).filter(|&i| matches!(types[i], Storage | Study) && 3.0 <= cells[i].area && cells[i].area <= 9.0);
        if let Some(i) = first_min(spare, |&i| (cells[i].area - 5.5).abs()) {
            types[i] = Bathroom;
        }
    }
    let faults = mk.faults(&cells, &types, &links, has_hall);
    let score = faults + rng.uniform(0.0, 0.6); // (some chance: houses are not all the one best shape)
    Some(Candidate { score, cells: cells.iter().map(|c| poly_of(&c.g)).collect(), types, links })
}

/// The candidate with the fewest faults: the kinds of hall in turn, each TRIES times, until one is good. The candidates of
/// one round are drawn side by side and judged in order, so the pick is that of drawing them one by one.
fn search(house: &House) -> Option<Candidate> {
    let mut kinds = vec!["stem", "vestibule", "none"];
    if Maker::new(house).side_fits() {
        kinds.push("side");
    }
    let mut best: Option<Candidate> = None;
    for attempt in 0..TRIES {
        let round: Vec<Option<Candidate>> = kinds.par_iter().map(|v| build(house, v, attempt)).collect();
        for got in round.into_iter().flatten() {
            if best.as_ref().map_or(true, |b| got.score < b.score) {
                best = Some(got);
            }
            if best.as_ref().is_some_and(|b| b.score < GOOD_FAULTS) {
                return best;
            }
        }
    }
    best
}

// --- the garages (ADR 0043) -------------------------------------------------------------------------------------

/// The garage behind one garage door: the floor to give it, or None when no shape of it will do. A rectangle running
/// square in from the door, grown over what would be left too narrow beside it. Best: a car fits, a house is left, no
/// partition ends at a window or door, little floor and few windows taken.
fn garage_behind(house: &House, floor: &G, door: &Opening) -> Option<(G, Option<G>)> {
    let mk = Maker::new(house);
    let [a, b] = door.seg;
    let (dx, dy) = (b[0] - a[0], b[1] - a[1]);
    // (the outline runs counter-clockwise: inward is to the left of the door)
    let (axis, inward) = if dy.abs() > dx.abs() { (0, if dy > 0.0 { -1.0 } else { 1.0 }) } else { (1, if dx > 0.0 { 1.0 } else { -1.0 }) };
    let across = 1 - axis;
    let wall = if inward > 0.0 { a[axis].min(b[axis]) } else { a[axis].max(b[axis]) };
    let (lo, hi) = (a[across].min(b[across]), a[across].max(b[across]));
    let middle = (lo + hi) / 2.0;
    let boxed = |from: f64, to: f64, left: f64, right: f64| {
        let (d0, d1) = (from.min(to), from.max(to));
        if axis == 0 {
            rect(d0, left, d1, right)
        } else {
            rect(left, d0, right, d1)
        }
    };
    let at = geom::point((a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0);
    let entry = house.entry.map(|e| geom::point(e[0], e[1]));
    let mut best: Option<((bool, bool, f64), (G, Option<G>))> = None;
    // the floor just inside the other doors, which a garage may be notched around
    let porches: Vec<G> = house
        .ops
        .iter()
        .zip(&mk.segs)
        .filter(|(o, _)| o.kind == Hole::Door)
        .map(|(_, s)| s.buffer_with_style(1.2, 16, CapStyle::Square, JoinStyle::Mitre, 5.0).unwrap_or_else(|_| geom::empty()))
        .collect();
    for (depth, notched) in GARAGE_D_M.into_iter().flat_map(|d| [(d, false), (d, true)]) {
        for width in GARAGE_W_M {
            let width = width.max(hi - lo + 0.1);
            for shift in [0.0, 0.25, -0.25, 0.5, -0.5, 0.9, -0.9] {
                let (left, right) = (middle - width / 2.0 + shift, middle + width / 2.0 + shift);
                if left > lo || right < hi {
                    continue; // the door must open into it
                }
                // (from behind the door's wall, which may stand at a slant in the frame)
                let mut strip = geom::inter(&boxed(wall - inward * 1.0, wall + inward * depth, left, right), floor);
                if notched {
                    let before = geom::area(&strip);
                    for porch in &porches {
                        strip = geom::diff_exact(&strip, porch);
                    }
                    if before - geom::area(&strip) < 0.01 {
                        continue; // (nothing to notch: drawn already)
                    }
                }
                let Some(mut g) = first_min(solid(&strip), |g| geom::distance(g, &at)) else { continue };
                let mut rest = geom::diff_exact(floor, &g);
                let mut crumbs: Vec<G> = solid(&rest).into_iter().filter(|r| geom::area(r) < MIN_PIECE_M2).collect();
                crumbs.extend(slivers(&rest, 1.8));
                if notched {
                    // (the notch itself is narrow, and stays the house's)
                    for porch in &porches {
                        crumbs = crumbs.iter().flat_map(|c| solid(&geom::diff_exact(c, porch))).collect();
                    }
                }
                if !crumbs.is_empty() {
                    crumbs.insert(0, geom::dup(&g));
                    let grown = buffered(&buffered(&geom::union_py(crumbs), 0.01, JoinStyle::Mitre), -0.01, JoinStyle::Mitre);
                    let Some(got) = first_min(solid(&geom::inter(&grown, floor)), |g| geom::distance(g, &at)) else { continue };
                    g = got;
                    rest = geom::diff_exact(floor, &g);
                }
                // no hair of floor left along a wall: it would count as the house reaching past the garage
                let trimmed = buffered(&buffered(&rest, -0.02, JoinStyle::Mitre), 0.02, JoinStyle::Mitre);
                let trimmed = geom::inter(&trimmed, &rest);
                rest = trimmed;
                let Some(got) = first_min(solid(&geom::diff_exact(floor, &rest)), |g| geom::distance(g, &at)) else { continue };
                g = got;
                let mut left_over = solid(&rest);
                if py_sum(left_over.iter().map(geom::area)) < MIN_HOUSE_M2 {
                    g = geom::dup(floor);
                    left_over.clear();
                }
                // what is left is one house, nowhere pinched to less than a doorway and a bit, and its front door opens into it
                if left_over.len() > 1
                    || left_over.first().is_some_and(|r| solid(&buffered(r, -0.55, JoinStyle::Mitre)).len() != 1)
                    || left_over.first().is_some_and(|r| entry.as_ref().is_some_and(|e| geom::distance(r, e) > 0.05))
                {
                    continue;
                }
                let edge = geom::boundary(&g);
                if !left_over.is_empty() && mk.cuts_a_door(&edge) {
                    continue;
                }
                let arms = left_over.first().map_or(0.0, |r| sliver_area(r, 1.8));
                let fits = [0.0, 0.3, -0.3, 0.6, -0.6].iter().any(|slide| {
                    let car = boxed(wall + inward * 0.1, wall + inward * (0.1 + CAR_M.1), middle + slide - CAR_M.0 / 2.0, middle + slide + CAR_M.0 / 2.0);
                    geom::area(&geom::inter(&car, &g)) >= 0.97 * CAR_M.0 * CAR_M.1
                });
                let windows = house.ops.iter().zip(&mk.segs).filter(|(o, s)| o.kind == Hole::Window && near_middle(&edge, s)).count();
                let clear = mk.clear(&edge, false);
                let cost = 0.15 * f64::max(0.0, geom::area(&g) - GARAGE_MOST_M2)
                    + windows as f64
                    + if clear { 0.0 } else if mk.clear(&edge, true) { 0.5 } else { 3.0 }
                    + shift.abs() * 0.1
                    + arms
                    + if notched { 1.0 } else { 0.0 };
                let key = (!fits, left_over.is_empty(), cost);
                if best.as_ref().map_or(true, |(k, _)| key < *k) {
                    best = Some((key, (g, left_over.into_iter().next())));
                }
            }
        }
    }
    best.map(|b| b.1)
}

/// The house with its garages taken off the floor, and whether any floor is left to plan.
fn with_garages(house: &House) -> (House, bool) {
    let mut floor = geom::from_polyc(&house.inner);
    let mut garages = vec![];
    for door in house.ops.iter().filter(|o| o.kind == Hole::GarageDoor) {
        let Some((g, left)) = garage_behind(house, &floor, door) else { continue };
        garages.push(poly_of(&oriented(&g)));
        match left {
            Some(rest) => floor = oriented(&rest),
            None => return (House { garages, ..house.clone() }, false),
        }
    }
    (House { inner: poly_of(&floor), garages, ..house.clone() }, true)
}

/// The plan of one house in its entry frame: rooms, partitions with their gaps, links.
fn plan(house: &House) -> Value {
    let (rest, planned) = with_garages(house);
    let found = if planned {
        search(&rest)
    } else {
        let n = rest.garages.len();
        Some(Candidate { score: 0.0, cells: rest.garages.clone(), types: vec![Garage; n], links: vec![] })
    };
    let Some(best) = found else { return Value::Null };
    let mk = Maker::new(house);
    let cells: Vec<G> = best.cells.iter().map(geom::from_polyc).collect();
    let mut walls = vec![];
    for i in 0..cells.len() {
        for j in i + 1..cells.len() {
            for (p, q) in shared_walls(&cells[i], &cells[j]) {
                let wall = geom::line(&[p, q]);
                let on = |at: Pt| geom::distance(&wall, &geom::point(at[0], at[1])) < 1e-3;
                let mut gaps: Vec<(f64, f64, &str)> = best
                    .links
                    .iter()
                    .filter(|l| ((l.a == i && l.b == j) || (l.a == j && l.b == i)) && on(l.p) && on(l.q))
                    .map(|l| (py_round(dist(p, l.p), 3), py_round(dist(p, l.q), 3), l.kind))
                    .collect();
                gaps.sort_by(|a, b| cmpf(a.0, b.0).then(cmpf(a.1, b.1)).then(a.2.cmp(b.2)));
                walls.push(json!([p, q, gaps]));
            }
        }
    }
    let rooms: Vec<Value> = best
        .cells
        .iter()
        .zip(&cells)
        .zip(&best.types)
        .map(|((ring, g), kind)| {
            let edge = geom::boundary(g);
            let near = |hole: Hole| -> Vec<&str> {
                mk.house.ops.iter().zip(&mk.segs).filter(|(o, s)| o.kind == hole && near_middle(&edge, s)).map(|(o, _)| o.id.as_str()).collect()
            };
            json!({"type": kind.name(), "exterior": ring.ext, "holes": ring.holes,
                   "windows": near(Hole::Window), "doors": near(Hole::Door), "garage_doors": near(Hole::GarageDoor)})
        })
        .collect();
    let links: Vec<Value> = best.links.iter().map(|l| json!([l.a, l.b, l.kind, l.p, l.q])).collect();
    json!({"rooms": rooms, "walls": walls, "links": links})
}

fn parse(v: &Value) -> Result<House, String> {
    let pt = |v: &Value| -> Result<Pt, String> {
        Ok([v[0].as_f64().ok_or("floor plan: not a point")?, v[1].as_f64().ok_or("floor plan: not a point")?])
    };
    let list = |v: &Value| v.as_array().cloned().unwrap_or_default();
    let ring = |v: &Value| list(v).iter().map(pt).collect::<Result<Vec<_>, _>>();
    let ops = list(&v["openings"])
        .iter()
        .map(|o| {
            Ok::<_, String>(Opening {
                id: o["id"].as_str().ok_or("floor plan: an opening without an id")?.to_string(),
                kind: match o["kind"].as_str() {
                    Some("window") => Hole::Window,
                    Some("garage_door") => Hole::GarageDoor,
                    _ => Hole::Door,
                },
                seg: [pt(&o["seg"][0])?, pt(&o["seg"][1])?],
                posts: ring(&o["posts"])?,
            })
        })
        .collect::<Result<_, _>>()?;
    Ok(House {
        garages: vec![],
        inner: PolyC { ext: ring(&v["exterior"])?, holes: list(&v["holes"]).iter().map(ring).collect::<Result<_, _>>()? },
        ops,
        entry: if v["entry"].is_null() { None } else { Some(pt(&v["entry"])?) },
        seed: v["seed"].as_u64().ok_or("floor plan: no seed")? as u32,
        style: match v["style"].as_str() {
            Some("closed") => Style::Closed,
            Some("mixed") => Style::Mixed,
            Some("open") => Style::Open,
            other => return Err(format!("floor plan: unknown style {other:?}")),
        },
    })
}

/// The plans of many houses, all cores: ``houses`` a JSON list, the answer a JSON list in the same order.
pub fn run(houses: &str) -> Result<String, String> {
    let list: Vec<Value> = serde_json::from_str(houses).map_err(|e| format!("floor plan input: {e}"))?;
    let houses: Vec<House> = list.iter().map(parse).collect::<Result<_, _>>()?;
    let plans: Vec<Value> = houses.par_iter().map(plan).collect();
    Ok(Value::Array(plans).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn wants(area: f64, style: Style, seed: u64) -> Vec<Want> {
        programme(area, style, &mut PyRandom::new(seed))
    }

    #[test]
    fn programme_fills_the_floor_with_one_living_space() {
        for style in [Style::Closed, Style::Mixed, Style::Open] {
            for area in [15.0, 28.0, 45.0, 70.0, 100.0, 150.0, 240.0] {
                let w = wants(area, style, 7);
                assert!((w.iter().map(|w| w.area).sum::<f64>() - area).abs() < 1e-6);
                assert!(w.iter().all(|w| w.kind != Hall));
                assert_eq!(w.iter().filter(|w| matches!(w.kind, Living | LivingKitchen)).count(), 1);
            }
        }
    }

    #[test]
    fn a_family_house_has_rooms_of_unequal_size() {
        let w = wants(100.0, Style::Closed, 3);
        let of = |k: Kind| w.iter().filter(|w| w.kind == k).map(|w| w.area).collect::<Vec<_>>();
        let beds = of(Bedroom);
        let largest = beds.iter().copied().fold(0.0, f64::max);
        assert_eq!((of(Living).len(), of(Kitchen).len(), beds.len()), (1, 1, 3));
        assert!(of(Living)[0] > largest && of(Bathroom).iter().all(|&a| a < 9.0) && of(Storage).iter().all(|&a| a < 8.0));
        assert!(w.last().is_some_and(|w| w.kind == Bedroom && w.area == largest)); // the largest bedroom is the last
    }

    #[test]
    fn small_and_large_houses() {
        let kinds = |a: f64| wants(a, Style::Closed, 1).iter().map(|w| w.kind).collect::<Vec<_>>();
        assert_eq!(kinds(15.0), [LivingKitchen]);
        assert_eq!(kinds(28.0), [LivingKitchen, Bathroom]);
        let large = wants(240.0, Style::Open, 5);
        assert!(large.len() >= 12 && large.iter().all(|w| w.area < 80.0));
    }

    #[test]
    fn scaled_keeps_small_rooms_small() {
        let w = scaled(&[want(Living, 20.0), want(Bathroom, 5.0)], 50.0);
        assert!((w[0].area + w[1].area - 50.0).abs() < 1e-9 && (w[1].area - 6.25).abs() < 1e-9);
    }

    #[test]
    fn half_plane_area_is_geos_area() {
        let p = PolyC { ext: vec![[0.0, 0.0], [10.0, 0.0], [10.0, 4.0], [4.0, 4.0], [4.0, 8.0], [0.0, 8.0], [0.0, 0.0]], holes: vec![] };
        assert!((area_below(&p, 0, 7.0) - 44.0).abs() < 1e-9);
        assert!((area_below(&p, 1, 6.0) - 48.0).abs() < 1e-9);
        assert_eq!(area_below(&p, 0, 0.0), 0.0);
    }
}

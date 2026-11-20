//! The crop recipe (crop.py, issue #328) in Rust: the ground patches, plants and artifacts of one crop patch.
//!
//! A line-by-line port of the Python twin, down to the generators: ``seeded(...)`` is :class:`PyRandom` (Mersenne Twister),
//! ``_gen(...)`` is :class:`NpGen` (numpy's PCG64), and every draw happens in the order of the Python code, so the same patch
//! gives the same instances, the same layers and (on the same GEOS) the same shapes. Keep the two in step: a change to one is a
//! change to the other (``tests/test_crop_rust.py`` compares them on a fixture village).

use crate::cfg::Cfg;
use crate::ctx::{hex_rgb, Ctx, Shared};
use crate::emit::Res;
use crate::geom::{self, PolyC, Pt, G};
use crate::index::{DistIdx, Region};
use crate::noise::value_noise;
use crate::patches::Patch;
use crate::planting::{floordiv, PyRandom};
use crate::pyrand::{arange, py_hypot, py_round, py_round0, NpGen};
use geos::{Geom, PreparedGeometry, SpatialIndex, STRtree};
use std::collections::HashSet;
use std::f64::consts::{PI, TAU};
use std::sync::Mutex;

const NOISE_SALT: i64 = 7100;
const TILE_M: f64 = 24.0;

type Rgb = [i64; 3];

/// One plant or artifact, as the Python recipe makes it (values rounded to 3 decimals).
#[derive(Clone, Debug)]
pub struct Inst {
    pub kind: String,
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub rot: f64,
    pub scale: f64,
}

/// The other ground near a crop patch: the crop patches that adjoin it (the baulk) and every ground unit within reach with its
/// terrain colour (what the edge blends into).
pub struct Neighbours {
    pub crop: Vec<(String, G)>,
    pub around: Vec<(G, Rgb, bool)>,
}

pub fn neighbours(sh: &Shared, polygon: &G, unit_id: &str) -> Neighbours {
    let pb = geom::bounds(polygon);
    let near = geom::buffer(polygon, 0.5, 16);
    let crop: Vec<(String, G)> = sh
        .crop_box
        .query(crate::util::grow(pb, 0.5))
        .into_iter()
        .map(|i| &sh.units[sh.crop_units[i]])
        .filter(|u| u.id != unit_id)
        .map(|u| (u.id.clone(), geom::multipolygon(&u.polys)))
        .filter(|(_, g)| geom::intersects(g, &near))
        .collect();
    let reach = geom::buffer(polygon, 16.0, 16);
    let around: Vec<(G, Rgb, bool)> = sh
        .around_box
        .query(crate::util::grow(pb, 16.0))
        .into_iter()
        .map(|i| &sh.units[sh.around_units[i]])
        .filter(|u| u.id != unit_id)
        .map(|u| (u, geom::multipolygon(&u.polys)))
        .filter(|(_, g)| geom::intersects(g, &reach))
        .map(|(u, g)| {
            let c = u.colour.map_or([128, 128, 128], |c| [c[0] as i64, c[1] as i64, c[2] as i64]);
            (g, c, u.recipe == "crop")
        })
        .collect();
    Neighbours { crop, around }
}

// --- small tools ---------------------------------------------------------------------------------------------------

static NAMES: Mutex<Option<HashSet<&'static str>>> = Mutex::new(None);

/// A layer or texture name as a ``&'static str`` (a few dozen distinct ones in a run).
pub fn intern(s: &str) -> &'static str {
    let mut g = NAMES.lock().unwrap();
    let set = g.get_or_insert_with(HashSet::new);
    if let Some(x) = set.get(s) {
        return x;
    }
    let leaked: &'static str = Box::leak(s.to_string().into_boxed_str());
    set.insert(leaked);
    leaked
}

fn rgb(hx: &str) -> Rgb {
    let c = hex_rgb(hx);
    [c[0] as i64, c[1] as i64, c[2] as i64]
}

fn rgb_cfg(c: Cfg) -> Rgb {
    rgb(c.s())
}

fn to_u8(c: Rgb) -> [u8; 3] {
    c.map(|v| v.clamp(0, 255) as u8)
}

fn mix(a: Rgb, b: Rgb, w: f64) -> Rgb {
    [0, 1, 2].map(|i| py_round0(a[i] as f64 * (1.0 - w) + b[i] as f64 * w))
}

fn smooth(x: f64) -> f64 {
    let x = x.clamp(0.0, 1.0);
    x * x * (3.0 - 2.0 * x)
}

fn union_of(parts: Vec<G>) -> G {
    geom::union_py(parts)
}

/// ``D._clean``: simplified, without slivers.
fn clean(g: &G, tol: f64) -> G {
    let mut keep = vec![];
    for p in geom::polys_g(g) {
        if geom::area(&p) < crate::patches::MIN_PIECE_M2 {
            continue;
        }
        let p = if tol > 0.0 { geom::simplify(&p, tol) } else { p };
        if !geom::is_empty(&p) && geom::area(&p) >= crate::patches::MIN_PIECE_M2 {
            keep.push(p);
        }
    }
    union_of(keep)
}

fn polys_by_area_first(g: &G) -> Option<G> {
    let mut best: Option<(f64, G)> = None;
    for p in geom::polys_g(g) {
        let a = geom::area(&p);
        if best.as_ref().is_none_or(|b| a > b.0) {
            best = Some((a, p));
        }
    }
    best.map(|b| b.1)
}

fn inset_buffer(g: &G, d: f64) -> G {
    geom::buffer(g, d, 16)
}

/// ``jitter_grid`` of detail.py: one point per cell of a grid in 24 m tiles, kept inside ``geom``.
fn jitter_grid(g: &mut NpGen, geom: &G, spacing: f64, jitter: f64) -> Vec<Pt> {
    if geom::is_empty(geom) || spacing <= 0.0 {
        return vec![];
    }
    let prep: Option<PreparedGeometry> = geom.to_prepared_geom().ok();
    let region = Region::from_geom(geom);
    let b = geom::bounds(geom);
    let per_tile = ((TILE_M / spacing) as usize).max(1);
    let tile = per_tile as f64 * spacing;
    let (nx, ny) = (((b[2] - b[0]) / tile).ceil() as i64, ((b[3] - b[1]) / tile).ceil() as i64);
    let mut out = vec![];
    for ti in 0..nx {
        for tj in 0..ny {
            let (x0, y0) = (b[0] + ti as f64 * tile, b[1] + tj as f64 * tile);
            let bx = geom::box_ccw(x0, y0, x0 + tile, y0 + tile);
            let hit = match &prep {
                Some(p) => p.intersects(&bx).unwrap_or(true),
                None => geom::intersects(geom, &bx),
            };
            if !hit {
                continue;
            }
            let n = per_tile * per_tile;
            let u: Vec<f64> = (0..2 * n).map(|_| g.random()).collect();
            for r in 0..per_tile {
                for c in 0..per_tile {
                    let k = r * per_tile + c;
                    let xs = x0 + (c as f64 + 0.5 + (u[k] - 0.5) * jitter) * spacing;
                    let ys = y0 + (r as f64 + 0.5 + (u[n + k] - 0.5) * jitter) * spacing;
                    if region.contains(xs, ys) {
                        out.push([xs, ys]);
                    }
                }
            }
        }
    }
    out
}

/// ``D.blob`` with a Python ``random.Random``.
fn blob(rng: &mut PyRandom, cx: f64, cy: f64, radius: f64, wobble: f64, stretch: f64, n: usize, axis: Option<f64>) -> G {
    let mut harm = [(0.0f64, 0.0f64, 0.0f64); 3];
    for (h, k) in harm.iter_mut().zip([2.0, 3.0, 5.0]) {
        *h = (k, rng.random() * TAU, 1.0 / k);
    }
    let norm = 0.0 + harm[0].2 + harm[1].2 + harm[2].2;
    let axis = axis.unwrap_or_else(|| rng.random() * PI);
    let (ca, sa) = (axis.cos(), axis.sin());
    let pts: Vec<Pt> = (0..n)
        .map(|i| {
            let t = TAU * i as f64 / n as f64;
            let s = 0.0 + harm[0].2 * (harm[0].0 * t + harm[0].1).sin() + harm[1].2 * (harm[1].0 * t + harm[1].1).sin() + harm[2].2 * (harm[2].0 * t + harm[2].1).sin();
            let r = radius * (1.0 + wobble * s / norm);
            let (x, y) = (r * t.cos() * stretch, r * t.sin());
            [cx + x * ca - y * sa, cy + x * sa + y * ca]
        })
        .collect();
    geom::polygon_fixed(&pts)
}

fn rad(rng: &mut PyRandom, span: [f64; 2]) -> f64 {
    span[0] + rng.random() * (span[1] - span[0])
}

fn vn(x: f64, y: f64, lattice: f64, salt: i64) -> f64 {
    value_noise(x, y, lattice, salt)
}

// --- geometry helpers ----------------------------------------------------------------------------------------------

/// The direction of the long side of the minimum rotated rectangle (radians) and its centre.
fn row_frame(poly: &G) -> (f64, Pt) {
    let ring = geom::min_rotated_rect(poly);
    let mrr = geom::polygon(&ring, &[]);
    let mut best: Option<(f64, Pt, Pt)> = None;
    for w in ring.windows(2) {
        let d = py_hypot(w[0][0] - w[1][0], w[0][1] - w[1][1]);
        if best.as_ref().is_none_or(|b| d > b.0) {
            best = Some((d, w[0], w[1]));
        }
    }
    let Some((_, a, b)) = best else { return (0.0, [0.0, 0.0]) };
    (f64::atan2(b[1] - a[1], b[0] - a[0]), geom::centroid(&mrr))
}

fn wobble(poly: &PolyC, amp: f64, lattice: f64, salt: i64, vertex_m: f64) -> G {
    let p = geom::orient(poly);
    let mut xy = geom::densify_pts(&p.ext, vertex_m);
    xy.pop();
    if xy.len() < 4 {
        return geom::from_polyc(&p);
    }
    let n = xy.len();
    let mut ext: Vec<Pt> = Vec::with_capacity(n);
    for i in 0..n {
        let (nx, pv) = (xy[(i + 1) % n], xy[(i + n - 1) % n]);
        let (tx, ty) = (nx[0] - pv[0], nx[1] - pv[1]);
        let ln = tx.hypot(ty).max(1e-9);
        let (nrx, nry) = (-ty / ln, tx / ln);
        let off = amp * (vn(xy[i][0], xy[i][1], lattice, salt) * 2.0 - 1.0);
        ext.push([xy[i][0] + nrx * off, xy[i][1] + nry * off]);
    }
    let out = geom::polygon_holes_fixed(&ext, &p.holes);
    if geom::is_empty(&out) {
        geom::from_polyc(&p)
    } else {
        out
    }
}

/// ``geom`` shrunk by ``d`` with a wandering edge.
fn inset(g: &G, d: f64, amp: f64, lattice: f64, salt: i64, vertex_m: f64) -> G {
    let inner = if d > 0.0 { inset_buffer(g, -d) } else { geom::dup(g) };
    if geom::is_empty(&inner) || amp <= 0.0 {
        return inner;
    }
    union_of(geom::polys(&inner).iter().map(|p| wobble(p, amp, lattice, salt, vertex_m)).collect())
}

// --- the plan of one patch ----------------------------------------------------------------------------------------

struct Plan {
    fallow: bool,
    stubble: bool,
    angle: f64,
    tone: [f64; 4],
    plant_mult: f64,
    tram: bool,
    tram_spacing: f64,
    tram_phase: f64,
    strips: usize,
    strip_w: f64,
    strip_seed: u32,
    stub_side: i32,
    stub_cover: f64,
    stub_seed: i64,
    round_bales: bool,
}

impl Plan {
    fn shade(&self, c: Rgb) -> Rgb {
        let [k, dr, dg, db] = self.tone;
        [(c[0], dr), (c[1], dg), (c[2], db)].map(|(v, d)| py_round0(v as f64 * (k + d)).clamp(0, 255))
    }
}

fn crop_plan(id: &str, polygon: &G, cc: Cfg, long_angle: f64) -> Plan {
    let mut rng = PyRandom::keyed(&format!("{id}:detail:crop:plan"));
    let (vc, tc, sc, fc) = (cc.at("variety"), cc.at("tramlines"), cc.at("stubble"), cc.at("fallow"));
    let area = geom::area(polygon);
    let fallow = rng.random() < fc.at("probability").f();
    let stub_roll = rng.random();
    let stubble = stub_roll < sc.at("probability").f() && area >= sc.at("min_area_m2").f() && !fallow;
    let ra = vc.at("row_angle");
    let (cross, free) = (ra.at("cross_share").f(), ra.at("free_share").f());
    let u = rng.random();
    let mut angle = long_angle + if u < cross { PI / 2.0 } else { 0.0 };
    let free_angle = rng.random() * PI;
    if cross <= u && u < cross + free {
        angle = free_angle;
    }
    angle += (rng.uniform(-1.0, 1.0) * ra.at("jitter_deg").f()).to_radians();
    let ts = vc.at("tone_shift").f();
    let tone = [1.0 + rng.uniform(-ts, ts), rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02)];
    let ps = vc.at("plant_scale").f2();
    let plant_mult = rng.uniform(ps[0], ps[1]);
    let sp = tc.at("spacing_m").f2();
    let spacing = rng.uniform(sp[0], sp[1]);
    let phase = rng.random();
    let es = tc.at("edge_strip");
    let edges = es.at("edges").f2();
    let n_strips = if rng.random() < es.at("probability").f() { rng.randint(edges[0] as i64, edges[1] as i64) as usize } else { 0 };
    let sw = es.at("width_m").f2();
    let strip_w = rng.uniform(sw[0], sw[1]);
    let strip_seed = rng.bits32();
    let side = if rng.random() < 0.5 { 1 } else { -1 };
    let cv = sc.at("coverage").f2();
    let cover = rng.uniform(cv[0], cv[1]);
    let stub_seed = rng.below(1000) as i64;
    let round_bales = rng.random() < sc.at("bales").at("round_share").f();
    let tram = tc.at("enabled").f() != 0.0 && area >= tc.at("min_area_m2").f() && !fallow;
    Plan {
        fallow, stubble, angle, tone, plant_mult, tram, tram_spacing: spacing, tram_phase: phase, strips: if tram { n_strips } else { 0 }, strip_w,
        strip_seed, stub_side: side, stub_cover: cover, stub_seed, round_bales,
    }
}

// --- baulk, headland, ground ---------------------------------------------------------------------------------------

/// ``(grass, dirt, any)``: this patch's half of the baulks along the edges it shares with other crop patches.
fn baulk(ctx: &Ctx, bc: Cfg, nb: &Neighbours) -> (G, G, bool) {
    let (mut grass, mut dirt): (Vec<G>, Vec<G>) = (vec![], vec![]);
    let w = bc.at("width_m").f2();
    let (lo, hi) = (w[0] / 2.0, w[1] / 2.0);
    let tol = bc.at("shared_tol_m").f();
    let boundary = geom::boundary(&ctx.polygon);
    for (nid, g) in &nb.crop {
        let line = geom::inter(&boundary, &geom::buffer(g, tol, 16));
        let lines = geom::lines_g(&line);
        if lines.iter().map(geom::length).sum::<f64>() < bc.at("min_shared_m").f() {
            continue;
        }
        let mut ab = [ctx.id.split('.').next().unwrap_or("").to_string(), nid.clone()];
        ab.sort();
        let is_dirt = PyRandom::keyed(&format!("{}:{}:detail:baulk", ab[0], ab[1])).random() < bc.at("dirt_share").f();
        let step = bc.at("step_m").f();
        let mut pts: Vec<Pt> = vec![];
        for ln in &lines {
            let n = ((geom::length(ln) / step) as i64 + 1).max(2);
            for i in 0..n {
                pts.push(geom::interp_norm(ln, i as f64 / (n - 1) as f64));
            }
        }
        let discs: Vec<G> = pts
            .iter()
            .map(|p| {
                let k = (0.65 * vn(p[0], p[1], 4.0, NOISE_SALT) + 0.35 * vn(p[0], p[1], 1.2, NOISE_SALT + 1)).clamp(0.0, 1.0);
                geom::buffer(&geom::point(p[0], p[1]), lo + (hi - lo) * k, 3)
            })
            .collect();
        let strip = geom::inter(&union_of(discs), &ctx.polygon);
        if is_dirt { dirt.push(strip) } else { grass.push(strip) }
    }
    let any = !grass.is_empty() || !dirt.is_empty();
    (union_of(grass), union_of(dirt), any)
}

fn headland(free: &G, hc: Cfg, near_nb: &Region, id: &str) -> G {
    let mut rng = PyRandom::keyed(&format!("{id}:detail:crop:headland"));
    let mut blobs = vec![];
    let (spacing, keep) = (hc.at("spacing_m").f(), hc.at("keep").f());
    for poly in geom::polys_g(free) {
        let ring = geom::exterior_line(&poly);
        let length = geom::length(&ring);
        if length < 1.0 {
            continue;
        }
        let steps = ((length / spacing) as i64).max(1);
        for i in 0..steps {
            if rng.random() > keep {
                continue;
            }
            let s = (i as f64 + rng.random()) / steps as f64 * length;
            let p = geom::interp(&ring, s);
            let q = geom::interp(&ring, length.min(s + 0.5));
            let ang = f64::atan2(q[1] - p[1], q[0] - p[0]);
            if !near_nb.is_empty() && near_nb.contains(p[0], p[1]) {
                continue;
            }
            let r = rad(&mut rng, hc.at("radius_m").f2());
            blobs.push(blob(&mut rng, p[0], p[1], r, 0.4, hc.at("stretch").f(), hc.at("vertices").i() as usize, Some(ang)));
        }
    }
    union_of(blobs)
}

// --- what lies beside the edge -------------------------------------------------------------------------------------

/// Points every ``step`` metres along every ring of ``free``, the outward normal there, and the ring each belongs to.
fn edge_samples(free: &G, step: f64) -> (Vec<Pt>, Vec<Pt>, Vec<usize>) {
    let (mut pts, mut nrm, mut rid) = (vec![], vec![], vec![]);
    let mut r = 0;
    for poly in geom::polys(free) {
        let p = geom::orient(&poly);
        for ring_pts in std::iter::once(&p.ext).chain(p.holes.iter()) {
            let ring = geom::line(ring_pts);
            let length = geom::length(&ring);
            if length < 1.0 {
                continue;
            }
            let n = ((length / step).round_ties_even() as i64).max(3);
            for i in 0..n {
                let s = length * i as f64 / n as f64;
                let a = geom::interp(&ring, (s - 0.5).rem_euclid(length));
                let b = geom::interp(&ring, (s + 0.5).rem_euclid(length));
                let pt = geom::interp(&ring, s);
                let (dx, dy) = (b[0] - a[0], b[1] - a[1]);
                let ln = match py_hypot(dx, dy) {
                    h if h == 0.0 => 1.0,
                    h => h,
                };
                pts.push(pt);
                nrm.push([dy / ln, -dx / ln]);
                rid.push(r);
            }
            r += 1;
        }
    }
    (pts, nrm, rid)
}

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Own,
    Road,
    Crop,
    Ground,
    Water,
    Nothing,
}

fn neighbour_colours(ctx: &Ctx, cfg: Cfg, pts: &[Pt], nrm: &[Pt], nb: &Neighbours) -> (Vec<[f64; 3]>, Vec<Kind>) {
    let nc = cfg.p("crop.edge.neighbour");
    let n = pts.len();
    let probe = nc.at("probe_m").f();
    let q: Vec<Pt> = (0..n).map(|i| [pts[i][0] + nrm[i][0] * probe, pts[i][1] + nrm[i][1] * probe]).collect();
    let sur = rgb(cfg.p("crop.surround").s());
    let mut col = vec![[sur[0] as f64, sur[1] as f64, sur[2] as f64]; n];
    let mut kind = vec![Kind::Nothing; n];
    let mut todo = vec![true; n];
    let mut take = |region: &Region, c: [f64; 3], k: Kind| {
        for i in 0..n {
            if todo[i] && region.contains(q[i][0], q[i][1]) {
                col[i] = c;
                kind[i] = k;
                todo[i] = false;
            }
        }
    };
    if let Some(road) = ctx.road.as_ref().filter(|r| !geom::is_empty(*r)) {
        let zone = geom::buffer(road, cfg.at("road_clearance_m").f() + probe + 0.5, 16);
        let c = rgb_cfg(nc.at("road"));
        take(&Region::from_geom(&zone), [c[0] as f64, c[1] as f64, c[2] as f64], Kind::Road);
    }
    for (g, c, is_crop) in &nb.around {
        take(&Region::from_geom(g), [c[0] as f64, c[1] as f64, c[2] as f64], if *is_crop { Kind::Crop } else { Kind::Ground });
    }
    if let Some(w) = ctx.water.as_ref().filter(|w| !geom::is_empty(*w)) {
        let c = rgb_cfg(nc.at("water"));
        take(&Region::from_geom(w), [c[0] as f64, c[1] as f64, c[2] as f64], Kind::Water);
    }
    take(&Region::from_geom(&ctx.polygon), [0.0; 3], Kind::Own);
    (col, kind)
}

struct Group {
    colour: Rgb,
    is_crop: bool,
    zone: G,
}

#[derive(Clone, PartialEq)]
enum Key {
    Own,
    Col(bool, [i64; 3]),
}

fn neighbour_groups(ctx: &Ctx, cfg: Cfg, free: &G, nb: &Neighbours) -> Vec<Group> {
    let nc = cfg.p("crop.edge.neighbour");
    let (pts, nrm, rid) = edge_samples(free, nc.at("sample_m").f());
    if pts.is_empty() {
        return vec![];
    }
    let n = pts.len();
    let (col, kind) = neighbour_colours(ctx, cfg, &pts, &nrm, nb);
    let k = nc.at("smooth").i();
    let mut sm = col.clone();
    let rings = rid.iter().copied().max().map_or(0, |m| m + 1);
    for r in 0..rings {
        let idx: Vec<usize> = (0..n).filter(|&i| rid[i] == r).collect();
        let len = idx.len() as i64;
        if len > 2 * k + 1 && k > 0 {
            for (j, &i) in idx.iter().enumerate() {
                let win: Vec<usize> = (-k..=k).map(|o| idx[(j as i64 + o).rem_euclid(len) as usize]).collect();
                let ok: Vec<usize> = win.iter().copied().filter(|&w| kind[w] != Kind::Own).collect();
                if !ok.is_empty() && kind[i] != Kind::Own {
                    let mut s = [0.0f64; 3];
                    for &w in &ok {
                        for c in 0..3 {
                            s[c] += col[w][c];
                        }
                    }
                    sm[i] = s.map(|v| v / ok.len() as f64);
                }
            }
        }
    }
    let step = nc.at("colour_step").f();
    let keys: Vec<Key> = (0..n)
        .map(|i| if kind[i] == Kind::Own { Key::Own } else { Key::Col(kind[i] == Kind::Crop, [0, 1, 2].map(|c| floordiv(sm[i][c], step))) })
        .collect();
    let extent = geom::envelope(&geom::buffer(free, f64::max(20.0, cfg.p("crop.edge.feather_m").f() * 2.0), 16));
    let key3 = |p: &Pt| ((p[0] * 1000.0).round_ties_even() / 1000.0 + 0.0, (p[1] * 1000.0).round_ties_even() / 1000.0 + 0.0);
    let uniq: HashSet<(u64, u64)> = pts.iter().map(|p| {
        let (a, b) = key3(p);
        (a.to_bits(), b.to_bits())
    }).collect();
    let cells: Option<Vec<G>> = if n >= 3 && uniq.len() == n {
        let vor = geom::voronoi(&pts, &extent);
        let mut cell: Vec<G> = (0..n).map(|_| geom::empty()).collect();
        // the first point (in the order shapely's STRtree reports them) the cell contains: one point per cell, but a degenerate cell
        // can hold several, and the Python twin takes the first hit
        let site: Vec<G> = pts.iter().map(|p| geom::point(p[0], p[1])).collect();
        let mut tree: STRtree<usize> = STRtree::with_capacity(10).expect("strtree");
        for (i, g) in site.iter().enumerate() {
            tree.insert(g, i);
        }
        for g in vor {
            let mut hits: Vec<usize> = vec![];
            tree.query(&g, |&i| hits.push(i));
            if let Some(&i) = hits.iter().find(|&&i| g.contains(&site[i]).unwrap_or(false)) {
                cell[i] = g;
            }
        }
        Some(cell)
    } else {
        None
    };
    let mut seen: Vec<Key> = vec![];
    for k in &keys {
        if !seen.contains(k) {
            seen.push(k.clone());
        }
    }
    let mut out = vec![];
    for key in seen {
        let Key::Col(is_crop, _) = key else { continue };
        let idx: Vec<usize> = (0..n).filter(|&i| keys[i] == key).collect();
        let members: Vec<G> = idx.iter().map(|&i| match &cells { Some(c) => geom::dup(&c[i]), None => geom::dup(&extent) }).collect();
        // a cell that is not valid on real data: repair each, then union (what the Python twin does when GEOS raises)
        let zone = match geom::union_try(&members) {
            Some(z) => z,
            None => union_of(members.iter().map(geom::repaired_buffer0).collect()),
        };
        let mut s = [0.0f64; 3];
        for &i in &idx {
            for c in 0..3 {
                s[c] += sm[i][c];
            }
        }
        let colour = s.map(|v| py_round0(v / idx.len() as f64));
        out.push(Group { colour, is_crop, zone });
    }
    out
}

struct LStyle {
    colour: Option<Rgb>,
    texture: Option<String>,
    tol: f64,
}

struct Layer {
    name: String,
    g: G,
    st: LStyle,
}

fn flat(name: &str, g: G, colour: Rgb) -> Layer {
    Layer { name: name.to_string(), g, st: LStyle { colour: Some(colour), texture: None, tol: 0.15 } }
}

fn edge_bands(free: &G, ec: Cfg, groups: &[Group], inner: &[(G, Rgb)], base: Rgb, reach_crop: f64) -> Vec<Layer> {
    let (feather, steps) = (ec.at("feather_m").f(), ec.at("steps").i());
    let (wob, lat, vm) = (ec.at("wobble_m").f(), ec.at("wobble_lattice_m").f(), ec.at("vertex_m").f());
    let mut out = vec![];
    let mut prev = geom::dup(free);
    for k in 1..=steps {
        let cur = inset(free, feather * k as f64 / steps as f64, wob, lat, NOISE_SALT + 20 + k, vm);
        let cur = geom::inter(&cur, &prev);
        let cur = geom::simplify(&cur, 0.15);
        let cur = geom::inter(&cur, &prev);
        let band = geom::diff_exact(&prev, &cur);
        prev = cur;
        if geom::is_empty(&band) {
            continue;
        }
        let depth = feather * (k as f64 - 0.5) / steps as f64;
        for gr in groups {
            let piece = geom::inter(&band, &gr.zone);
            if geom::is_empty(&piece) {
                continue;
            }
            let reach = if gr.is_crop { reach_crop } else { feather };
            let w = smooth(depth / reach.max(1e-6));
            let mut rest = geom::dup(&piece);
            let mut parts: Vec<(G, Rgb)> = vec![];
            for (reg, c) in inner {
                let hit = geom::inter(&piece, reg);
                if !geom::is_empty(&hit) {
                    parts.push((hit, *c));
                    rest = geom::diff_exact(&rest, reg);
                }
            }
            parts.push((rest, base));
            for (g, inn) in parts {
                let col = mix(gr.colour, inn, w);
                if geom::is_empty(&g) || w >= 0.97 || (0..3).map(|i| (col[i] - inn[i]).abs()).max().unwrap() <= 2 {
                    continue;
                }
                out.push(Layer { name: format!("crop_edge_{k}"), g, st: LStyle { colour: Some(col), texture: None, tol: 0.0 } });
            }
        }
    }
    out
}

// --- tramlines, stubble, spots -------------------------------------------------------------------------------------

fn tramlines(free: &G, plan: &Plan, tc: Cfg, origin: Pt) -> G {
    let local = geom::rotate(free, -plan.angle, origin);
    let b = geom::bounds(&local);
    let (w, gap, sp) = (tc.at("track_width_m").f(), tc.at("track_gap_m").f(), plan.tram_spacing);
    let mut strips = vec![];
    let mut c = b[1] + sp * (0.25 + 0.5 * plan.tram_phase);
    while c < b[3] {
        for s in [-1.0f64, 1.0] {
            let yc = c + s * gap / 2.0;
            strips.push(geom::box_ccw(b[0] - 1.0, yc - w / 2.0, b[2] + 1.0, yc + w / 2.0));
        }
        c += sp;
    }
    if strips.is_empty() {
        return geom::empty();
    }
    let inner = geom::buffer_mitre(free, -tc.at("margin_m").f());
    if geom::is_empty(&inner) {
        return geom::empty();
    }
    clean(&geom::inter(&geom::rotate(&union_of(strips), plan.angle, origin), &inner), 0.05)
}

fn edge_strips(free: &G, plan: &Plan, tc: Cfg) -> G {
    if plan.strips == 0 || geom::is_empty(free) {
        return geom::empty();
    }
    let mut rng = PyRandom::keyed(&format!("{}:crop:strips", plan.strip_seed));
    let Some(big) = polys_by_area_first(free) else { return geom::empty() };
    let big = geom::orient(&geom::polys(&big).remove(0));
    let es = tc.at("edge_strip");
    let min_edge = es.at("min_edge_m").f();
    let mut segs: Vec<[Pt; 2]> = big.ext.windows(2).filter(|w| py_hypot(w[0][0] - w[1][0], w[0][1] - w[1][1]) >= min_edge).map(|w| [w[0], w[1]]).collect();
    rng.shuffle(&mut segs);
    let mut strips = vec![];
    for (i, seg) in segs.iter().take(plan.strips).enumerate() {
        let buf = geom::buffer_single_sided(&geom::line(seg), plan.strip_w);
        for p in geom::polys(&buf) {
            strips.push(wobble(&p, es.at("wobble_m").f(), 2.0, NOISE_SALT + 40 + i as i64, 1.0));
        }
    }
    if strips.is_empty() {
        return geom::empty();
    }
    clean(&geom::inter(&union_of(strips), free), 0.1)
}

fn stubble(free: &G, plan: &Plan, sc: Cfg, origin: Pt) -> (G, f64, [f64; 4]) {
    let local = geom::rotate(free, -plan.angle, origin);
    let b = geom::bounds(&local);
    let xs = arange(b[0] - 2.0, b[2] + 2.01, 1.0);
    let wm = sc.at("wobble_m").f();
    let wob: Vec<f64> = xs.iter().map(|&x| wm * (vn(x, plan.stub_seed as f64, 6.0, NOISE_SALT + 50) * 2.0 - 1.0)).collect();
    let side = plan.stub_side;
    let local_area = geom::area(&local);
    let region = |yc: f64| -> G {
        let far = if side > 0 { b[3] + 5.0 } else { b[1] - 5.0 };
        let mut pts: Vec<Pt> = xs.iter().zip(&wob).map(|(&x, &w)| [x, yc + w]).collect();
        pts.push([*xs.last().unwrap(), far]);
        pts.push([xs[0], far]);
        geom::inter(&geom::polygon_fixed(&pts), &local)
    };
    let (mut lo, mut hi) = (b[1], b[3]);
    for _ in 0..16 {
        let mid = (lo + hi) / 2.0;
        let share = geom::area(&region(mid)) / local_area.max(1e-9);
        if (share > plan.stub_cover) == (side > 0) {
            lo = mid;
        } else {
            hi = mid;
        }
    }
    let yc = (lo + hi) / 2.0;
    (clean(&geom::inter(&geom::rotate(&region(yc), plan.angle, origin), free), 0.1), yc, b)
}

fn stubble_lanes(stub: &G, plan: &Plan, sc: Cfg, origin: Pt) -> G {
    let local = geom::rotate(stub, -plan.angle, origin);
    let b = geom::bounds(&local);
    let (sp, w) = (sc.at("row_spacing_m").f(), sc.at("row_width_m").f());
    let ys = arange(b[1] + sp / 2.0, b[3], sp);
    if ys.is_empty() {
        return geom::empty();
    }
    let lanes = union_of(ys.iter().map(|&y| geom::box_ccw(b[0] - 1.0, y - w / 2.0, b[2] + 1.0, y + w / 2.0)).collect());
    clean(&geom::inter(&geom::rotate(&geom::inter(&lanes, &local), plan.angle, origin), stub), 0.05)
}

fn emit_one(ctx: &Ctx, kind: &str, x: f64, y: f64, rot: f64, scale: f64) -> Option<Inst> {
    if !ctx.sh.terrain.ok(x, y) {
        return None;
    }
    let z = ctx.sh.terrain.surface(x, y);
    Some(Inst { kind: kind.to_string(), x: py_round(x, 3), y: py_round(y, 3), z: py_round(z, 3), rot: py_round(rot.rem_euclid(TAU), 3), scale: py_round(scale, 3) })
}

fn bales(ctx: &Ctx, stub: &G, plan: &Plan, sc: Cfg, yc: f64, bounds: [f64; 4], origin: Pt) -> Vec<Inst> {
    let bc = sc.at("bales");
    let kind = if plan.round_bales { "art_hay_bale_round" } else { "art_hay_bale_square" };
    let spec = if plan.round_bales { bc.at("round") } else { bc.at("square") };
    let mut rng = PyRandom::keyed(&format!("{}:detail:crop:bales", ctx.id));
    let safe = geom::buffer(stub, -spec.at("radius_m").f() - 0.3, 16);
    if geom::is_empty(&safe) {
        return vec![];
    }
    let region = Region::from_geom(&safe);
    let (x0, x1) = (bounds[0], bounds[2]);
    let sp = spec.at("spacing_m").f();
    let mut out: Vec<Inst> = vec![];
    let (c, s) = (plan.angle.cos(), plan.angle.sin());
    let lines = bc.at("lines").f2();
    let max = bc.at("max").i() as usize;
    let r = spec.at("radius_m").f();
    for j in 0..rng.randint(lines[0] as i64, lines[1] as i64) {
        let y = yc + plan.stub_side as f64 * (r + 1.2 + j as f64 * (sp + 1.0));
        let mut x = x0 + rng.random() * sp;
        while x < x1 {
            let lx = x + (rng.random() - 0.5) * 0.4 * sp;
            let ly = y + (rng.random() - 0.5) * 0.5;
            let wx = origin[0] + (lx - origin[0]) * c - (ly - origin[1]) * s;
            let wy = origin[1] + (lx - origin[0]) * s + (ly - origin[1]) * c;
            if region.contains(wx, wy) && out.len() < max {
                let rot = if plan.round_bales { rng.random() * TAU } else { plan.angle + (rng.random() - 0.5) * 0.2 };
                let scale = rad(&mut rng, spec.at("scale").f2());
                out.extend(emit_one(ctx, kind, wx, wy, rot, scale));
            }
            x += sp;
        }
    }
    out
}

fn spots(free: &G, area: f64, pid: &str, sc: Cfg) -> Vec<(&'static str, G)> {
    let mut out = vec![];
    if geom::is_empty(free) {
        return out;
    }
    let mut rng = PyRandom::keyed(&format!("{pid}:detail:crop:spots"));
    let mut g = NpGen::keyed(&format!("{pid}:detail:crop:spots"));
    let inner = {
        let i = geom::buffer(free, -1.0, 16);
        if geom::is_empty(&i) { geom::dup(free) } else { i }
    };
    let cand = jitter_grid(&mut g, &inner, 5.0, 0.9);
    if cand.is_empty() {
        return out;
    }
    for (i, name) in ["wet", "bare", "weeds"].into_iter().enumerate() {
        let spec = sc.at(name);
        let want = spec.at("per_ha").f() * area / 1e4;
        let n = want as i64 + if rng.random() < want - (want as i64) as f64 { 1 } else { 0 };
        if n <= 0 {
            continue;
        }
        let score: Vec<f64> = cand.iter().map(|p| vn(p[0], p[1], sc.at("lattice_m").f(), NOISE_SALT + 60 + i as i64)).collect();
        let score: Vec<f64> = score.into_iter().map(|v| v + 0.25 * g.random()).collect();
        let mut order: Vec<usize> = (0..cand.len()).collect();
        order.sort_by(|&a, &b| (-score[a]).partial_cmp(&(-score[b])).unwrap().then(a.cmp(&b)));
        let mut blobs = vec![];
        for &k in order.iter().take(n as usize) {
            let r = rad(&mut rng, spec.at("radius_m").f2());
            blobs.push(blob(&mut rng, cand[k][0], cand[k][1], r, 0.4, 1.5, 9, None));
        }
        let geo = clean(&geom::inter(&union_of(blobs), free), 0.1);
        if !geom::is_empty(&geo) {
            out.push((name, geo));
        }
    }
    out
}

fn flowers(free: &G, pid: &str, fc: Cfg) -> Vec<Layer> {
    let spec = fc.at("flowers");
    let per_ha = spec.at("per_ha").f();
    let inner = geom::buffer(free, -0.6, 16);
    if per_ha <= 0.0 || geom::is_empty(&inner) {
        return vec![];
    }
    let mut rng = PyRandom::keyed(&format!("{pid}:detail:crop:flowers"));
    let mut g = NpGen::keyed(&format!("{pid}:detail:crop:flowers"));
    let pts = jitter_grid(&mut g, &inner, (1e4 / per_ha / 2.5).sqrt(), 0.9);
    if pts.is_empty() {
        return vec![];
    }
    let nz: Vec<f64> = pts.iter().map(|p| vn(p[0], p[1], 7.0, NOISE_SALT + 70)).collect();
    let mut keep: Vec<Pt> = vec![];
    for (p, z) in pts.iter().zip(&nz) {
        if g.random() < (2.5 * z * z).clamp(0.0, 1.0) {
            keep.push(*p);
        }
    }
    let cols: Vec<Rgb> = spec.at("colours").arr().iter().map(|c| rgb_cfg(*c)).collect();
    let mut by: std::collections::BTreeMap<usize, Vec<G>> = Default::default();
    for p in keep {
        let k = rng.below(cols.len() as u32) as usize;
        let r = rad(&mut rng, spec.at("radius_m").f2());
        by.entry(k).or_default().push(blob(&mut rng, p[0], p[1], r, 0.3, 1.0, 7, None));
    }
    by.into_iter().map(|(i, b)| Layer { name: format!("crop_flower_{i}"), g: union_of(b), st: LStyle { colour: Some(cols[i]), texture: None, tol: 0.03 } }).collect()
}

fn tones(free: &G, pid: &str, area: f64, tone_per_ha: f64, tone_radius: [f64; 2], cols: &[Rgb]) -> Vec<Layer> {
    let mut out = vec![];
    let n = py_round0(tone_per_ha * area / 1e4);
    if n <= 0 || geom::is_empty(free) {
        return out;
    }
    for (i, col) in cols.iter().enumerate().skip(1) {
        let suffix = if i == 1 { String::new() } else { format!(":{i}") };
        let mut rng = PyRandom::keyed(&format!("{pid}:detail:crop:tones{suffix}"));
        let mut g = NpGen::keyed(&format!("{pid}:detail:crop:tonepts{suffix}"));
        let pts: Vec<Pt> = jitter_grid(&mut g, free, f64::max(2.0, (area / (n.max(1) as f64)).sqrt() * 0.9), 0.9).into_iter().take(n as usize).collect();
        let t = if pts.is_empty() {
            geom::empty()
        } else {
            let mut bs = vec![];
            for p in &pts {
                let r = rad(&mut rng, tone_radius);
                bs.push(blob(&mut rng, p[0], p[1], r, 0.4, 1.5, 8, None));
            }
            union_of(bs)
        };
        out.push(Layer { name: if i == 1 { "crop_tone".to_string() } else { format!("crop_tone_{i}") }, g: t, st: LStyle { colour: Some(*col), texture: None, tol: 0.15 } });
    }
    out
}

// --- plants --------------------------------------------------------------------------------------------------------

fn grid(g: &mut NpGen, geom: &G, step: f64, row: f64, angle: f64, origin: Pt) -> Vec<Pt> {
    if geom::is_empty(geom) {
        return vec![];
    }
    if row <= 0.0 {
        return jitter_grid(g, geom, step, 0.9);
    }
    let local = geom::rotate(geom, -angle, origin);
    let b = geom::bounds(&local);
    let xs = arange(b[0], b[2] + step, step);
    let ys = arange(b[1], b[3] + row, row);
    let n = xs.len() * ys.len();
    let mut gx: Vec<f64> = Vec::with_capacity(n);
    let mut gy: Vec<f64> = Vec::with_capacity(n);
    for _ in 0..ys.len() {
        for &x in &xs {
            gx.push(x);
        }
    }
    for &y in &ys {
        for _ in 0..xs.len() {
            gy.push(y);
        }
    }
    for v in gx.iter_mut() {
        *v += (g.random() - 0.5) * step * 0.3;
    }
    for v in gy.iter_mut() {
        *v += (g.random() - 0.5) * row * 0.08;
    }
    let region = Region::from_geom(&local);
    let (c, s) = (angle.cos(), angle.sin());
    let mut out = Vec::new();
    for k in 0..n {
        if region.contains(gx[k], gy[k]) {
            let (dx, dy) = (gx[k] - origin[0], gy[k] - origin[1]);
            out.push([origin[0] + dx * c - dy * s, origin[1] + dx * s + dy * c]);
        }
    }
    out
}

fn plant_density(d: f64, edge: Cfg) -> f64 {
    let (margin, fall) = (edge.at("plant_margin_m").f(), edge.at("plant_falloff_m").f());
    if fall <= 0.0 {
        return if d >= margin { 1.0 } else { 0.0 };
    }
    smooth((d - margin) / fall)
}

#[allow(clippy::too_many_arguments)]
fn plants(
    ctx: &Ctx, cc: Cfg, planted: &G, edge_line: &G, plan: &Plan, origin: Pt, keep_off: &[(f64, f64, f64)], bare: &G, weeds: &G, weed_scale: f64,
) -> Vec<Inst> {
    let mut out = vec![];
    let lines: Vec<Vec<Pt>> = geom::lines_g(edge_line).iter().map(geom::coords).collect();
    let edge_d = DistIdx::new(&lines, &[]);
    let edge = cc.at("edge");
    let (jit, jlat) = (edge.at("plant_jitter_m").f(), edge.at("plant_jitter_lattice_m").f());
    let bare_r = if geom::is_empty(bare) { None } else { Some(Region::from_geom(bare)) };
    let weeds_r = if geom::is_empty(weeds) { None } else { Some(Region::from_geom(weeds)) };
    for spec in cc.at("plants").arr() {
        let kind = spec.at("kind").s();
        let mut g = NpGen::keyed(&format!("{}:detail:crop:{kind}", ctx.id));
        let mut pts = grid(&mut g, planted, spec.at("spacing_m").f(), spec.at("row_m").fo(0.0), plan.angle, origin);
        if pts.is_empty() {
            continue;
        }
        let mut d: Vec<f64> = pts.iter().map(|p| edge_d.dist(p[0], p[1])).collect();
        if jit > 0.0 {
            for (di, p) in d.iter_mut().zip(&pts) {
                *di += jit * (vn(p[0], p[1], jlat, NOISE_SALT + 8) * 2.0 - 1.0);
            }
        }
        let density = spec.at("density").f();
        let amp = spec.at("noise_amp").fo(0.0);
        let mut p: Vec<f64> = d.iter().map(|&di| plant_density(di, edge) * density).collect();
        if amp > 0.0 {
            for (pi, pt) in p.iter_mut().zip(&pts) {
                *pi *= 1.0 - amp * (1.0 - vn(pt[0], pt[1], 5.0, NOISE_SALT + 7));
            }
        }
        if edge.at("mode").s() == "wall" {
            let margin = edge.at("plant_margin_m").f();
            for (pi, di) in p.iter_mut().zip(&d) {
                if !(di + (g.random() - 0.5) * 0.2 >= margin) {
                    *pi = 0.0;
                }
            }
        }
        let mut kept = vec![];
        for (pt, pi) in pts.iter().zip(&p) {
            if g.random() < *pi {
                kept.push(*pt);
            }
        }
        pts = kept;
        if let Some(r) = &bare_r {
            pts.retain(|p| !r.contains(p[0], p[1]));
        }
        for &(x, y, r) in keep_off {
            pts.retain(|p| (p[0] - x).hypot(p[1] - y) >= r);
        }
        let mult: Vec<f64> = pts.iter().map(|p| if weeds_r.as_ref().is_some_and(|w| w.contains(p[0], p[1])) { plan.plant_mult * weed_scale } else { plan.plant_mult }).collect();
        out.extend(emit_plants(&mut g, kind, &pts, spec.at("scale").f2(), ctx, &mult));
    }
    out
}

fn emit_plants(g: &mut NpGen, kind: &str, pts: &[Pt], span: [f64; 2], ctx: &Ctx, mult: &[f64]) -> Vec<Inst> {
    if pts.is_empty() {
        return vec![];
    }
    let rot: Vec<f64> = pts.iter().map(|_| g.random() * TAU).collect();
    let sc: Vec<f64> = pts.iter().zip(mult).map(|(_, m)| (span[0] + g.random() * (span[1] - span[0])) * m).collect();
    let mut out = vec![];
    for (k, p) in pts.iter().enumerate() {
        if !ctx.sh.terrain.ok(p[0], p[1]) {
            continue;
        }
        let z = ctx.sh.terrain.surface(p[0], p[1]);
        out.push(Inst { kind: kind.to_string(), x: py_round(p[0], 3), y: py_round(p[1], 3), z: py_round(z, 3), rot: py_round(rot[k], 3), scale: py_round(sc[k], 3) });
    }
    out
}

// --- artifacts -----------------------------------------------------------------------------------------------------

struct Art<'a> {
    ctx: &'a Ctx<'a>,
    rng: PyRandom,
    ring: G,
    safe: Region,
    cluster_m: f64,
    placed: Vec<(f64, f64, String, f64)>,
    out: Vec<Inst>,
    anchors_corner: Vec<Pt>,
    inner: G,
    deep: G,
}

impl Art<'_> {
    fn heading(&self, p: Pt) -> f64 {
        let s = geom::project(&self.ring, p);
        let a = geom::interp(&self.ring, (s - 0.4).max(0.0));
        let b = geom::interp(&self.ring, geom::length(&self.ring).min(s + 0.4));
        f64::atan2(b[1] - a[1], b[0] - a[0])
    }

    fn fits(&self, x: f64, y: f64, kind: &str, sep: f64) -> bool {
        if !self.safe.contains(x, y) {
            return false;
        }
        self.placed.iter().all(|(px, py, k, ps)| py_hypot(x - px, y - py) >= f64::max(sep, *ps) * if k != kind { 0.5 } else { 1.0 })
    }

    fn put(&mut self, item: Cfg, budget: i64) -> i64 {
        let place = item.at("place").s();
        let anchor: Pt = if place == "corner" {
            let k = self.rng.below(self.anchors_corner.len() as u32) as usize;
            self.anchors_corner[k]
        } else if place == "edge" {
            let s = self.rng.random() * geom::length(&self.ring);
            geom::nearest_on(geom::interp(&self.ring, s), &self.inner)
        } else {
            let mut g = NpGen::new(self.rng.bits32() as u64);
            let pts = jitter_grid(&mut g, &self.deep, 4.0, 0.9);
            if !pts.is_empty() {
                pts[self.rng.below(pts.len() as u32) as usize]
            } else {
                geom::rep_point(&self.inner)
            }
        };
        let grp = item.at("group").f2();
        let count = self.rng.randint(grp[0] as i64, grp[1] as i64).min(budget);
        let sep = item.at("sep_m").f();
        let kind = item.at("kind").s();
        let mut got = 0;
        if item.at("line").f() != 0.0 {
            let ang = self.heading(anchor);
            for i in 0..count {
                let (x, y) = (anchor[0] + ang.cos() * sep * i as f64, anchor[1] + ang.sin() * sep * i as f64);
                if self.fits(x, y, kind, sep) {
                    self.placed.push((x, y, kind.to_string(), sep));
                    let sc = rad(&mut self.rng, item.at("scale").f2());
                    self.out.extend(emit_one(self.ctx, kind, x, y, ang, sc));
                    got += 1;
                }
            }
            return got;
        }
        for _ in 0..count * 14 {
            if got >= count {
                break;
            }
            let a = self.rng.random() * TAU;
            let r = if place == "interior" { 0.0 } else { self.rng.random().sqrt() * self.cluster_m };
            let (x, y) = (anchor[0] + a.cos() * r, anchor[1] + a.sin() * r);
            if !self.fits(x, y, kind, sep) {
                continue;
            }
            let rot = if place == "interior" { self.rng.random() * TAU } else { self.heading([x, y]) + (self.rng.random() - 0.5) * 0.5 };
            self.placed.push((x, y, kind.to_string(), sep));
            let sc = rad(&mut self.rng, item.at("scale").f2());
            self.out.extend(emit_one(self.ctx, kind, x, y, rot, sc));
            got += 1;
        }
        got
    }
}

fn artifacts<'a>(ctx: &'a Ctx<'a>, ac: Cfg, free: &G, max_count: Option<i64>) -> Vec<Inst> {
    let items_all = ac.at("items").arr();
    let area = geom::area(free);
    if items_all.is_empty() || geom::is_empty(free) || area < ac.at("min_area_m2").f() {
        return vec![];
    }
    let mut rng = PyRandom::keyed(&format!("{}:detail:crop:art", ctx.id));
    let mut kinds: Vec<&str> = vec![];
    for i in &items_all {
        if !kinds.contains(&i.at("kind").s()) {
            kinds.push(i.at("kind").s());
        }
    }
    let kpp = ac.at("kinds_per_patch").f2();
    let want = (rng.randint(kpp[0] as i64, kpp[1] as i64) as usize).min(kinds.len());
    let chosen: Vec<&str> = rng.sample(kinds.len(), want).into_iter().map(|i| kinds[i]).collect();
    let items: Vec<Cfg> = items_all.into_iter().filter(|i| chosen.contains(&i.at("kind").s())).collect();
    let extras: Vec<Cfg> = ac.at("extras").arr().into_iter().filter(|e| rng.random() < e.at("probability").f()).collect();
    let mut n = (ac.at("min").i()).max((ac.at("max").i()).min(py_round0(ac.at("per_ha").f() * area / 1e4)));
    if let Some(m) = max_count {
        n = n.min(m);
    }
    let inner = {
        let i = geom::buffer(free, -ac.at("inset_m").f(), 16);
        if geom::is_empty(&i) { geom::dup(free) } else { i }
    };
    let ring_poly = polys_by_area_first(free).unwrap();
    let ring = geom::exterior_line(&ring_poly);
    let mrr = geom::min_rotated_rect(&ring_poly);
    let mut corners: Vec<Pt> = mrr.iter().take(4).map(|&c| geom::nearest_on(c, &inner)).collect();
    rng.shuffle(&mut corners);
    corners.truncate(2);
    let safe = {
        let s = geom::buffer(free, -0.4, 16);
        if geom::is_empty(&s) { geom::dup(free) } else { s }
    };
    let deep = {
        let d = geom::buffer(free, -3.0, 16);
        if geom::is_empty(&d) { geom::dup(&inner) } else { d }
    };
    let weights: Vec<f64> = items.iter().map(|i| i.at("weight").f()).collect();
    let mut st = Art { ctx, rng, ring, safe: Region::from_geom(&safe), cluster_m: ac.at("cluster_m").f(), placed: vec![], out: vec![], anchors_corner: corners, inner, deep };
    for e in &extras {
        st.put(*e, 99);
    }
    let base = st.out.len() as i64;
    for _ in 0..n * 6 {
        if st.out.len() as i64 - base >= n {
            break;
        }
        let k = st.rng.choice_weighted(&weights);
        let budget = n - (st.out.len() as i64 - base);
        st.put(items[k], budget);
    }
    st.out
}

/// ``D.free_area(ctx, cfg, trunks=False)`` with shapely's own unions and differences (the cached :meth:`Ctx::free_area` shortcuts
/// a union of one part, which GEOS would still re-node).
fn free_area(ctx: &Ctx, cfg: Cfg) -> G {
    let mut blocked: Vec<G> = vec![];
    let (wall, fence) = (cfg.at("wall_clearance_m").f(), cfg.at("fence_clearance_m").f());
    for b in &ctx.buildings {
        blocked.push(geom::buffer_mitre(b, wall));
    }
    for f in &ctx.fences {
        blocked.push(geom::buffer_flat_mitre(f, fence));
    }
    if let Some(road) = &ctx.road {
        blocked.push(geom::simplify(&geom::buffer_mitre(road, cfg.at("road_clearance_m").f()), 0.1));
    }
    if let Some(b) = ctx.blocked.as_ref().filter(|b| !geom::is_empty(*b)) {
        blocked.push(geom::dup(b));
    }
    if blocked.is_empty() {
        geom::dup(&ctx.polygon)
    } else {
        geom::diff_exact(&ctx.polygon, &union_of(blocked))
    }
}

// --- the recipe ----------------------------------------------------------------------------------------------------

/// Ground patches, plants and artifacts of one crop patch: the patches (in the order of the Python recipe's draping: flat ones,
/// then the textured by texture) and the instances.
pub fn crop_detail(ctx: &Ctx, cfg: Cfg, nb: &Neighbours) -> (Res, Vec<Inst>) {
    let (pid, cc) = (ctx.id.as_str(), cfg.at("crop"));
    let mut res = Res::default();
    let free = free_area(ctx, cfg);
    if geom::is_empty(&free) {
        return (res, vec![]);
    }
    let (ec, bc, sc, tc, spc) = (cc.at("edge"), cc.at("baulk"), cc.at("stubble"), cc.at("tramlines"), cc.at("spots"));
    let (long_angle, origin) = row_frame(&ctx.polygon);
    let plan = crop_plan(pid, &ctx.polygon, cc, long_angle);
    let angle = plan.angle;
    let ground = if plan.fallow { cc.at("fallow").at("colours") } else { cc.at("ground_colours") };
    let cols: Vec<Rgb> = ground.arr().iter().map(|c| plan.shade(rgb_cfg(*c))).collect();
    let texture: Option<String> = if plan.fallow { None } else { Some(cc.at("texture").s().to_string()).filter(|t| !t.is_empty()) };

    let nb_union = union_of(nb.crop.iter().map(|(_, g)| geom::dup(g)).collect());
    let near_nb = if geom::is_empty(&nb_union) { geom::empty() } else { geom::buffer(&nb_union, 1.5, 16) };
    let near_region = Region::from_geom(&near_nb);
    let (baulk_grass, baulk_dirt, has_baulk) = baulk(ctx, bc, nb);
    let baulk_g = if has_baulk { geom::inter(&union_of(vec![geom::dup(&baulk_grass), geom::dup(&baulk_dirt)]), &free) } else { geom::empty() };
    let head = if cc.at("headland").at("keep").f() > 0.0 { geom::diff_exact(&headland(&free, cc.at("headland"), &near_region, pid), &baulk_g) } else { geom::empty() };

    let mut tram = if plan.tram { tramlines(&free, &plan, tc, origin) } else { geom::empty() };
    let strip = edge_strips(&free, &plan, tc);
    let mut stub = geom::empty();
    let mut lanes = geom::empty();
    let mut bale_list: Vec<Inst> = vec![];
    if plan.stubble {
        let (s, yc, bounds) = stubble(&free, &plan, sc, origin);
        stub = s;
        if !geom::is_empty(&stub) {
            lanes = stubble_lanes(&stub, &plan, sc, origin);
            bale_list = bales(ctx, &stub, &plan, sc, yc, bounds, origin);
            tram = if !geom::is_empty(&tram) { geom::diff_exact(&tram, &stub) } else { tram };
        }
    }
    let spot = spots(&free, geom::area(&ctx.polygon), pid, spc);
    let spot_of = |name: &str| spot.iter().find(|(n, _)| *n == name).map(|(_, g)| geom::dup(g));
    let wet_col: Rgb = cols[0].map(|v| py_round0(v as f64 * spc.at("wet").at("darken").f()));
    let straw = plan.shade(rgb_cfg(sc.at("colours").arr()[0]));

    let mut batches: Vec<Vec<Layer>> = vec![];
    if !geom::is_empty(&baulk_grass) || !geom::is_empty(&baulk_dirt) {
        let bcol = bc.at("colours").arr();
        for (n, g, c) in [("crop_baulk", &baulk_grass, rgb_cfg(bcol[0])), ("crop_baulk_dirt", &baulk_dirt, rgb_cfg(bc.at("dirt_colour")))] {
            if !geom::is_empty(g) {
                batches.push(vec![Layer { name: n.to_string(), g: geom::dup(g), st: LStyle { colour: Some(c), texture: None, tol: 0.15 } }]);
            }
        }
    }
    let head_tex = || {
        let t = cc.at("headland").at("texture");
        if t.is_null() { "dirt".to_string() } else { t.s().to_string() }
    };
    if !geom::is_empty(&head) {
        batches.push(vec![Layer { name: "crop_headland".into(), g: geom::dup(&head), st: LStyle { colour: None, texture: Some(head_tex()), tol: 0.15 } }]);
    }
    if !geom::is_empty(&strip) {
        batches.push(vec![Layer { name: "crop_dirtstrip".into(), g: geom::dup(&strip), st: LStyle { colour: None, texture: Some(head_tex()), tol: 0.15 } }]);
    }
    if !geom::is_empty(&tram) {
        batches.push(vec![flat("crop_tramline", geom::dup(&tram), mix(cols[0], rgb_cfg(tc.at("colour")), tc.at("mix").f()))]);
    }
    let (feather, steps) = (ec.at("feather_m").f(), ec.at("steps").i());
    if feather > 0.0 && steps > 0 {
        let groups = neighbour_groups(ctx, cfg, &free, nb);
        let inner: Vec<(G, Rgb)> = if !geom::is_empty(&stub) { vec![(geom::dup(&stub), straw)] } else { vec![] };
        batches.push(edge_bands(&free, ec, &groups, &inner, cols[0], bc.at("blend_m").f()));
    }
    if !geom::is_empty(&lanes) {
        batches.push(vec![flat("crop_stubble_rows", geom::dup(&lanes), plan.shade(rgb_cfg(sc.at("colours").arr()[1])))]);
    }
    if !geom::is_empty(&stub) {
        batches.push(vec![flat("crop_stubble", geom::dup(&stub), straw)]);
    }
    for (name, layer, colour) in [("wet", "crop_wet", wet_col), ("bare", "crop_bare_spot", rgb_cfg(spc.at("bare").at("colour"))), ("weeds", "crop_weeds", rgb_cfg(spc.at("weeds").at("colour")))] {
        if let Some(g) = spot_of(name) {
            batches.push(vec![flat(layer, g, colour)]);
        }
    }
    if plan.fallow {
        for f in flowers(&free, pid, cc.at("fallow")) {
            batches.push(vec![f]);
        }
    }
    if texture.is_none() {
        let (ton, rd) = if !plan.fallow { (cc.at("tone_per_ha").f(), cc.at("tone_radius_m").f2()) } else { (cc.at("fallow").at("tone_per_ha").f(), cc.at("fallow").at("tone_radius_m").f2()) };
        for t in tones(&free, pid, geom::area(&ctx.polygon), ton, rd, &cols) {
            batches.push(vec![t]);
        }
    }

    let mut taken = geom::empty();
    let mut patches: Vec<Patch> = vec![];
    for batch in batches {
        let mut made: Vec<G> = vec![];
        for l in batch {
            let mut g = geom::inter(&clean(&geom::inter(&l.g, &free), l.st.tol), &free);
            if !geom::is_empty(&taken) {
                g = geom::diff_exact(&g, &taken);
            }
            let g = clean(&g, 0.0);
            if geom::is_empty(&g) {
                continue;
            }
            made.push(geom::dup(&g));
            patches.push(Patch {
                layer: intern(&l.name), geom: g, colour: l.st.colour.map(to_u8), texture: l.st.texture.as_deref().map(intern), angle: 0.0, origin: [0.0, 0.0], lift: None,
            });
        }
        if !made.is_empty() {
            let mut all = vec![geom::dup(&taken)];
            all.extend(made);
            taken = union_of(all);
        }
    }
    let core = if !geom::is_empty(&taken) {
        let d = geom::diff_exact(&free, &taken);
        clean(&geom::diff_exact(&geom::inter(&clean(&d, 0.1), &free), &taken), 0.0)
    } else {
        clean(&free, 0.1)
    };
    if !geom::is_empty(&core) {
        match &texture {
            Some(t) => patches.push(Patch { layer: "crop_rows", geom: core, colour: Some([255, 255, 255]), texture: Some(intern(t)), angle, origin, lift: None }),
            None => patches.push(Patch { layer: "crop_ground", geom: core, colour: Some(to_u8(cols[0])), texture: None, angle: 0.0, origin: [0.0, 0.0], lift: None }),
        }
    }
    let off = [
        if geom::is_empty(&baulk_g) { geom::empty() } else { geom::buffer(&baulk_g, 0.15, 16) },
        if geom::is_empty(&head) { geom::empty() } else { geom::buffer(&head, 0.1, 16) },
        if geom::is_empty(&tram) { geom::empty() } else { geom::buffer(&tram, 0.2, 16) },
        geom::dup(&strip),
        geom::dup(&stub),
    ];
    let planted = geom::diff_exact(&free, &union_of(off.into_iter().collect()));
    let mut edge_line = geom::boundary(&free);
    if !geom::is_empty(&near_nb) {
        edge_line = geom::diff_exact(&edge_line, &near_nb);
    }
    let fb = cc.at("fallow");
    let mut arts = artifacts(ctx, cc.at("artifacts"), &free, if plan.fallow { Some(fb.at("max_artifacts").i()) } else { None });
    arts.extend(bale_list);
    let mut radii: Vec<(String, f64)> = vec![];
    for i in cc.at("artifacts").at("items").arr().into_iter().chain(cc.at("artifacts").at("extras").arr()) {
        radii.push((i.at("kind").s().to_string(), i.at("radius_m").f()));
    }
    radii.push(("art_hay_bale_round".into(), sc.at("bales").at("round").at("radius_m").f()));
    radii.push(("art_hay_bale_square".into(), sc.at("bales").at("square").at("radius_m").f()));
    let radius_of = |k: &str| radii.iter().rev().find(|(n, _)| n == k).map_or(1.0, |r| r.1);
    let keep_off: Vec<(f64, f64, f64)> = arts.iter().map(|a| (a.x, a.y, radius_of(&a.kind))).collect();
    let bare = spot_of("bare").unwrap_or_else(geom::empty);
    let weeds = spot_of("weeds").unwrap_or_else(geom::empty);
    let mut crops = if plan.fallow {
        vec![]
    } else {
        plants(ctx, cc, &planted, &edge_line, &plan, origin, &keep_off, &bare, &weeds, spc.at("weeds").at("plant_scale").f())
    };
    crops.extend(arts);

    // the row order of the Python stage: flat patches in order, then the textured by texture
    let (flat_p, mut tex_p): (Vec<Patch>, Vec<Patch>) = patches.into_iter().partition(|p| p.texture.is_none());
    tex_p.sort_by(|a, b| a.texture.cmp(&b.texture));
    res.patches = flat_p.into_iter().chain(tex_p).collect();
    (res, crops)
}

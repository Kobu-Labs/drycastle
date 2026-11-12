//! The field and forest recipes, which also run on big areas. A big area is cut into tiles; a tile is computed with a halo and
//! clipped to its exact box, and everything random is a pure function of position (lattice cells with their own streams), so
//! neighbouring tiles agree along their seam and the result does not depend on how the area was cut.
//!
//! * **field** (meadow, orchard, allotments, fields): tone patches, dirt headlands along the edge, a strip of furrows in a share
//!   of the units, a few stored clutter kinds (the dense kinds come from the meadow raster).
//! * **forest**: leaf litter under the trees, forest-floor tone patches, bare spots.

use crate::cfg::Cfg;
use crate::ctx::{palette, Ctx, Near, Shared};
use crate::emit::{Emitter, Res, Tint};
use crate::geom::{self, PolyC, Pt, G};
use crate::index::Region;
use crate::meadow::{self, Cells};
use crate::patches::{assemble, Flat, Patch, Rect};
use crate::rng::{seed_of, Rng};
use crate::util::{self, blob, grow, lattice, point_rng, rad, Polyline};
use std::collections::BTreeMap;
use std::f64::consts::TAU;

/// One unit part to run (a context's polygon with what the stage decided about it).
pub struct Part {
    pub cid: String,
    pub kind: String,
    pub recipe: String,
    pub poly: PolyC,
    pub colour: Option<[u8; 3]>,
    pub cfg_kind: String,
}

pub struct Head {
    p: Pt,
    ang: f64,
    seed: u64,
}

pub struct Furrow {
    pub piece: Vec<PolyC>,
    pub rect: PolyC,
    pub angle: f64,
    pub centre: Pt,
}

/// Decisions of a whole unit that its tiles share: headland blobs along the edge, the furrow strip.
#[derive(Default)]
pub struct Pre {
    pub heads: Vec<Head>,
    pub furrow: Option<Furrow>,
}

const HEAD_REACH_M: f64 = 9.0;
const TONE_REACH_M: f64 = 20.0;

fn pid_seed(id: &str, purpose: &str) -> u64 {
    seed_of(&format!("{id}:detail:{purpose}"))
}

/// A strip of the unit's minimum rotated rectangle, turned along its long side.
fn furrow_rect(rng: &mut Rng, poly: &G, fu: Cfg) -> Option<(G, f64, Pt)> {
    let ring = geom::min_rotated_rect(poly);
    if ring.len() < 5 {
        return None;
    }
    let mut edges: Vec<(Pt, Pt)> = ring.windows(2).map(|w| (w[0], w[1])).collect();
    let len = |e: &(Pt, Pt)| (e.1[0] - e.0[0]).hypot(e.1[1] - e.0[1]);
    edges.sort_by(|a, b| len(b).partial_cmp(&len(a)).unwrap()); // stable: ties keep ring order
    let ((a, b), (c, d)) = (edges[0], edges[1]);
    let (long_len, short_len) = ((b[0] - a[0]).hypot(b[1] - a[1]), (d[0] - c[0]).hypot(d[1] - c[1]));
    if short_len < 6.0 {
        return None;
    }
    let angle = (b[1] - a[1]).atan2(b[0] - a[0]);
    let width = short_len * rad(rng, fu.at("coverage").f2());
    let off = (rng.f() - 0.5) * (short_len - width);
    let cen = geom::centroid(&geom::from_polyc(&PolyC { ext: ring.clone(), holes: vec![] }));
    let (cx, cy) = (cen[0] - angle.sin() * off, cen[1] + angle.cos() * off);
    let r = crate::garden::rotated_rect(cx, cy, long_len, width, angle);
    Some((r, angle, [cx, cy]))
}

/// The shared decisions of a field unit.
pub fn prepare_field(part: &Part, cfg: Cfg) -> Pre {
    let fc = cfg.at("field");
    let unit = geom::from_polyc(&part.poly);
    let mut pre = Pre::default();
    // headland blobs along the unit's edge: one candidate per `spacing` metres of it, each with its own stream
    let head = fc.at("headland");
    let ring = Polyline::new(part.poly.ext.clone());
    let length = ring.length();
    if length >= 1.0 && head.at("keep").f() > 0.0 {
        let steps = ((length / head.at("spacing_m").f()) as usize).max(1);
        let base = pid_seed(&part.cid, "field:heads");
        for i in 0..steps {
            let mut r = Rng::cell(base, i as i64, 0);
            if r.f() > head.at("keep").f() {
                continue;
            }
            let s = (i as f64 + r.f()) / steps as f64 * length;
            let (p, q) = (ring.at(s), ring.at((s + 0.5).min(length)));
            pre.heads.push(Head { p, ang: (q[1] - p[1]).atan2(q[0] - p[0]), seed: r.next_u64() });
        }
    }
    let fu = fc.at("furrow");
    let mut rng = Rng::keyed(&format!("{}:detail:field:furrow", part.cid));
    if geom::area(&unit) >= fu.at("min_area_m2").f() && rng.f() < fu.at("probability").f() {
        if let Some((rect, angle, centre)) = furrow_rect(&mut rng, &unit, fu) {
            let interior = geom::buffer(&unit, -fc.at("edge_band_m").f(), 16);
            let mut piece = geom::inter(&rect, &interior);
            let max_area = fu.at("max_area_m2").f();
            if geom::area(&piece) > max_area {
                // a strip, not the whole field: keep the part nearest its middle
                let c = geom::centroid(&rect);
                piece = geom::inter(&piece, &geom::buffer(&geom::point(c[0], c[1]), (max_area / std::f64::consts::PI).sqrt() * 1.6, 16));
            }
            if !geom::is_empty(&piece) && geom::area(&piece) > fu.at("min_piece_m2").f() {
                let rect_buf = geom::polys(&geom::buffer(&rect, 0.5, 16)).into_iter().next().unwrap_or_default();
                pre.furrow = Some(Furrow { piece: geom::polys(&piece), rect: rect_buf, angle, centre });
            }
        }
    }
    pre
}

/// Large flat tone blobs (two tones) over the unit: ``(tone_a, tone_b, colours)``. A lattice of cells of
/// ``0.9 * sqrt(1 / tone_per_ha)`` hectares holds one blob candidate each, kept with the share that makes the density the rules say.
fn tone_layers(ctx: &Ctx, unit_reg: &Region, unit_area: f64, scan: [f64; 4], section: Cfg, seed: u64) -> (G, G, [Option<[u8; 3]>; 2]) {
    let shades = section.at("tone_shades").fvec();
    let (cols, _) = palette(ctx, &section.at("tone_colours").svec(), &shades, section.at("terrain_mix"), 1.0);
    let per_ha = section.at("tone_per_ha").f();
    let n = (per_ha * unit_area / 1e4).round();
    let flat = [cols.first().copied(), cols.get(1 % cols.len().max(1)).copied()];
    let (mut a, mut b): (Vec<G>, Vec<G>) = (vec![], vec![]);
    if n > 0.0 && per_ha > 0.0 {
        let spacing = (0.9 * (1e4 / per_ha).sqrt()).max(2.0);
        let radius = section.at("tone_radius_m").f2();
        for [x, y] in lattice(seed, spacing, scan, 0.81, |x, y, _| unit_reg.contains(x, y)) {
            let mut r = point_rng(seed, x, y);
            let rr = rad(&mut r, radius);
            let g = blob(&mut r, x, y, rr, 0.4, 1.5, 8, None);
            if r.f() < 0.5 {
                a.push(g);
            } else {
                b.push(g);
            }
        }
    }
    (geom::union_all(a), geom::union_all(b), flat)
}

/// Clips patches to the tile's exact box (a patch never leaves its tile, so patches of neighbouring tiles never overlap).
fn clip_patches(patches: &mut Vec<Patch>, e: [f64; 4]) {
    for p in patches.iter_mut() {
        p.geom = geom::clip_rect(&p.geom, e[0], e[1], e[2], e[3]);
    }
    patches.retain(|p| !geom::is_empty(&p.geom) && geom::area(&p.geom) > 1e-6);
}

fn region_of_layers(got: &BTreeMap<&'static str, G>, names: &[&str]) -> Region {
    let mut ps = vec![];
    for n in names {
        if let Some(g) = got.get(n) {
            ps.extend(geom::polys(g));
        }
    }
    Region::from_polycs(&ps)
}

/// Drops points within a trunk's radius.
fn trunk_filter(ctx: &Ctx, pts: Vec<Pt>, r: f64) -> Vec<Pt> {
    if ctx.trees.is_empty() {
        return pts;
    }
    pts.into_iter().filter(|p| !ctx.trees.iter().any(|t| (p[0] - t[0]).hypot(p[1] - t[1]) <= r)).collect()
}

struct Zones {
    dirt: f64,
    edge: f64,
    tone: f64,
    litter: f64,
    interior: f64,
}

impl Zones {
    fn new(s: Cfg) -> Zones {
        Zones { dirt: s.at("dirt").fo(0.0), edge: s.at("edge").fo(0.0), tone: s.at("tone").fo(0.0), litter: s.at("litter").fo(0.0), interior: s.at("interior").fo(0.0) }
    }
    fn max(&self) -> f64 {
        self.dirt.max(self.edge).max(self.tone).max(self.litter).max(self.interior)
    }
}

/// Runs a field or forest part, or one tile of it (``tile``: the exact box). Returns the result and its meadow cells.
pub fn run(sh: &Shared, part: &Part, cfg: Cfg, pre: &Pre, tile: Option<[f64; 4]>, forest: bool) -> (Res, Cells) {
    let mut res = Res::default();
    let sec = cfg.at(if forest { "forest" } else { "field" });
    let mcfg = cfg.at("meadow");
    let band = if forest { 0.0 } else { sec.at("edge_band_m").f() };
    let halo = (band + 3.0).max(3.0);
    let unit = geom::from_polyc(&part.poly);
    let unit_reg = Region::from_polycs(std::slice::from_ref(&part.poly));
    let unit_area = geom::area(&unit);
    let (poly, near) = match tile {
        Some(e) => {
            let h = grow(e, halo);
            (geom::clip_rect(&unit, h[0], h[1], h[2], h[3]), Near::Box)
        }
        None => (geom::dup(&unit), Near::Exact),
    };
    if geom::is_empty(&poly) {
        return (res, Cells::default());
    }
    let ctx = Ctx::build(sh, &part.cid, &part.kind, poly, near, part.colour);
    let free = ctx.free_area(cfg, false);
    if geom::is_empty(free) {
        return (res, Cells::default());
    }
    let scan_box = |reach: f64| match tile {
        Some(e) => grow(e, reach),
        None => grow(part.poly.bounds(), 1.0),
    };
    let pid = part.cid.as_str();
    let mut layers: BTreeMap<&'static str, G> = BTreeMap::new();
    let mut rects: BTreeMap<&'static str, Vec<Rect>> = BTreeMap::new();
    layers.insert("verge_gravel", geom::dup(&ctx.verge_g));
    layers.insert("verge_dirt", geom::dup(&ctx.verge_d));
    let (ta, tb, tone_flat);
    if forest {
        let canopy = sh.canopy_m;
        let lit = sec.at("litter");
        // trees of the unit near this tile: litter under each, per-tree streams
        let trees: Vec<[f64; 3]> = {
            let reach = scan_box(10.0);
            sh.t_idx.query(reach).into_iter().map(|i| sh.trees[i]).filter(|t| unit_reg.contains(t[0], t[1])).collect()
        };
        let lseed = pid_seed(pid, "forest:litter");
        let mut litter_parts = vec![];
        for t in &trees {
            let mut r = point_rng(lseed, t[0], t[1]);
            let rr = canopy * t[2] * lit.at("radius_factor").f();
            for _ in 0..lit.at("blobs_per_tree").i() {
                if r.f() > lit.at("keep").f() {
                    continue;
                }
                let (a, dist) = (r.f() * TAU, r.f() * rr * 0.6);
                let radius = rr * (0.7 + 0.5 * r.f());
                litter_parts.push(blob(&mut r, t[0] + dist * a.cos(), t[1] + dist * a.sin(), radius, 0.35, 1.3, 8, None));
            }
        }
        let (a, b, f) = tone_layers(&ctx, &unit_reg, unit_area, scan_box(TONE_REACH_M), sec, pid_seed(pid, "foresttones"));
        (ta, tb, tone_flat) = (a, b, f);
        // a few bare spots, `per_ha` per hectare of the unit: one candidate per cell of 1 / per_ha hectares. The Python stage draws
        // up to 10 random places in the unit's bounding box for each spot and gives up when none is inside, so a ragged unit gets
        // fewer spots than the rules say; `keep` reproduces that share, so the density matches the reference.
        let d = sec.at("dirt");
        let per_ha = d.at("per_ha").f();
        let mut dirt = vec![];
        if per_ha > 0.0 {
            let ub = part.poly.bounds();
            let fill = (unit_area / ((ub[2] - ub[0]) * (ub[3] - ub[1])).max(1e-9)).clamp(0.0, 1.0);
            let keep = 1.0 - (1.0 - fill).powi(10);
            let s = (1e4 / per_ha).sqrt();
            let dseed = pid_seed(pid, "forest:dirt");
            let sb = scan_box(3.0);
            for j in ((sb[1] / s).floor() as i64)..=((sb[3] / s).floor() as i64) {
                for i in ((sb[0] / s).floor() as i64)..=((sb[2] / s).floor() as i64) {
                    let mut r = Rng::cell(dseed, i, j);
                    let (x, y) = ((i as f64 + r.f()) * s, (j as f64 + r.f()) * s);
                    if r.f() < keep && util::in_box(sb, x, y) && unit_reg.contains(x, y) {
                        let radius = rad(&mut r, d.at("radius_m").f2());
                        dirt.push(blob(&mut r, x, y, radius, 0.4, 1.4, 10, None));
                    }
                }
            }
        }
        layers.insert("dirt", geom::union_all(dirt));
        layers.insert("litter", geom::union_all(litter_parts));
    } else {
        let (a, b, f) = tone_layers(&ctx, &unit_reg, unit_area, scan_box(TONE_REACH_M), sec, pid_seed(pid, "fieldtones"));
        (ta, tb, tone_flat) = (a, b, f);
        // dirt headlands along the edge
        let head = sec.at("headland");
        let sb = scan_box(HEAD_REACH_M);
        let mut heads = vec![];
        for h in &pre.heads {
            if util::in_box(sb, h.p[0], h.p[1]) {
                let mut r = Rng::new(h.seed);
                let radius = rad(&mut r, head.at("radius_m").f2());
                heads.push(blob(&mut r, h.p[0], h.p[1], radius, 0.4, head.at("stretch").f(), head.at("vertices").i() as usize, Some(h.ang)));
            }
        }
        layers.insert("dirt", geom::union_all(heads));
        if let Some(f) = &pre.furrow {
            let hb = geom::bounds(&ctx.polygon);
            let piece = geom::union_all(f.piece.iter().map(|p| geom::clip_rect(&geom::from_polyc(p), hb[0], hb[1], hb[2], hb[3])).collect());
            if !geom::is_empty(&piece) {
                layers.insert("furrow", piece);
                rects.insert("furrow", vec![Rect { rect: geom::from_polyc(&f.rect), angle: f.angle, centre: f.centre }]);
            }
        }
    }
    layers.insert("tone_a", ta);
    layers.insert("tone_b", tb);
    let flat = Flat { lawn: &[], tone_a: tone_flat[0], tone_b: tone_flat[1] };
    let got = assemble(&mut res.patches, free, &layers, &flat, &rects, &mut Rng::keyed(&format!("{pid}:detail:colours")));

    // stored clutter (the dense kinds come from the raster)
    let stored: Vec<&str> = mcfg.at("stored_kinds").svec();
    let c = sec.at("clutter");
    let free_r = Region::from_geom(free);
    let furrow_r = region_of_layers(&got, &["furrow"]);
    let bare_r = region_of_layers(&got, &["dirt", "verge_dirt", "verge_gravel"]);
    let tone_r = region_of_layers(&got, &["tone_a", "tone_b"]);
    let litter_r = region_of_layers(&got, &["litter"]);
    let interior_r = if band > 0.0 { Some(Region::from_geom(&geom::buffer(free, -band, 16))) } else { None };
    let mut em = Emitter::new(sh);
    let order: &[&str] = if forest { &["leaf", "stick", "rock", "pebble", "tuft"] } else { &["tuft", "pebble", "rock", "stick", "flower"] };
    let trunk_r = cfg.at("trunk_radius_m").f();
    let tint = Tint::new(&ctx, cfg);
    let box_for_points = match tile {
        Some(e) => e,
        None => grow(geom::bounds(free), 1.0),
    };
    for kind in order {
        let s = c.at(kind);
        if s.is_null() || !stored.contains(kind) {
            continue;
        }
        let z = Zones::new(s);
        let seed = pid_seed(pid, kind);
        let pts = crate::util::lattice_in(seed, s.at("spacing_m").f(), box_for_points, z.max(), Some(&free_r), |x, y, u| {
            let dens = if forest {
                // (bare, dirt), (litter, litter), (tones, tone), (free, interior)
                if !free_r.contains(x, y) {
                    return false;
                }
                if bare_r.contains(x, y) {
                    z.dirt
                } else if litter_r.contains(x, y) {
                    z.litter
                } else if tone_r.contains(x, y) {
                    z.tone
                } else {
                    z.interior
                }
            } else {
                // meadow = free without the furrow; (bare, dirt), (edge band, edge), (tones, tone), (meadow, interior)
                if !free_r.contains(x, y) || furrow_r.contains(x, y) {
                    return false;
                }
                if bare_r.contains(x, y) {
                    z.dirt
                } else if interior_r.as_ref().is_some_and(|r| !r.contains(x, y)) {
                    z.edge
                } else if tone_r.contains(x, y) {
                    z.tone
                } else {
                    z.interior
                }
            };
            u < dens
        });
        em.emit(kind, seed, &trunk_filter(&ctx, pts, trunk_r), s.at("scale").f2(), Some(&tint));
    }
    res.clutter = em.out;

    let atype = if forest { "forest" } else { "field" };
    let cells = meadow::add_cells(&ctx, &res.patches, atype, free, mcfg, band, tile);
    if let Some(e) = tile {
        clip_patches(&mut res.patches, e);
    }
    (res, cells)
}

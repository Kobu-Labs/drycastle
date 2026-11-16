//! Swamp ground, mud and puddles of the forest and meadow floor (``detail._swamp_layers``, ``detail._forest_mud``, ``detail._hollow``).
//!
//! Every candidate is a pure function of its position (lattice cells with their own streams, per-way streams), so tiles of a big area
//! agree along their seams. The draws differ from the Python stage's; the recipe and the numbers (``detail.swamp``, ``detail.hollow``,
//! ``detail.forest.mud``) are the same.

use crate::cfg::Cfg;
use crate::ctx::Ctx;
use crate::field::pid_seed;
use crate::geom::{self, Pt, G};
use crate::index::Region;
use crate::noise::clump;
use crate::rng::Rng;
use crate::terrain::Terrain;
use crate::util::{self, blob, lattice_in, point_rng, rad, Polyline};
use std::collections::BTreeMap;
use std::f64::consts::TAU;

/// The noises of mud and puddles (``detail.MUD_SALT`` and the others).
const MUD_SALT: i64 = 5200;
const PUDDLE_SALT: i64 = 5300;
const DAMP_SALT: i64 = 5400;

/// How far below its surroundings the ground lies at a point, 0..1 (``detail._hollow``): the mean terrain height on a ring of
/// ``ring_m`` minus the point's own, over ``depth_m``.
pub fn hollow(t: &Terrain, x: f64, y: f64, h: Cfg) -> f64 {
    let r = h.at("ring_m").f();
    let ring: f64 = (0..8).map(|k| k as f64 * TAU / 8.0).map(|a| t.surface(x + r * a.cos(), y + r * a.sin())).sum::<f64>() / 8.0;
    ((ring - t.surface(x, y)) / h.at("depth_m").f()).clamp(0.0, 1.0)
}

/// The wet floor of ``ctx.swamp``: ``swamp`` (the base colour over all of it), large ``mud`` patches, and small ``puddle`` patches (likelier in
/// hollows) each in a rim of mud. Shapes only: ``assemble`` clips them. ``bx``: the box the candidates are drawn in.
pub fn swamp_layers(ctx: &Ctx, cfg: Cfg, free: &G, pid: &str, bx: [f64; 4]) -> BTreeMap<&'static str, G> {
    let mut out = BTreeMap::new();
    let Some(zone) = &ctx.swamp else { return out };
    let area = geom::inter(free, zone);
    if geom::is_empty(&area) {
        return out;
    }
    let (sc, hol) = (cfg.at("swamp"), cfg.at("hollow"));
    let region = Region::from_geom(&area);
    let (mc, pc) = (sc.at("mud"), sc.at("puddle"));
    let mut mud = vec![];
    let mut puddles = vec![];

    let (keep, amount, lat) = (mc.at("keep").f(), mc.at("clump").f(), mc.at("clump_m").f());
    let seed = pid_seed(pid, "swamp:mud");
    let pts = lattice_in(seed, mc.at("spacing_m").f(), bx, (keep * (1.0 + amount)).min(1.0), Some(&region), |x, y, u| {
        region.contains(x, y) && u < keep * clump(x, y, amount, lat, MUD_SALT)
    });
    for p in pts {
        let mut r = point_rng(seed, p[0], p[1]);
        let radius = rad(&mut r, mc.at("radius_m").f2());
        mud.push(blob(&mut r, p[0], p[1], radius, 0.4, mc.at("stretch").f(), 10, None));
    }

    let (keep, amount, lat, gain) = (pc.at("keep").f(), pc.at("clump").f(), pc.at("clump_m").f(), hol.at("gain").f());
    let seed = pid_seed(pid, "swamp:puddle");
    let rim = pc.at("rim_m").f();
    let t = &ctx.sh.terrain;
    let pts = lattice_in(seed, pc.at("spacing_m").f(), bx, (keep * (1.0 + amount) * (1.0 + gain)).min(1.0), Some(&region), |x, y, u| {
        region.contains(x, y) && u < keep * clump(x, y, amount, lat, PUDDLE_SALT) * (1.0 + gain * hollow(t, x, y, hol))
    });
    for p in pts {
        let mut r = point_rng(seed, p[0], p[1]);
        let radius = rad(&mut r, pc.at("radius_m").f2());
        let g = blob(&mut r, p[0], p[1], radius, 0.35, pc.at("stretch").f(), 9, None);
        if rim > 0.0 {
            mud.push(geom::buffer(&g, rim, 2));
        }
        puddles.push(g);
    }
    out.insert("mud", geom::union_all(mud));
    out.insert("puddle", geom::union_all(puddles));
    out
}

/// Mud and puddles of the plain forest floor, outside the swamp (``detail._forest_mud``): a few patches per hectare in damp spots (a noise,
/// likelier in hollows) and at the edge of the paths and tracks; a puddle in the middle of some. ``(mud, puddles)``.
pub fn forest_mud(ctx: &Ctx, cfg: Cfg, free: &G, pid: &str, bx: [f64; 4]) -> (G, G) {
    let (mc, hol) = (cfg.at("forest").at("mud"), cfg.at("hollow"));
    let area = match &ctx.swamp {
        Some(s) => geom::diff(free, s),
        None => geom::dup(free),
    };
    if geom::is_empty(&area) {
        return (geom::empty(), geom::empty());
    }
    let region = Region::from_geom(&area);
    let mut spots: Vec<(Pt, Option<f64>)> = vec![];
    let d = mc.at("damp");
    let (keep, amount, lat, gain) = (d.at("keep").f(), d.at("clump").f(), d.at("clump_m").f(), hol.at("gain").f());
    let t = &ctx.sh.terrain;
    let seed = pid_seed(pid, "mud:damp");
    for p in lattice_in(seed, d.at("spacing_m").f(), bx, (keep * (1.0 + amount) * (1.0 + gain)).min(1.0), Some(&region), |x, y, u| {
        region.contains(x, y) && u < keep * clump(x, y, amount, lat, DAMP_SALT) * (1.0 + gain * hollow(t, x, y, hol))
    }) {
        spots.push((p, None));
    }
    // the ways: each has its own stream, so every tile and parcel it crosses sees the same spots
    let tr = mc.at("track");
    let (spacing, tkeep) = (tr.at("spacing_m").f(), tr.at("keep").f());
    for &ei in &ctx.tracks {
        let e = &ctx.sh.roads[ei];
        let line = Polyline::new(e.line.clone());
        let len = line.length();
        if len < 1.0 {
            continue;
        }
        let mut er = Rng::keyed(&format!("{}:detail:mud", e.id));
        let mut s = er.f() * spacing;
        while s < len {
            if er.f() < tkeep {
                let (p, q) = (line.at(s), line.at((s + 0.5).min(len)));
                let ang = (q[1] - p[1]).atan2(q[0] - p[0]);
                let off = e.width / 2.0 + rad(&mut er, mc.at("radius_m").f2()) * 0.4;
                let side = if er.f() < 0.5 { 1.0 } else { -1.0 };
                spots.push(([p[0] - side * ang.sin() * off, p[1] + side * ang.cos() * off], Some(ang)));
            }
            s += spacing * (0.6 + 0.8 * er.f());
        }
    }
    let (mut mud, mut puddles) = (vec![], vec![]);
    let bseed = pid_seed(pid, "mud:blob");
    for (p, ang) in spots {
        if !util::in_box(bx, p[0], p[1]) || !region.contains(p[0], p[1]) {
            continue;
        }
        let mut r = point_rng(bseed, p[0], p[1]);
        let radius = rad(&mut r, mc.at("radius_m").f2());
        mud.push(blob(&mut r, p[0], p[1], radius, 0.4, mc.at("stretch").f(), 10, ang));
        if r.f() < mc.at("puddle_share").f() {
            let pr = radius * rad(&mut r, mc.at("puddle_scale").f2());
            puddles.push(blob(&mut r, p[0], p[1], pr, 0.35, 1.3, 8, None));
        }
    }
    (geom::union_all(mud), geom::union_all(puddles))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_bowl_is_a_hollow_and_a_plane_is_not() {
        // 9 x 9 vertices of 4 m: a plane z = 0.05 x, a bowl and its mirror
        let n = 9;
        let (mut plane, mut bowl) = (vec![], vec![]);
        for r in 0..n {
            for c in 0..n {
                let (x, y) = (c as f64 * 4.0 - 16.0, r as f64 * 4.0 - 16.0);
                plane.push(0.05 * x);
                bowl.push(0.01 * (x * x + y * y));
            }
        }
        let cfg = serde_json::json!({"ring_m": 6.0, "depth_m": 0.25});
        let hol = Cfg(&cfg);
        let tp = Terrain::new(32.0, 8, -4, -4, n, n, plane, vec![true; n * n]);
        let tb = Terrain::new(32.0, 8, -4, -4, n, n, bowl, vec![true; n * n]);
        assert!(hollow(&tp, 0.0, 0.0, hol) < 1e-9);
        assert!(hollow(&tb, 0.0, 0.0, hol) > 0.9);
        let hill = Terrain::new(32.0, 8, -4, -4, n, n, tb.heights.iter().map(|z| -z).collect(), vec![true; n * n]);
        assert!(hollow(&hill, 0.0, 0.0, hol) < 1e-9); // a hilltop is no hollow
    }

    #[test]
    fn the_bowl_of_python_and_rust_agree() {
        // reference value computed with detail._hollow on the same bowl (tests/test_detail.py: test_hollow_is_the_dip_below_the_ring)
        let n = 9;
        let mut bowl = vec![];
        for r in 0..n {
            for c in 0..n {
                let (x, y) = (c as f64 * 4.0 - 16.0, r as f64 * 4.0 - 16.0);
                bowl.push(0.002 * (x * x + y * y));
            }
        }
        let cfg = serde_json::json!({"ring_m": 6.0, "depth_m": 0.25});
        let tb = Terrain::new(32.0, 8, -4, -4, n, n, bowl, vec![true; n * n]);
        let h = hollow(&tb, 1.0, 2.0, Cfg(&cfg));
        assert!((h - 0.2795290039756342).abs() < 1e-9, "{h}");
    }
}

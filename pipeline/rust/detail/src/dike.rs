//! The flood dike (dike.py): a gravel track with grass patches along the road, flat grass beside it with a ragged border and
//! thick grass as clutter.

use crate::cfg::Cfg;
use crate::ctx::{hex_rgb, Ctx};
use crate::emit::{r3, Emitter, Res, Tint};
use crate::geom::{self, Pt, G};
use crate::index::{DistIdx, Region};
use crate::patches::{assemble, clean, Flat, Patch, Rect};
use crate::rng::{seed_of, Rng};
use crate::util::{blob, lattice, Polyline};
use std::collections::BTreeMap;
use std::f64::consts::TAU;

const PATCH_RGB: [[f64; 3]; 2] = [[0x6F as f64, 0xA0 as f64, 0x4A as f64], [0x4F as f64, 0x7D as f64, 0x3A as f64]];

/// Rough noise 0..1 along ``s`` metres: waves of 1.4 to 16 m, stretched so that it reaches both ends often.
fn wander(rng: &mut Rng, s: &[f64]) -> Vec<f64> {
    let waves = [(1.4, 0.35), (3.1, 0.5), (7.0, 0.7), (16.0, 0.6)];
    let phases: Vec<f64> = waves.iter().map(|_| rng.f() * TAU).collect();
    let norm: f64 = waves.iter().map(|w| w.1).sum();
    s.iter()
        .map(|&d| {
            let out: f64 = waves.iter().zip(&phases).map(|(&(wl, amp), ph)| amp * (d / wl * TAU + ph).sin()).sum::<f64>() / norm;
            0.5 + 0.5 * (1.7 * out).clamp(-1.0, 1.0)
        })
        .collect()
}

/// ``np.gradient`` along the points (central differences inside, one-sided at the ends).
fn gradient(pts: &[Pt]) -> Vec<Pt> {
    let n = pts.len();
    (0..n)
        .map(|i| {
            let (a, b, div) = if i == 0 { (0, 1, 1.0) } else if i == n - 1 { (n - 2, n - 1, 1.0) } else { (i - 1, i + 1, 2.0) };
            [(pts[b][0] - pts[a][0]) / div, (pts[b][1] - pts[a][1]) / div]
        })
        .collect()
}

/// A ribbon round ``line``, ``half`` wide each side at least, the edges wandering out by up to ``extra`` and loose pebbles lying
/// beyond them.
fn ribbon(line: &Polyline, half: f64, extra: f64, rng: &mut Rng, spill: Cfg) -> G {
    let length = line.length();
    let n = ((length / 0.35) as usize + 1).max(2);
    let s: Vec<f64> = (0..n).map(|i| length * i as f64 / (n - 1) as f64).collect();
    let pts: Vec<Pt> = s.iter().map(|&d| line.at(d)).collect();
    let tan: Vec<Pt> = gradient(&pts)
        .into_iter()
        .map(|t| {
            let l = t[0].hypot(t[1]).max(1e-9);
            [t[0] / l, t[1] / l]
        })
        .collect();
    let nor: Vec<Pt> = tan.iter().map(|t| [-t[1], t[0]]).collect();
    let off_l: Vec<f64> = wander(rng, &s).into_iter().map(|w| half + extra * w).collect();
    let off_r: Vec<f64> = wander(rng, &s).into_iter().map(|w| half + extra * w).collect();
    let mut ring: Vec<Pt> = (0..n).map(|i| [pts[i][0] + nor[i][0] * off_l[i], pts[i][1] + nor[i][1] * off_l[i]]).collect();
    ring.extend((0..n).rev().map(|i| [pts[i][0] - nor[i][0] * off_r[i], pts[i][1] - nor[i][1] * off_r[i]]));
    let mut parts = vec![geom::polygon_fixed(&ring)];
    let (spacing, rad, reach) = (spill.at("spacing_m").f(), spill.at("radius_m").f2(), spill.at("reach_m").f());
    for (off, sign) in [(&off_l, 1.0), (&off_r, -1.0)] {
        let mut d = rng.f() * spacing;
        while d < length {
            let i = (n - 1).min((d / length * (n - 1) as f64) as usize);
            let r = rad[0] + rng.f() * (rad[1] - rad[0]);
            let out = off[i] + (rng.f() * 1.1 - 0.35) * reach;
            let c = [pts[i][0] + sign * nor[i][0] * out, pts[i][1] + sign * nor[i][1] * out];
            let stretch = 1.0 + rng.f();
            parts.push(blob(rng, c[0], c[1], r, 0.55, stretch, 14, Some(tan[i][1].atan2(tan[i][0]))));
            d += spacing * (0.4 + 1.2 * rng.f());
        }
    }
    geom::union_all(parts)
}

/// Rough noise 0..1 along a closed ring of ``length`` metres (whole numbers of waves, so it joins up).
fn closed_wander(rng: &mut Rng, length: f64, s: &[f64]) -> Vec<f64> {
    let mut out = vec![0.0; s.len()];
    for (wl, amp) in [(7.0, 0.5), (14.0, 0.8), (32.0, 0.7)] {
        let k = ((length / wl).round_ties_even()).max(1.0);
        let ph = rng.f() * TAU;
        for (o, &d) in out.iter_mut().zip(s) {
            *o += amp * (d / length * TAU * k + ph).sin();
        }
    }
    out.into_iter().map(|o| 0.5 + 0.5 * (1.5 * o / 2.0).clamp(-1.0, 1.0)).collect()
}

struct Ragged {
    hull: G,
    ring: Vec<Pt>,
    nor: Vec<Pt>,
    off: Vec<f64>,
}

/// The parcel's outline pushed in by up to ``inward`` and out by up to ``outward`` metres, smoothly and at random.
fn ragged(poly: &G, rng: &mut Rng, inward: f64, outward: f64) -> Ragged {
    let p = geom::polys(&geom::buffer0(poly)).into_iter().max_by(|a, b| a.ext.len().cmp(&b.ext.len())).unwrap_or_default();
    let mut ext = p.ext.clone();
    // counter-clockwise exterior
    let signed: f64 = ext.windows(2).map(|w| w[0][0] * w[1][1] - w[1][0] * w[0][1]).sum();
    if signed < 0.0 {
        ext.reverse();
    }
    let line = Polyline::new(ext);
    let length = line.length();
    let n = ((length / 0.7) as usize).max(8);
    let s: Vec<f64> = (0..n).map(|i| length * i as f64 / n as f64).collect();
    let pts: Vec<Pt> = s.iter().map(|&d| line.at(d)).collect();
    let nor: Vec<Pt> = (0..n)
        .map(|i| {
            let (a, b) = (pts[(i + n - 1) % n], pts[(i + 1) % n]);
            let (tx, ty) = (b[0] - a[0], b[1] - a[1]);
            let l = tx.hypot(ty).max(1e-9);
            [ty / l, -tx / l] // outward for a counter-clockwise ring
        })
        .collect();
    let w = closed_wander(rng, length, &s);
    let off: Vec<f64> = w.into_iter().map(|w| -inward + (inward + outward) * w.powf(1.3)).collect();
    let moved: Vec<Pt> = (0..n).map(|i| [pts[i][0] + nor[i][0] * off[i], pts[i][1] + nor[i][1] * off[i]]).collect();
    let mut hull = geom::polygon_fixed(&moved);
    let ps = geom::polys_g(&hull);
    if geom::is_empty(&hull) || ps.len() != 1 {
        hull = ps.into_iter().max_by(|a, b| geom::area(a).partial_cmp(&geom::area(b)).unwrap()).unwrap_or_else(|| geom::dup(poly));
    }
    Ragged { hull, ring: pts, nor, off }
}

struct Img<'a> {
    w: usize,
    h: usize,
    px: &'a [u8],
}

impl Img<'_> {
    fn texel(&self, u: f64, v: f64) -> [f64; 3] {
        let row = (((1.0 - v).rem_euclid(1.0) * self.h as f64).floor() as usize) % self.h;
        let col = ((u.rem_euclid(1.0) * self.w as f64).floor() as usize) % self.w;
        let i = (row * self.w + col) * 3;
        [self.px[i] as f64, self.px[i + 1] as f64, self.px[i + 2] as f64]
    }
}

/// The track as patches: per road stretch of ``piece_m``, a rectangle along it clipped to the track.
fn track_pieces(ctx: &Ctx, rc: Cfg, rng: &mut Rng, tile: (f64, f64)) -> Vec<Patch> {
    let mut track_parts = vec![];
    let mut pieces: Vec<(Pt, Pt)> = vec![];
    let (tw, th) = tile;
    for (line, _width) in &ctx.road_lines {
        let inside = geom::inter(line, &ctx.polygon);
        for seg in geom::lines_g(&inside) {
            let pl = Polyline::new(geom::coords(&seg));
            if pl.length() < 0.5 {
                continue;
            }
            track_parts.push(ribbon(&pl, rc.at("track_width_m").f() / 2.0, rc.at("track_wobble_m").f(), rng, rc.at("spill")));
            let k = ((pl.length() / rc.at("piece_m").f()).round_ties_even() as usize).max(1);
            for i in 0..k {
                pieces.push((pl.at(pl.length() * i as f64 / k as f64), pl.at(pl.length() * (i + 1) as f64 / k as f64)));
            }
        }
    }
    if track_parts.is_empty() {
        return vec![];
    }
    let walls = if ctx.buildings.is_empty() { geom::empty() } else { geom::union_all(ctx.buildings.iter().map(geom::dup).collect()) };
    let track = clean(&geom::diff(&geom::inter(&geom::union_all(track_parts), &ctx.polygon), &walls), 0.04);
    let mut patches: Vec<Patch> = vec![];
    let mut done: Vec<([f64; 4], G)> = vec![]; // the pieces laid so far, to keep a piece off the ones beside it
    let lift = rc.at("track_lift_m").f();
    for (a, b) in pieces {
        let ang = (b[1] - a[1]).atan2(b[0] - a[0]);
        let (ux, uy) = (ang.cos(), ang.sin());
        // a rectangle along the stretch, a little longer than it so that bends leave no gap, wide enough for the track
        let l = geom::line(&[[a[0] - ux * 0.6, a[1] - uy * 0.6], [b[0] + ux * 0.6, b[1] + uy * 0.6]]);
        let rect = geom::buffer_flat(&l, rc.at("track_wobble_m").f() + 3.0);
        let rb = geom::bounds(&rect);
        let mut g = geom::inter(&geom::clip_rect(&track, rb[0], rb[1], rb[2], rb[3]), &rect);
        if geom::is_empty(&g) {
            continue;
        }
        let gb = geom::bounds(&g);
        let near: Vec<G> = done.iter().filter(|(bb, _)| crate::util::boxes_meet(*bb, gb)).map(|(_, d)| geom::dup(d)).collect();
        if !near.is_empty() {
            g = geom::diff(&g, &geom::union_all(near));
        }
        if geom::is_empty(&g) || geom::area(&g) < 0.05 {
            continue;
        }
        done.push((geom::bounds(&g), geom::dup(&g)));
        // the track's centre line at v = 0.5 of the tile, and a random start along it
        let shift = rng.f() * tw;
        let origin = [a[0] - ux * shift + uy * 0.5 * th, a[1] - uy * shift - ux * 0.5 * th];
        patches.push(Patch { layer: "dike_track", geom: g, colour: None, texture: Some("dike_gravel"), angle: ang, origin, lift: Some(lift) });
    }
    let taken = geom::union_all(done.into_iter().map(|(_, g)| g).collect());
    let rest = clean(&geom::diff(&track, &taken), 0.0);
    if !geom::is_empty(&rest) && !patches.is_empty() {
        // leftovers at bends: the texture as the first piece lays it
        let (angle, origin, l) = (patches[0].angle, patches[0].origin, patches[0].lift);
        patches.push(Patch { layer: "dike_track", geom: rest, colour: None, texture: Some("dike_gravel"), angle, origin, lift: l });
    }
    patches
}

pub fn dike(ctx: &Ctx, cfg: Cfg, image: (usize, usize, &[u8]), tile: (f64, f64)) -> Res {
    let pid = ctx.id.as_str();
    let rc = cfg.at("dike");
    let mut res = Res::default();
    let mut rng = Rng::keyed(&format!("{pid}:detail:dike"));
    let track_patches = track_pieces(ctx, rc, &mut rng, tile);
    let track = geom::union_all(track_patches.iter().map(|p| geom::dup(&p.geom)).collect());

    let bc = rc.at("border");
    let rg = ragged(&ctx.polygon, &mut rng, bc.at("inward_m").f(), bc.at("outward_m").f());
    let hull = &rg.hull;
    // the ground rules (buildings, fences, ...) on the ragged outline; the lawn covers the road inside the parcel (the track is
    // narrower than the carriageway under it), not beyond the parcel line
    let mut free = ctx.free_of(cfg, true, hull, false);
    if let Some(road) = ctx.road.as_ref().filter(|r| !geom::is_empty(*r)) {
        let beyond = geom::diff(road, &geom::buffer(&ctx.polygon, 0.3, 16));
        free = geom::diff(&free, &geom::buffer_mitre(&beyond, cfg.at("road_clearance_m").f()));
    }
    let cols: Vec<[u8; 3]> = rc.at("lawn_colours").svec().iter().map(|h| hex_rgb(h)).collect();
    let before = res.patches.len();
    let mut layers: BTreeMap<&'static str, G> = BTreeMap::new();
    layers.insert("lawn", geom::dup(&free));
    let flat = Flat { lawn: &cols, tone_a: None, tone_b: None };
    let rects: BTreeMap<&'static str, Vec<Rect>> = BTreeMap::new();
    let got = assemble(&mut res.patches, &free, &layers, &flat, &rects, &mut Rng::keyed(&format!("{pid}:detail:colours")));
    let lawn_lift = bc.at("lawn_lift_m").f();
    for p in res.patches[before..].iter_mut() {
        p.lift = Some(lawn_lift); // over the neighbours' own patches, which lie at lift_m
    }
    let covered = got.get("lawn").map_or_else(geom::empty, geom::dup);
    // loose blobs of lawn beyond the border, smaller the further out, so the green frays into what lies there
    let reach_poly = geom::buffer(hull, bc.at("blob_reach_m").f() + 2.0, 16);
    let reach = ctx.free_of(cfg, true, &reach_poly, true);
    let mut blobs = vec![];
    let step = bc.at("blob_spacing_m").f();
    let mut d = rng.f() * step;
    let (reach_m, radii) = (bc.at("blob_reach_m").f(), bc.at("blob_radius_m").f2());
    let nring = rg.ring.len();
    while d < nring as f64 * 0.7 {
        let i = (nring - 1).min((d / 0.7) as usize);
        let away = rng.f().powf(1.5) * reach_m; // most of them close to the border
        let r = radii[1] * (1.0 - 0.75 * away / reach_m) * (0.5 + 0.5 * rng.f());
        let c = [rg.ring[i][0] + rg.nor[i][0] * (rg.off[i] + away), rg.ring[i][1] + rg.nor[i][1] * (rg.off[i] + away)];
        let stretch = 1.0 + rng.f();
        blobs.push(blob(&mut rng, c[0], c[1], r.max(radii[0]), 0.5, stretch, 14, None));
        d += step * (0.4 + 1.2 * rng.f());
    }
    let spill = if blobs.is_empty() { geom::empty() } else { clean(&geom::diff(&geom::inter(&geom::union_all(blobs), &reach), hull), 0.1) };
    for poly in geom::polys_g(&spill) {
        let c = cols[rng.below(cols.len())];
        res.patches.push(Patch { layer: "lawn", geom: poly, colour: Some(c), texture: None, angle: 0.0, origin: [0.0, 0.0], lift: Some(lawn_lift) });
    }
    // the grass grows on the lawn and its loose blobs but not on the track (5 cm beyond it): the track is cut out point by point
    // (a distance to the track's edges) instead of buffering and subtracting its big outline
    let grass = geom::union2(&covered, &spill);
    let d_track = if geom::is_empty(&track) { DistIdx::empty() } else { DistIdx::new(&[], &geom::polys(&track)) };

    let gc = rc.at("grass");
    let seed = seed_of(&format!("{pid}:detail:dike_grass"));
    let grass_r = Region::from_geom(&grass);
    let poly_r = Region::from_geom(&ctx.polygon);
    let exterior = DistIdx::new(&geom::polys(&ctx.polygon).into_iter().map(|p| p.ext).collect::<Vec<_>>(), &[]);
    let fade = bc.at("fade_m").f();
    // the grass thins out across the border (half as dense on it, none beyond fade_m outside, full inside)
    let pts = crate::util::lattice_in(seed, gc.at("spacing_m").f(), geom::bounds(&grass), 1.0, Some(&grass_r), |x, y, u| {
        if !grass_r.contains(x, y) || d_track.dist(x, y) <= 0.05 {
            return false;
        }
        let dist = exterior.dist(x, y);
        let signed = if poly_r.contains(x, y) { dist } else { -dist };
        u < (0.5 + signed / (2.0 * fade)).clamp(0.0, 1.0)
    });
    let tint = Tint::new(ctx, cfg);
    let mut em = Emitter::new(ctx.sh);
    for (k, sub) in crate::village::split_kinds(seed, &pts, &gc.at("kinds").shares()) {
        em.emit(&k, seed, &sub, gc.at("scale").f2(), Some(&tint));
    }
    if !em.out.is_empty() {
        for c in em.out.iter_mut() {
            // taller the further from the track: the sides of the dike
            let dd = d_track.dist(c.x, c.y);
            c.scale = r3(c.scale * (1.0 + gc.at("reach_scale").f() * (dd / gc.at("reach_m").f()).min(1.0)));
        }
    }
    let mut out = em.out;

    res.patches.extend(track_patches);
    // tufts on the drawn grass patches of the track (read from the texture, so they sit on the drawn patches)
    let pt = rc.at("patch_tuft");
    let img = Img { w: image.0, h: image.1, px: image.2 };
    let tseed = seed_of(&format!("{pid}:detail:dike_patch_tuft"));
    let mut em2 = Emitter::new(ctx.sh);
    for p in res.patches.iter().filter(|p| p.layer == "dike_track") {
        let pr = Region::from_geom(&p.geom);
        let pts = lattice(tseed, pt.at("spacing_m").f(), geom::bounds(&p.geom), 1.0, |x, y, _| pr.contains(x, y));
        let (c, s) = ((-p.angle).cos(), (-p.angle).sin());
        let keep = pt.at("keep").f();
        let on: Vec<Pt> = pts
            .into_iter()
            .filter(|q| {
                let (dx, dy) = (q[0] - p.origin[0], q[1] - p.origin[1]);
                let rgb = img.texel((dx * c - dy * s) / tile.0, (dx * s + dy * c) / tile.1);
                let near = PATCH_RGB.iter().any(|t| (rgb[0] - t[0]).abs() + (rgb[1] - t[1]).abs() + (rgb[2] - t[2]).abs() < 12.0);
                near && crate::util::point_rng(tseed ^ 0x77, q[0], q[1]).f() < keep
            })
            .collect();
        em2.emit("tuft", tseed, &on, pt.at("scale").f2(), Some(&tint));
    }
    out.extend(em2.out);
    res.clutter = out;
    res
}

//! The village recipe (street sides, greens): grass over all the ground beside the carriageway, gravel and dirt verge
//! blobs, tufts along the asphalt edge and pebbles on the gravel.

use crate::cfg::Cfg;
use crate::ctx::{hex_rgb, Ctx};
use crate::emit::{Emitter, Res, Tint};
use crate::geom::{self, Pt, G};
use crate::index::Region;
use crate::patches::{assemble, Flat, Rect};
use crate::rng::{seed_of, Rng};
use crate::util::{lattice, point_rng};
use std::collections::BTreeMap;

fn layer_region(got: &BTreeMap<&'static str, G>, name: &str) -> Region {
    got.get(name).map_or_else(Region::empty, Region::from_geom)
}

/// Points dealt out to kinds by their shares (``[[kind, share], ...]``).
pub fn split_kinds(seed: u64, pts: &[Pt], kinds: &[(&str, f64)]) -> Vec<(String, Vec<Pt>)> {
    let total: f64 = kinds.iter().map(|k| k.1).sum();
    let mut out: Vec<(String, Vec<Pt>)> = kinds.iter().map(|k| (k.0.to_string(), vec![])).collect();
    for p in pts {
        let r = point_rng(seed ^ 0x5911, p[0], p[1]).f();
        let mut acc = 0.0;
        let mut idx = kinds.len() - 1;
        for (i, k) in kinds.iter().enumerate() {
            acc += k.1 / total;
            if r < acc {
                idx = i;
                break;
            }
        }
        out[idx].1.push(*p);
    }
    out.retain(|(_, v)| !v.is_empty());
    out
}

pub fn village(ctx: &Ctx, cfg: Cfg) -> Res {
    let pid = ctx.id.as_str();
    let rc = cfg.at("village");
    let mut res = Res::default();
    if ctx.road.as_ref().is_none_or(geom::is_empty) {
        return res;
    }
    let free = ctx.free_area(cfg, false);
    let cols: Vec<[u8; 3]> = rc.at("grass_colours").svec().iter().map(|h| hex_rgb(h)).collect();
    let mut layers: BTreeMap<&'static str, G> = BTreeMap::new();
    layers.insert("verge_gravel", geom::dup(&ctx.verge_g));
    layers.insert("verge_dirt", geom::dup(&ctx.verge_d));
    layers.insert("lawn", geom::dup(free));
    // a worn footpath's edge: two bands of thin grass between the bare ground and the lawn, the lawn's colour drifting to trodden
    let trodden = if cfg.at("paths").has("fringe_colour") { hex_rgb(cfg.at("paths").at("fringe_colour").s()) } else { [138, 122, 85] };
    let toward = |t: f64| -> [u8; 3] { [0, 1, 2].map(|i| (cols[0][i] as f64 + (trodden[i] as f64 - cols[0][i] as f64) * t).round() as u8) };
    let has_fringe = !geom::is_empty(&ctx.fringe[0]) || !geom::is_empty(&ctx.fringe[1]);
    if has_fringe {
        layers.insert("tone_a", geom::dup(&ctx.fringe[0]));
        layers.insert("tone_b", geom::dup(&ctx.fringe[1]));
    }
    let flat = Flat { lawn: &cols, swamp: &[], tone_a: has_fringe.then(|| toward(0.62)), tone_b: has_fringe.then(|| toward(0.3)) };
    let rects: BTreeMap<&'static str, Vec<Rect>> = BTreeMap::new();
    let got = assemble(&mut res.patches, free, &layers, &flat, &rects, &mut Rng::keyed(&format!("{pid}:detail:colours")));
    let (gravel, dirt, grass) = (layer_region(&got, "verge_gravel"), layer_region(&got, "verge_dirt"), layer_region(&got, "lawn"));
    let (thin_a, thin_b) = (layer_region(&got, "tone_a"), layer_region(&got, "tone_b"));
    let all = geom::union_all(["verge_gravel", "verge_dirt", "tone_a", "tone_b", "lawn"].iter().filter_map(|n| got.get(n)).map(geom::dup).collect());
    let bounds = geom::bounds(&all);
    let tint = Tint::new(ctx, cfg);
    let mut em = Emitter::new(ctx.sh);
    let c = rc.at("clutter");
    for kind in ["tuft", "pebble", "rock", "flower", "leaf"] {
        let s = c.at(kind);
        if s.is_null() {
            continue;
        }
        let (vg, vd, vgr) = (s.at("gravel").fo(0.0), s.at("dirt").fo(0.0), s.at("grass").fo(0.0));
        let seed = seed_of(&format!("{pid}:detail:{kind}"));
        let pts = lattice(seed, s.at("spacing_m").f(), bounds, vg.max(vd).max(vgr), |x, y, u| {
            let dens = if gravel.contains(x, y) {
                vg
            } else if dirt.contains(x, y) {
                vd
            } else if grass.contains(x, y) {
                vgr
            } else if thin_b.contains(x, y) {
                vgr * 0.6 // the grass thins out towards a footpath
            } else if thin_a.contains(x, y) {
                vgr * 0.25
            } else {
                return false;
            };
            u < dens
        });
        if s.has("kinds") {
            for (k, sub) in split_kinds(seed, &pts, &s.at("kinds").shares()) {
                em.emit(&k, seed, &sub, s.at("scale").f2(), Some(&tint));
            }
        } else {
            em.emit(kind, seed, &pts, s.at("scale").f2(), Some(&tint));
        }
    }
    res.clutter = em.out;
    res
}

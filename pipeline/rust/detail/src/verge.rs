//! Gravel and dirt blobs along the carriageway edges of the roads in ``detail.verge.highways`` (``make_verge``).

use crate::cfg::Cfg;
use crate::ctx::RoadEdge;
use crate::geom::{self, PolyC};
use crate::rng::Rng;
use crate::util::{blob, rad, Polyline};
use rayon::prelude::*;

/// ``[gravel, dirt]`` blobs; each road edge has its own stream (``<edge id>:detail:verge``) and the edges are joined in order.
pub fn make_verge(roads: &[RoadEdge], cfg: Cfg) -> [Vec<PolyC>; 2] {
    let highways = cfg.at("highways").svec();
    let per_edge: Vec<[Vec<PolyC>; 2]> = roads
        .par_iter()
        .map(|e| {
            let mut out: [Vec<PolyC>; 2] = [vec![], vec![]];
            if !highways.contains(&e.highway.as_str()) {
                return out;
            }
            let mut rng = Rng::keyed(&format!("{}:detail:verge", e.id));
            let line = Polyline::new(e.line.clone());
            let (half, length) = (e.width / 2.0, line.length());
            let step = cfg.at("step_m").f2();
            let mut s = rng.f() * step[1];
            while s < length {
                let (p, q) = (line.at(s), line.at((s + 0.5).min(length)));
                let ang = (q[1] - p[1]).atan2(q[0] - p[0]);
                for side in [-1.0, 1.0] {
                    if rng.f() > cfg.at("keep").f() {
                        continue;
                    }
                    let off = half + rad(&mut rng, cfg.at("offset_m").f2());
                    let (cx, cy) = (p[0] - side * ang.sin() * off, p[1] + side * ang.cos() * off);
                    let radius = rad(&mut rng, cfg.at("radius_m").f2());
                    let stretch = rad(&mut rng, cfg.at("stretch").f2());
                    let b = blob(&mut rng, cx, cy, radius, 0.35, stretch, cfg.at("vertices").i() as usize, Some(ang));
                    let slot = if rng.f() < cfg.at("gravel_share").f() { 0 } else { 1 };
                    out[slot].extend(geom::polys(&b));
                }
                s += rad(&mut rng, step);
            }
            out
        })
        .collect();
    let mut out: [Vec<PolyC>; 2] = [vec![], vec![]];
    for e in per_edge {
        let [g, d] = e;
        out[0].extend(g);
        out[1].extend(d);
    }
    out
}

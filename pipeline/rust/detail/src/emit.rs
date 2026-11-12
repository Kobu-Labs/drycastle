//! Clutter instances: the rows a recipe returns, and ``_emit`` (terrain test, height, turn, scale, tint).

use crate::cfg::Cfg;
use crate::ctx::{Ctx, Shared};
use crate::geom::{self, Pt};
use crate::index::DistIdx;
use crate::noise;
use crate::util::point_rng;
use std::f64::consts::TAU;

/// The cell the tone noise is laid out in (meadow.py reads it per 2 m cell).
pub const CELL_M: f64 = 2.0;

#[derive(Clone, Debug)]
pub struct Clutter {
    pub kind: u16,
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub rot: f64,
    pub scale: f64,
    /// -1: no tint.
    pub tint: i16,
}

/// What a recipe returns for one context (or one tile of it).
#[derive(Default)]
pub struct Res {
    pub patches: Vec<crate::patches::Patch>,
    pub clutter: Vec<Clutter>,
}

pub fn r3(v: f64) -> f64 {
    (v * 1000.0).round() / 1000.0
}

/// Tint shift per point: fresher beside fences, walls and water.
pub struct Tint {
    edges: DistIdx,
    water: DistIdx,
    fresh_m: f64,
    fence_fresh: f64,
    water_m: f64,
    water_fresh: f64,
}

impl Tint {
    pub fn new(ctx: &Ctx, cfg: Cfg) -> Tint {
        let t = cfg.at("tint");
        let mut lines: Vec<Vec<Pt>> = ctx.fences.iter().map(geom::coords).collect();
        for b in &ctx.buildings {
            for p in geom::polys(b) {
                lines.push(p.ext);
            }
        }
        let water = match &ctx.water {
            Some(w) => DistIdx::new(&[], &geom::polys(w)),
            None => DistIdx::empty(),
        };
        Tint {
            edges: DistIdx::new(&lines, &[]),
            water,
            fresh_m: t.at("fresh_m").f(),
            fence_fresh: t.at("fence_fresh").f(),
            water_m: t.at("water_m").f(),
            water_fresh: t.at("water_fresh").f(),
        }
    }

    pub fn bias(&self, x: f64, y: f64) -> f64 {
        let mut b = 0.0;
        if !self.edges.is_empty() && self.edges.dist(x, y) < self.fresh_m {
            b += self.fence_fresh;
        }
        if !self.water.is_empty() {
            b += self.water_fresh * (1.0 - self.water.dist(x, y) / self.water_m).clamp(0.0, 1.0);
        }
        b
    }
}

pub struct Emitter<'a> {
    pub sh: &'a Shared,
    pub out: Vec<Clutter>,
}

impl<'a> Emitter<'a> {
    pub fn new(sh: &'a Shared) -> Emitter<'a> {
        Emitter { sh, out: vec![] }
    }

    pub fn kind_index(&self, kind: &str) -> u16 {
        self.sh.kinds.iter().position(|k| k == kind).unwrap_or_else(|| panic!("unknown clutter kind {kind}")) as u16
    }

    /// Instances of ``kind`` at ``pts`` with a random turn and a scale in ``span`` (points off the meshed terrain are
    /// dropped). A tintable kind also gets its tint byte (``tint``: the bias; none = no bias).
    pub fn emit(&mut self, kind: &str, seed: u64, pts: &[Pt], span: [f64; 2], tint: Option<&Tint>) {
        let ki = self.kind_index(kind);
        let tintable = self.sh.tintable.iter().any(|k| k == kind);
        for p in pts {
            if !self.sh.terrain.ok(p[0], p[1]) {
                continue;
            }
            let mut r = point_rng(seed, p[0], p[1]);
            let rot = r.f() * TAU;
            let scale = span[0] + r.f() * (span[1] - span[0]);
            let mut t = -1;
            if tintable {
                let bias = tint.map_or(0.0, |t| t.bias(p[0], p[1]));
                t = noise::tint_byte(noise::tone_noise(p[0] / CELL_M, p[1] / CELL_M), r.f(), bias) as i16;
            }
            self.out.push(Clutter {
                kind: ki,
                x: r3(p[0]),
                y: r3(p[1]),
                z: r3(self.sh.terrain.surface(p[0], p[1])),
                rot: r3(rot),
                scale: r3(scale),
                tint: t,
            });
        }
    }
}

//! Small shared tools: polylines with arc length, blobs, the jittered lattice every scatter runs on.

use crate::geom::{self, Pt, G};
use crate::rng::{mix3, Rng};
use std::f64::consts::{PI, TAU};

pub struct Polyline {
    pub pts: Vec<Pt>,
    cum: Vec<f64>,
}

impl Polyline {
    pub fn new(pts: Vec<Pt>) -> Polyline {
        let mut cum = Vec::with_capacity(pts.len());
        let mut s = 0.0;
        for (i, p) in pts.iter().enumerate() {
            if i > 0 {
                s += ((p[0] - pts[i - 1][0]).powi(2) + (p[1] - pts[i - 1][1]).powi(2)).sqrt();
            }
            cum.push(s);
        }
        Polyline { pts, cum }
    }

    pub fn length(&self) -> f64 {
        self.cum.last().copied().unwrap_or(0.0)
    }

    /// The point ``s`` metres along the line (clamped to its ends).
    pub fn at(&self, s: f64) -> Pt {
        if self.pts.is_empty() {
            return [0.0, 0.0];
        }
        if s <= 0.0 || self.pts.len() == 1 {
            return self.pts[0];
        }
        if s >= self.length() {
            return *self.pts.last().unwrap();
        }
        let i = self.cum.partition_point(|&c| c <= s).clamp(1, self.pts.len() - 1);
        let (a, b) = (self.pts[i - 1], self.pts[i]);
        let seg = self.cum[i] - self.cum[i - 1];
        let t = if seg > 0.0 { (s - self.cum[i - 1]) / seg } else { 0.0 };
        [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]
    }
}

/// A smooth irregular blob: radius ``radius`` times (1 + wobble * noise) round the angle, stretched along ``axis`` (random
/// without one) by ``stretch``. The noise is three random harmonics.
pub fn blob(rng: &mut Rng, cx: f64, cy: f64, radius: f64, wobble: f64, stretch: f64, n: usize, axis: Option<f64>) -> G {
    let mut harm = [(0.0, 0.0, 0.0); 3];
    for (h, k) in harm.iter_mut().zip([2.0, 3.0, 5.0]) {
        *h = (k, rng.f() * TAU, 1.0 / k);
    }
    let norm: f64 = harm.iter().map(|h| h.2).sum();
    let axis = axis.unwrap_or_else(|| rng.f() * PI);
    let (ca, sa) = (axis.cos(), axis.sin());
    let pts: Vec<Pt> = (0..n)
        .map(|i| {
            let t = TAU * i as f64 / n as f64;
            let r = radius * (1.0 + wobble * harm.iter().map(|&(k, ph, a)| a * (k * t + ph).sin()).sum::<f64>() / norm);
            let (x, y) = (r * t.cos() * stretch, r * t.sin());
            [cx + x * ca - y * sa, cy + x * sa + y * ca]
        })
        .collect();
    geom::polygon_fixed(&pts)
}

/// A value in ``span`` (``rng.random()`` scaled).
pub fn rad(rng: &mut Rng, span: [f64; 2]) -> f64 {
    span[0] + rng.f() * (span[1] - span[0])
}

/// A cheap deterministic uniform in [0, 1) of a lattice cell (no generator set up).
#[inline]
pub fn cell_u(seed: u64, i: i64, j: i64) -> f64 {
    (mix3(seed, i as u64, j as u64) >> 11) as f64 / (1u64 << 53) as f64
}

/// Candidate points of a jittered lattice of ``spacing`` metres inside ``bbox`` ([x0, y0, x1, y1), half open): one per
/// cell, jittered inside it (``jitter`` of the cell), kept when ``accept(x, y, u)`` holds, where ``u`` is the cell's
/// uniform draw. Passing ``u`` lets a caller thin by a density with a single draw: keep when ``u < density``. ``vmax`` is
/// an upper bound of the density: cells with ``u >= vmax`` are dropped before anything else is computed.
///
/// The lattice is anchored at the origin and every cell has its own stream, so two boxes that meet agree on the points
/// along their border, whatever the order or thread.
pub fn lattice(seed: u64, spacing: f64, bbox: [f64; 4], vmax: f64, accept: impl FnMut(f64, f64, f64) -> bool) -> Vec<Pt> {
    lattice_in(seed, spacing, bbox, vmax, None, accept)
}

/// :func:`lattice` that skips whole tiles of cells the ``region`` surely does not touch (``accept`` must be false outside it).
pub fn lattice_in(
    seed: u64, spacing: f64, bbox: [f64; 4], vmax: f64, region: Option<&crate::index::Region>, mut accept: impl FnMut(f64, f64, f64) -> bool,
) -> Vec<Pt> {
    const TILE: i64 = 16;
    let mut out = vec![];
    if spacing <= 0.0 || vmax <= 0.0 || bbox[0] >= bbox[2] || bbox[1] >= bbox[3] {
        return out;
    }
    let (i0, i1) = ((bbox[0] / spacing).floor() as i64 - 1, (bbox[2] / spacing).ceil() as i64 + 1);
    let (j0, j1) = ((bbox[1] / spacing).floor() as i64 - 1, (bbox[3] / spacing).ceil() as i64 + 1);
    let mut tj = j0;
    while tj <= j1 {
        let mut ti = i0;
        while ti <= i1 {
            if let Some(r) = region {
                let tb = [(ti as f64 - 1.0) * spacing, (tj as f64 - 1.0) * spacing, ((ti + TILE) as f64 + 1.0) * spacing, ((tj + TILE) as f64 + 1.0) * spacing];
                if !r.may_touch(tb) {
                    ti += TILE;
                    continue;
                }
            }
            for j in tj..(tj + TILE).min(j1 + 1) {
                for i in ti..(ti + TILE).min(i1 + 1) {
                    let u = cell_u(seed, i, j);
                    if u >= vmax {
                        continue;
                    }
                    let mut r = Rng::cell(seed ^ 0x5851_F42D_4C95_7F2D, i, j);
                    let x = (i as f64 + 0.5 + (r.f() - 0.5) * 0.9) * spacing;
                    let y = (j as f64 + 0.5 + (r.f() - 0.5) * 0.9) * spacing;
                    if x < bbox[0] || x >= bbox[2] || y < bbox[1] || y >= bbox[3] {
                        continue;
                    }
                    if accept(x, y, u) {
                        out.push([x, y]);
                    }
                }
            }
            ti += TILE;
        }
        tj += TILE;
    }
    out
}

/// The stream of one point, derived from its position (so that the draws after the point do not depend on the others).
pub fn point_rng(seed: u64, x: f64, y: f64) -> Rng {
    Rng::new(mix3(seed, x.to_bits(), y.to_bits()))
}

pub fn in_box(b: [f64; 4], x: f64, y: f64) -> bool {
    x >= b[0] && x < b[2] && y >= b[1] && y < b[3]
}

pub fn grow(b: [f64; 4], m: f64) -> [f64; 4] {
    [b[0] - m, b[1] - m, b[2] + m, b[3] + m]
}

pub fn boxes_meet(a: [f64; 4], b: [f64; 4]) -> bool {
    a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1]
}

#[cfg(test)]
mod tests {
    use super::*;
    use geos::Geom;

    #[test]
    fn polyline_interpolates() {
        let l = Polyline::new(vec![[0.0, 0.0], [10.0, 0.0], [10.0, 10.0]]);
        assert_eq!(l.length(), 20.0);
        assert_eq!(l.at(5.0), [5.0, 0.0]);
        assert_eq!(l.at(15.0), [10.0, 5.0]);
        assert_eq!(l.at(99.0), [10.0, 10.0]);
    }

    #[test]
    fn lattice_boxes_agree_on_their_border() {
        // the points of two adjacent boxes are exactly the points of the box that spans both
        let (a, b) = ([0.0, 0.0, 30.0, 40.0], [30.0, 0.0, 60.0, 40.0]);
        let whole = lattice(9, 0.7, [0.0, 0.0, 60.0, 40.0], 0.3, |_, _, _| true);
        let mut halves = lattice(9, 0.7, a, 0.3, |_, _, _| true);
        halves.extend(lattice(9, 0.7, b, 0.3, |_, _, _| true));
        let key = |p: &Pt| (p[0].to_bits(), p[1].to_bits());
        let mut w: Vec<_> = whole.iter().map(key).collect();
        let mut h: Vec<_> = halves.iter().map(key).collect();
        w.sort();
        h.sort();
        assert_eq!(w, h);
        // density: about vmax of the cells
        let cells = 60.0 * 40.0 / 0.49;
        assert!((whole.len() as f64 / cells - 0.3).abs() < 0.03);
    }

    #[test]
    fn blob_is_a_valid_polygon_of_about_the_radius() {
        let mut r = Rng::new(5);
        let b = blob(&mut r, 10.0, 20.0, 2.0, 0.3, 1.0, 14, None);
        let a = geom::area(&b);
        assert!(a > 6.0 && a < 20.0, "{a}");
        assert!(b.is_valid().unwrap());
    }
}

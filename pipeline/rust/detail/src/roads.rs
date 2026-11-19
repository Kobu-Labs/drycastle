//! Road ribbons and junction patches laid on the terrain: `roads.drape_mesh`, one footprint per task on all cores.
//!
//! Operation by operation what the Python does (the cells in its order, the same GEOS intersection of the footprint with each
//! terrain triangle, ear clipping, the same sliver rule and plane arithmetic), so the triangles are the Python's. A footprint with
//! a piece the ear clipper here does not cover (a hole, over 80 vertices) is handed back: Python drapes that one itself.

use crate::geom::{self, G};
use crate::terrain::Terrain;
use geos::Geom;
use rayon::prelude::*;

const MIN_PIECE_AREA: f64 = 1e-6; // meshes._MIN_PIECE_AREA
const SLIVER_AREA2: f64 = 2e-6;

/// Ear clipping of one ring without holes, ear by ear in the order of the reference's triangulator (`mapbox_earcut`: after an ear
/// the walk goes on at the next vertex), so the triangles are the reference's. False (nothing added) for what this does not
/// cover: over 80 vertices (the reference then hashes) or a ring that needs more than the removal of flat corners.
fn ear_clip(ring: &[[f64; 2]], out: &mut Vec<[[f64; 2]; 3]>) -> bool {
    let n = if ring.len() > 1 && ring.first() == ring.last() { ring.len() - 1 } else { ring.len() };
    if n > 80 {
        return false;
    }
    if n < 3 {
        return true;
    }
    let p = &ring[..n];
    let mut sum = 0.0;
    let mut j = n - 1;
    for i in 0..n {
        sum += (p[j][0] - p[i][0]) * (p[i][1] + p[j][1]);
        j = i;
    }
    // the linked list, in the winding the clipper wants; `last` is the vertex put in last
    let order: Vec<usize> = if sum > 0.0 { (0..n).collect() } else { (0..n).rev().collect() };
    let (mut prev, mut next) = (vec![0usize; n], vec![0usize; n]);
    for k in 0..n {
        next[order[k]] = order[(k + 1) % n];
        prev[order[(k + 1) % n]] = order[k];
    }
    let mut last = order[n - 1];
    let area = |a: usize, b: usize, c: usize| (p[b][1] - p[a][1]) * (p[c][0] - p[b][0]) - (p[b][0] - p[a][0]) * (p[c][1] - p[b][1]);
    macro_rules! remove {
        ($i:expr) => {{
            let (a, b) = (prev[$i], next[$i]);
            next[a] = b;
            prev[b] = a;
        }};
    }
    if p[last] == p[next[last]] {
        remove!(last);
        last = next[last];
    }
    let inside = |a: usize, b: usize, c: usize, q: usize| {
        let (ax, ay, bx, by, cx, cy, px, py) = (p[a][0], p[a][1], p[b][0], p[b][1], p[c][0], p[c][1], p[q][0], p[q][1]);
        (cx - px) * (ay - py) - (ax - px) * (cy - py) >= 0.0 && (ax - px) * (by - py) - (bx - px) * (ay - py) >= 0.0 && (bx - px) * (cy - py) - (cx - px) * (by - py) >= 0.0
    };
    let mut tris = vec![];
    let mut ear = last;
    for pass in 0..2 {
        let mut stop = ear;
        let mut stuck = false;
        while prev[ear] != next[ear] {
            let (a, c) = (prev[ear], next[ear]);
            let mut is_ear = area(a, ear, c) < 0.0;
            if is_ear {
                let mut q = next[c];
                while q != a {
                    if inside(a, ear, c, q) && area(prev[q], q, next[q]) >= 0.0 {
                        is_ear = false;
                        break;
                    }
                    q = next[q];
                }
            }
            if is_ear {
                tris.push([p[a], p[ear], p[c]]);
                remove!(ear);
                ear = c;
                stop = c;
                continue;
            }
            ear = c;
            if ear == stop {
                stuck = true;
                break;
            }
        }
        if !stuck {
            out.extend(tris);
            return true;
        }
        if pass == 1 {
            return false;
        }
        // no ear left: drop the repeated points and the flat corners, then once more
        let (mut q, mut end) = (ear, ear);
        loop {
            let mut again = false;
            if p[q] == p[next[q]] || area(prev[q], q, next[q]) == 0.0 {
                remove!(q);
                q = prev[q];
                end = q;
                if q == next[q] {
                    break;
                }
                again = true;
            } else {
                q = next[q];
            }
            if !again && q == end {
                break;
            }
        }
        ear = end;
    }
    false
}

/// The triangles (9 numbers each: x y z of three corners, counter-clockwise from above) of one footprint, `lift` above the terrain.
/// None: a piece of it is not covered here (see [`ear_clip`]) or GEOS failed; the caller drapes it with the reference.
pub fn drape_foot(foot: &G, t: &Terrain, lift: f64) -> Option<Vec<f64>> {
    let mut out = vec![];
    if geom::is_empty(foot) {
        return Some(out);
    }
    let b = geom::bounds(foot);
    let st = t.step;
    let lo = |v: f64, o: i64| ((v / st).floor() as i64 - o).max(0);
    let (c0, c1) = (lo(b[0], t.i0), ((b[2] / st).floor() as i64 - t.i0).min(t.ncols as i64 - 1));
    let (r0, r1) = (lo(b[1], t.j0), ((b[3] / st).floor() as i64 - t.j0).min(t.nrows as i64 - 1));
    let prepared = foot.to_prepared_geom().ok()?;
    for cj in r0..=r1 {
        for ci in c0..=c1 {
            let (x, y) = ((t.i0 + ci) as f64 * st, (t.j0 + cj) as f64 * st);
            if !prepared.intersects(&geom::rect(x, y, x + st, y + st)).ok()? {
                continue;
            }
            let (ci, cj) = (ci as usize, cj as usize);
            if !t.cell_valid(ci, cj) {
                continue;
            }
            let v00 = [x, y, t.height(cj, ci)];
            let v10 = [x + st, y, t.height(cj, ci + 1)];
            let v01 = [x, y + st, t.height(cj + 1, ci)];
            let v11 = [x + st, y + st, t.height(cj + 1, ci + 1)];
            let tris = if (v10[2] - v01[2]).abs() < (v00[2] - v11[2]).abs() { [[v00, v10, v01], [v10, v11, v01]] } else { [[v00, v10, v11], [v00, v11, v01]] };
            for [a, bb, c] in tris {
                let tri = geom::polygon(&[[a[0], a[1]], [bb[0], bb[1]], [c[0], c[1]]], &[]);
                let piece = foot.intersection(&tri).ok()?;
                if geom::is_empty(&piece) {
                    continue;
                }
                let det = (bb[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (bb[1] - a[1]);
                if det.abs() < 1e-12 {
                    continue;
                }
                let gx = ((bb[2] - a[2]) * (c[1] - a[1]) - (c[2] - a[2]) * (bb[1] - a[1])) / det;
                let gy = ((c[2] - a[2]) * (bb[0] - a[0]) - (bb[2] - a[2]) * (c[0] - a[0])) / det;
                for pg in geom::polys_g(&piece) {
                    if geom::area(&pg) <= MIN_PIECE_AREA {
                        continue;
                    }
                    let mut flat = vec![];
                    for pc in geom::polys(&pg) {
                        if !(pc.holes.is_empty() && ear_clip(&pc.ext, &mut flat)) {
                            return None;
                        }
                    }
                    for p in flat {
                        let area2 = (p[1][0] - p[0][0]) * (p[2][1] - p[0][1]) - (p[2][0] - p[0][0]) * (p[1][1] - p[0][1]);
                        if area2.abs() < SLIVER_AREA2 {
                            continue; // sliver from clipping along a terrain edge
                        }
                        let v = |q: [f64; 2]| [q[0], q[1], a[2] + gx * (q[0] - a[0]) + gy * (q[1] - a[1]) + lift];
                        let order = if area2 < 0.0 { [0, 2, 1] } else { [0, 1, 2] };
                        for k in order {
                            out.extend_from_slice(&v(p[k]));
                        }
                    }
                }
            }
        }
    }
    Some(out)
}

/// Every footprint (WKB, with its lift) draped, in the order given; None where [`drape_foot`] gives None.
pub fn drape_feet(feet: &[(&[u8], f64)], t: &Terrain) -> Vec<Option<Vec<f64>>> {
    feet.par_iter().map(|(wkb, lift)| drape_foot(&geom::from_wkb(wkb), t, *lift)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn flat_terrain() -> Terrain {
        Terrain::new(8.0, 4, 0, 0, 5, 5, vec![10.0; 25], vec![true; 25])
    }

    #[test]
    fn ear_clip_fans_a_convex_ring_like_the_reference() {
        // the reference on this clockwise pentagon: [1, 0, 4], [1, 4, 3], [1, 3, 2]
        let ring = [[2.0, 0.0], [1.0, 0.0], [0.0, 2.0], [1.5, 3.0], [3.0, 1.5]];
        let mut out = vec![];
        assert!(ear_clip(&ring, &mut out));
        assert_eq!(out, vec![[ring[1], ring[0], ring[4]], [ring[1], ring[4], ring[3]], [ring[1], ring[3], ring[2]]]);
    }

    #[test]
    fn a_footprint_covers_its_area_and_lies_on_the_terrain() {
        let t = flat_terrain();
        let foot = geom::polygon(&[[0.5, 0.5], [5.5, 0.7], [5.2, 3.4], [0.7, 3.1]], &[]);
        let tris = drape_foot(&foot, &t, 0.25).unwrap();
        let mut area = 0.0;
        for v in tris.chunks_exact(9) {
            let a2 = (v[3] - v[0]) * (v[7] - v[1]) - (v[6] - v[0]) * (v[4] - v[1]);
            assert!(a2 > 0.0); // counter-clockwise from above
            assert!((v[2] - 10.25).abs() < 1e-12 && (v[5] - 10.25).abs() < 1e-12 && (v[8] - 10.25).abs() < 1e-12);
            area += a2 / 2.0;
        }
        assert!((area - geom::area(&foot)).abs() < 1e-6);
    }

    #[test]
    fn a_footprint_with_a_hole_is_handed_back() {
        let t = flat_terrain();
        let foot = geom::polygon(&[[0.2, 0.2], [1.8, 0.2], [1.8, 1.8], [0.2, 1.8]], &[vec![[0.9, 0.5], [1.5, 0.5], [1.5, 0.8]]]);
        assert!(drape_foot(&foot, &t, 0.0).is_none());
    }
}

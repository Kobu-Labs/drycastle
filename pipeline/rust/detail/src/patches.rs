//! Patches: the layers clipped in priority order (``_assemble``), and their draping on the terrain (``regions.drape``).

use crate::geom::{self, Pt, G};
use crate::rng::Rng;
use crate::terrain::Terrain;
use std::collections::BTreeMap;

pub const PATCH_PRIORITY: [&str; 11] =
    ["dike_track", "gravel", "verge_gravel", "bed", "furrow", "dirt", "verge_dirt", "litter", "tone_a", "tone_b", "lawn"];
const SOFT_LAYERS: [&str; 4] = ["lawn", "tone_a", "tone_b", "litter"];
pub const MIN_PIECE_M2: f64 = 0.15;
pub const PATCH_ROW: usize = 19;

pub fn texture_of(layer: &str) -> Option<&'static str> {
    match layer {
        "dike_track" => Some("dike_gravel"),
        "gravel" | "verge_gravel" => Some("gravel"),
        "bed" | "furrow" => Some("soil_rows"),
        "dirt" | "verge_dirt" => Some("dirt"),
        "litter" => Some("leaf_litter"),
        _ => None,
    }
}

pub struct Patch {
    pub layer: &'static str,
    pub geom: G,
    pub colour: Option<[u8; 3]>,
    pub texture: Option<&'static str>,
    pub angle: f64,
    pub origin: Pt,
    pub lift: Option<f64>,
}

fn layer_name(l: &str) -> &'static str {
    PATCH_PRIORITY.iter().find(|p| **p == l).copied().unwrap_or("lawn")
}

/// Simplified, without slivers: fewer triangles for the same look.
pub fn clean(g: &G, tol: f64) -> G {
    let mut keep = vec![];
    for p in geom::polys_g(g) {
        if geom::area(&p) < MIN_PIECE_M2 {
            continue;
        }
        let p = if tol > 0.0 { geom::simplify(&p, tol) } else { p };
        for q in geom::polys_g(&p) {
            if !geom::is_empty(&q) && geom::area(&q) >= MIN_PIECE_M2 {
                keep.push(q);
            }
        }
    }
    geom::union_all(keep)
}

pub struct Flat<'a> {
    pub lawn: &'a [[u8; 3]],
    pub tone_a: Option<[u8; 3]>,
    pub tone_b: Option<[u8; 3]>,
}

pub struct Rect {
    pub rect: G,
    pub angle: f64,
    pub centre: Pt,
}

/// Clips ``layers`` to the free ground in priority order (an earlier layer keeps what it covers) and makes the patches.
/// Returns the clipped layers by name.
pub fn assemble(
    patches: &mut Vec<Patch>, free: &G, layers: &BTreeMap<&'static str, G>, flat: &Flat, rects: &BTreeMap<&'static str, Vec<Rect>>,
    rng: &mut Rng,
) -> BTreeMap<&'static str, G> {
    let mut taken = geom::empty();
    let mut out: BTreeMap<&'static str, G> = BTreeMap::new();
    for layer in PATCH_PRIORITY {
        let Some(g) = layers.get(layer) else { continue };
        if geom::is_empty(g) {
            continue;
        }
        // simplify the layer's own shape first, then cut it back to the free ground and to what earlier layers took
        let tol = if SOFT_LAYERS.contains(&layer) { 0.25 } else { 0.06 };
        let mut g = geom::inter(&clean(&geom::inter(g, free), tol), free);
        if !geom::is_empty(&taken) {
            g = geom::diff(&g, &taken);
        }
        let g = clean(&g, 0.0);
        if geom::is_empty(&g) {
            continue;
        }
        taken = geom::union2(&taken, &g);
        out.insert(layer_name(layer), g);
    }
    for layer in PATCH_PRIORITY {
        let Some(g) = out.get(layer) else { continue };
        match layer {
            "lawn" => {
                for poly in geom::polys_g(g) {
                    let c = flat.lawn[rng.below(flat.lawn.len())];
                    patches.push(Patch { layer: "lawn", geom: poly, colour: Some(c), texture: None, angle: 0.0, origin: [0.0, 0.0], lift: None });
                }
            }
            "tone_a" | "tone_b" => {
                let c = if layer == "tone_a" { flat.tone_a } else { flat.tone_b };
                patches.push(Patch { layer: layer_name(layer), geom: geom::dup(g), colour: c, texture: None, angle: 0.0, origin: [0.0, 0.0], lift: None });
            }
            "bed" | "furrow" => {
                if let Some(rs) = rects.get(layer) {
                    for r in rs {
                        let piece = geom::inter(g, &geom::buffer(&r.rect, 0.01, 8));
                        if !geom::is_empty(&piece) {
                            patches.push(Patch {
                                layer: layer_name(layer), geom: piece, colour: None, texture: texture_of(layer), angle: r.angle, origin: r.centre, lift: None,
                            });
                        }
                    }
                }
            }
            _ => patches.push(Patch {
                layer: layer_name(layer), geom: geom::dup(g), colour: None, texture: texture_of(layer), angle: 0.0, origin: [0.0, 0.0], lift: None,
            }),
        }
    }
    out
}

// --- draping ---------------------------------------------------------------------------------------------------

pub type Tri = [Pt; 3];

fn triangulate(p: &geom::PolyC, out: &mut Vec<Tri>) {
    let mut data: Vec<f64> = vec![];
    let mut holes: Vec<usize> = vec![];
    let ring = |r: &Vec<Pt>, data: &mut Vec<f64>| {
        let n = if r.len() > 1 && r.first() == r.last() { r.len() - 1 } else { r.len() };
        for q in &r[..n] {
            data.push(q[0]);
            data.push(q[1]);
        }
    };
    ring(&p.ext, &mut data);
    for h in &p.holes {
        holes.push(data.len() / 2);
        ring(h, &mut data);
    }
    let Ok(idx) = earcutr::earcut(&data, &holes, 2) else { return };
    for t in idx.chunks_exact(3) {
        let v = |i: usize| [data[2 * i], data[2 * i + 1]];
        out.push([v(t[0]), v(t[1]), v(t[2])]);
    }
}

fn drape_cell(g: &G, ci: usize, cj: usize, t: &Terrain, out: &mut Vec<Tri>) {
    if !t.cell_valid(ci, cj) {
        return;
    }
    let b = t.cell_box(ci, cj);
    let piece = geom::clip_rect(g, b[0], b[1], b[2], b[3]);
    if geom::is_empty(&piece) {
        return;
    }
    let cell_area = t.step * t.step;
    let tris = t.cell_triangles(ci, cj);
    if geom::area(&piece) >= cell_area * (1.0 - 1e-9) {
        out.extend_from_slice(&tris);
        return;
    }
    for tri in tris {
        let tg = geom::polygon(&tri, &[]);
        let part = geom::inter(&piece, &tg);
        for p in geom::polys(&part) {
            triangulate(&p, out);
        }
    }
}

fn drape_rec(g: &G, r: (usize, usize, usize, usize), t: &Terrain, out: &mut Vec<Tri>) {
    if geom::is_empty(g) {
        return;
    }
    let (ci0, ci1, cj0, cj1) = r;
    if ci0 == ci1 && cj0 == cj1 {
        drape_cell(g, ci0, cj0, t, out);
        return;
    }
    let (nx, ny) = (ci1 - ci0 + 1, cj1 - cj0 + 1);
    if nx >= ny {
        let m = ci0 + nx / 2; // left: ci0..m-1, right: m..ci1
        let x = t.cell_box(m, cj0)[0];
        let (y0, y1) = (t.cell_box(ci0, cj0)[1], t.cell_box(ci0, cj1)[3]);
        let (xa, xb) = (t.cell_box(ci0, cj0)[0], t.cell_box(ci1, cj0)[2]);
        drape_rec(&geom::clip_rect(g, xa, y0, x, y1), (ci0, m - 1, cj0, cj1), t, out);
        drape_rec(&geom::clip_rect(g, x, y0, xb, y1), (m, ci1, cj0, cj1), t, out);
    } else {
        let m = cj0 + ny / 2;
        let y = t.cell_box(ci0, m)[1];
        let (x0, x1) = (t.cell_box(ci0, cj0)[0], t.cell_box(ci1, cj0)[2]);
        let (ya, yb) = (t.cell_box(ci0, cj0)[1], t.cell_box(ci0, cj1)[3]);
        drape_rec(&geom::clip_rect(g, x0, ya, x1, y), (ci0, ci1, cj0, m - 1), t, out);
        drape_rec(&geom::clip_rect(g, x0, y, x1, yb), (ci0, ci1, m, cj1), t, out);
    }
}

/// Triangles (xy) covering ``g`` on the meshed cells of the terrain, each inside one terrain triangle.
pub fn drape_xy(g: &G, t: &Terrain) -> Vec<Tri> {
    let mut out = vec![];
    if geom::is_empty(g) {
        return out;
    }
    if let Some(r) = t.cell_range(geom::bounds(g)) {
        drape_rec(g, r, t, &mut out);
    }
    out
}

/// A triangle lifted onto the terrain and wound counter-clockwise from above; None for a sliver.
pub fn lift_tri(t: &Terrain, tri: &Tri, lift: f64) -> Option<[[f64; 3]; 3]> {
    let mut v = [[0.0; 3]; 3];
    for (k, p) in tri.iter().enumerate() {
        v[k] = [p[0], p[1], t.surface(p[0], p[1]) + lift];
    }
    let cz = (v[1][0] - v[0][0]) * (v[2][1] - v[0][1]) - (v[1][1] - v[0][1]) * (v[2][0] - v[0][0]);
    if cz.abs() <= 1e-12 {
        return None;
    }
    if cz < 0.0 {
        v.swap(1, 2);
    }
    Some(v)
}

pub struct Textures<'a> {
    pub tile: &'a dyn Fn(&str) -> (f64, f64),
    pub index: &'a dyn Fn(&str) -> f64,
}

/// The rows (``PATCH_ROW`` numbers each) of the patches' triangles; adds the triangle count and area per layer to ``tally``.
pub fn patch_rows(patches: &[Patch], t: &Terrain, lift_m: f64, tex: &Textures, rows: &mut Vec<f64>, tally: &mut BTreeMap<&'static str, (usize, f64, usize)>) {
    for p in patches {
        let lift = p.lift.unwrap_or(lift_m);
        let tris = drape_xy(&p.geom, t);
        let e = tally.entry(p.layer).or_insert((0, 0.0, 0));
        e.1 += geom::area(&p.geom);
        e.2 += 1;
        let (tw, th) = p.texture.map(|n| (tex.tile)(n)).unwrap_or((1.0, 1.0));
        let (c, s) = ((-p.angle).cos(), (-p.angle).sin());
        for tri in &tris {
            let Some(v) = lift_tri(t, tri, lift) else { continue };
            e.0 += 1;
            for q in &v {
                rows.extend_from_slice(q);
            }
            match p.texture {
                None => {
                    let col = p.colour.unwrap_or([128, 128, 128]);
                    rows.extend([col[0] as f64, col[1] as f64, col[2] as f64]);
                    rows.extend([0.0; 6]);
                    rows.push(-1.0);
                }
                Some(name) => {
                    rows.extend([255.0, 255.0, 255.0]);
                    for q in &v {
                        let (dx, dy) = (q[0] - p.origin[0], q[1] - p.origin[1]);
                        rows.push((dx * c - dy * s) / tw);
                        rows.push((dx * s + dy * c) / th);
                    }
                    rows.push((tex.index)(name));
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn flat_terrain() -> Terrain {
        // 4 x 4 cells of 10 m on the plane z = x / 10; origin cell index 0
        let (rows, cols) = (5, 5);
        let mut h = vec![];
        for _r in 0..rows {
            for c in 0..cols {
                h.push(c as f64 * 1.0);
            }
        }
        Terrain::new(40.0, 4, 0, 0, rows, cols, h, vec![true; rows * cols])
    }

    fn area_of(tris: &[Tri]) -> f64 {
        tris.iter().map(|t| ((t[1][0] - t[0][0]) * (t[2][1] - t[0][1]) - (t[2][0] - t[0][0]) * (t[1][1] - t[0][1])).abs() / 2.0).sum()
    }

    #[test]
    fn draping_covers_exactly_the_polygon() {
        let t = flat_terrain();
        // a polygon spanning several cells with a hole: the triangles must tile it exactly
        let g = geom::polygon(
            &[[3.0, 4.0], [33.0, 6.0], [31.0, 28.0], [5.0, 31.0]],
            &[vec![[12.0, 12.0], [20.0, 12.0], [20.0, 19.0], [12.0, 19.0]]],
        );
        let tris = drape_xy(&g, &t);
        assert!((area_of(&tris) - geom::area(&g)).abs() < 1e-6, "{} vs {}", area_of(&tris), geom::area(&g));
        // every triangle lies inside one terrain triangle: its vertices' surface heights are coplanar with its centroid
        for tri in &tris {
            let v = lift_tri(&t, tri, 0.02).expect("no sliver here");
            let cx = (v[0][0] + v[1][0] + v[2][0]) / 3.0;
            let cy = (v[0][1] + v[1][1] + v[2][1]) / 3.0;
            let cz = (v[0][2] + v[1][2] + v[2][2]) / 3.0;
            assert!((cz - (t.surface(cx, cy) + 0.02)).abs() < 1e-9);
            // counter-clockwise from above
            assert!((v[1][0] - v[0][0]) * (v[2][1] - v[0][1]) - (v[1][1] - v[0][1]) * (v[2][0] - v[0][0]) > 0.0);
        }
        // a whole cell becomes its two triangles
        let cell = geom::rect(10.0, 10.0, 20.0, 20.0);
        assert_eq!(drape_xy(&cell, &t).len(), 2);
    }

    #[test]
    fn no_triangles_outside_meshed_cells() {
        let mut t = flat_terrain();
        // invalidate the corner vertex (0, 0): the cell (0, 0) is no longer meshed
        t.valid[0] = false;
        let g = geom::rect(1.0, 1.0, 9.0, 9.0);
        assert!(drape_xy(&g, &t).is_empty());
        let g = geom::rect(1.0, 1.0, 19.0, 9.0);
        assert!((area_of(&drape_xy(&g, &t)) - 9.0 * 8.0).abs() < 1e-6); // only the part over cell (1, 0): x 10..19
    }

    #[test]
    fn clean_drops_slivers_and_keeps_the_rest() {
        let a = geom::rect(0.0, 0.0, 0.2, 0.2);
        let b = geom::rect(5.0, 5.0, 8.0, 8.0);
        let c = clean(&geom::union_all(vec![a, b]), 0.1);
        assert!((geom::area(&c) - 9.0).abs() < 1e-9);
    }
}

//! The meadow raster (meadow.py): per cell the density of each layer of ground clutter and the zone byte, from the free
//! ground of a unit and its patches; and the ``meadow.bin`` file.

use crate::cfg::Cfg;
use crate::ctx::Ctx;
use crate::geom::{self, Pt, G};
use crate::index::{DistIdx, Region};
use crate::noise::{clump, hash01, value_noise, voronoi01};
use crate::patches::Patch;
use crate::util;
use flate2::{write::ZlibEncoder, Compression};
use std::collections::BTreeMap;
use std::io::Write;

pub const MEADOW_FORMAT_VERSION: u16 = 3;
pub const MEADOW_TYPES: [&str; 2] = ["field", "forest"];
const BARE_LAYERS: [&str; 4] = ["dirt", "verge_dirt", "verge_gravel", "gravel"];
pub const DRY_STEP: f64 = 10.0;

/// The raster cells one context (or tile) contributes: global cell indices and ``layers + 1`` bytes each (zone byte last).
#[derive(Default)]
pub struct Cells {
    pub gx: Vec<i32>,
    pub gy: Vec<i32>,
    pub vals: Vec<u8>,
}

pub struct Layer {
    pub name: String,
    pub max_per_cell: f64,
    pub scale: [f64; 2],
    pub mixes: Vec<Vec<(String, f64)>>,
}

pub fn layers(mcfg: Cfg) -> Vec<Layer> {
    let mixes = mcfg.at("mixes").arr();
    let mixes = if mixes.is_empty() { vec![mcfg.at("mixes")] } else { mixes };
    mcfg.at("layers")
        .entries()
        .into_iter()
        .map(|(name, s)| {
            let per_mix = mixes
                .iter()
                .map(|m| {
                    let src = if m.has(name) && !m.at(name).arr().is_empty() { m.at(name) } else { s.at("kinds") };
                    src.shares().into_iter().map(|(k, w)| (k.to_string(), w as f32 as f64)).collect()
                })
                .collect();
            Layer { name: name.to_string(), max_per_cell: s.at("max_per_cell").f(), scale: s.at("scale").f2(), mixes: per_mix }
        })
        .collect()
}

fn smooth_inside(r: &Region, x: f64, y: f64) -> bool {
    r.contains(x, y)
}

/// Puts the cells of one context on the raster. ``free`` is its free ground, ``patches`` say where soil, litter and tones are,
/// ``tile`` (exact box) restricts the cells to those whose centre lies in it. Returns the cells.
pub fn add_cells(ctx: &Ctx, patches: &[Patch], atype: &str, free: &G, mcfg: Cfg, band: f64, tile: Option<[f64; 4]>) -> Cells {
    let mut out = Cells::default();
    if geom::is_empty(free) {
        return out;
    }
    let cell = mcfg.at("cell_m").f();
    let jit = mcfg.at("jitter_m").f();
    let [x0, y0, x1, y1] = geom::bounds(free);
    let (mut gx0, mut gx1) = ((x0 / cell).floor() as i64, (x1 / cell).ceil() as i64);
    let (mut gy0, mut gy1) = ((y0 / cell).floor() as i64, (y1 / cell).ceil() as i64);
    if let Some(t) = tile {
        // cells whose centre (gx + 0.5) * cell lies in [t0, t1)
        gx0 = gx0.max((t[0] / cell - 0.5).ceil() as i64);
        gx1 = gx1.min((t[2] / cell - 0.5).ceil() as i64);
        gy0 = gy0.max((t[1] / cell - 0.5).ceil() as i64);
        gy1 = gy1.min((t[3] / cell - 0.5).ceil() as i64);
    }
    if gx1 <= gx0 || gy1 <= gy0 {
        return out;
    }
    let free_r = Region::from_geom(free);
    let mut gx: Vec<i64> = vec![];
    let mut gy: Vec<i64> = vec![];
    for j in gy0..gy1 {
        for i in gx0..gx1 {
            let (cx, cy) = ((i as f64 + 0.5) * cell, (j as f64 + 0.5) * cell);
            if !smooth_inside(&free_r, cx, cy) {
                continue;
            }
            if [(-jit, -jit), (jit, -jit), (jit, jit), (-jit, jit)].iter().all(|(sx, sy)| free_r.contains(cx + sx, cy + sy)) {
                gx.push(i);
                gy.push(j);
            }
        }
    }
    if gx.is_empty() {
        return out;
    }
    let n = gx.len();
    let region_of = |names: &[&str]| {
        let mut ps = vec![];
        for p in patches.iter().filter(|p| names.contains(&p.layer)) {
            ps.extend(geom::polys(&p.geom));
        }
        Region::from_polycs(&ps)
    };
    let (bare_r, furrow_r, litter_r, tone_r) =
        (region_of(&BARE_LAYERS), region_of(&["furrow", "bed"]), region_of(&["litter"]), region_of(&["tone_a", "tone_b"]));
    let interior = if band > 0.0 { Some(Region::from_geom(&geom::buffer(free, -band, 16))) } else { None };
    let reach = mcfg.at("tree_reach_m").f();
    let near_tree_of = |cx: f64, cy: f64| {
        ctx.trees.iter().any(|t| {
            x0 - reach <= t[0] && t[0] <= x1 + reach && y0 - reach <= t[1] && t[1] <= y1 + reach && (cx - t[0]).powi(2) + (cy - t[1]).powi(2) <= (reach + cell * 0.5).powi(2)
        })
    };

    // zone byte inputs
    let zc = mcfg.at("zones").at(atype);
    let mix_ids: Vec<i64> = zc.at("mixes").arr().iter().map(|e| e.arr()[0].i()).collect();
    let mut cum: Vec<f64> = vec![];
    let mut acc = 0.0;
    for e in zc.at("mixes").arr() {
        acc += e.arr()[1].f();
        cum.push(acc);
    }
    let total = acc;
    let unmown_reach = mcfg.at("unmown_m").f();
    let mut lines: Vec<Vec<Pt>> = ctx.fences.iter().map(geom::coords).collect();
    for b in &ctx.buildings {
        for p in geom::polys(b) {
            lines.push(p.ext);
        }
    }
    let d_edge = DistIdx::new(&lines, &[]);
    let d_water = match &ctx.water {
        Some(w) => DistIdx::new(&[], &geom::polys(w)),
        None => DistIdx::empty(),
    };
    let wet_m = mcfg.at("wet_m").f();
    let patch_cells = mcfg.at("patch_m").f() / cell;

    let ls = layers(mcfg);
    let nl = ls.len();
    let amp = mcfg.at("noise_amp").f();
    let lattice_cells = mcfg.at("noise_m").f() / cell;
    let clump_cells = mcfg.at("clump_m").f() / cell;
    let sections = mcfg.at(atype);
    out.gx.reserve(n);
    out.gy.reserve(n);
    let mut v = vec![0u8; nl + 1];
    for k in 0..n {
        let (i, j) = (gx[k], gy[k]);
        let (cx, cy) = ((i as f64 + 0.5) * cell, (j as f64 + 0.5) * cell);
        let bare = bare_r.contains(cx, cy);
        let furrow = furrow_r.contains(cx, cy);
        let litter = litter_r.contains(cx, cy);
        let tone = tone_r.contains(cx, cy);
        let edge = interior.as_ref().is_some_and(|r| !r.contains(cx, cy));
        let near_tree = near_tree_of(cx, cy);
        // zone byte
        let owner = voronoi01(i as f64 + 0.5, j as f64 + 0.5, patch_cells, 3000);
        let pick = cum.iter().position(|&c| c / total > owner).unwrap_or(mix_ids.len() - 1);
        let mut mix = mix_ids[pick];
        let mut dry: i64 = 8;
        let unmown = !d_edge.is_empty() && d_edge.dist(cx, cy) < unmown_reach;
        let wet = !d_water.is_empty() && d_water.dist(cx, cy) < wet_m;
        if zc.has("edge_dry_mix") && edge && hash01(i, j, 3100) < zc.at("edge_dry_share").f() {
            mix = zc.at("edge_dry_mix").i();
        }
        if zc.has("wet_mix") && wet {
            mix = zc.at("wet_mix").i();
        }
        if zc.has("unmown_mix") && unmown {
            mix = zc.at("unmown_mix").i();
        }
        if zc.has("dry_edge") && edge {
            dry += zc.at("dry_edge").i();
        }
        if unmown {
            dry -= mcfg.at("fresh_unmown").i();
        }
        if wet {
            dry -= mcfg.at("fresh_water").i();
        }
        let zone_byte = ((mix << 4) | dry.clamp(0, 15)) as u8;

        let mut any = false;
        for (li, layer) in ls.iter().enumerate() {
            v[li] = 0;
            let spec = sections.at(&layer.name);
            if spec.is_null() || spec.0.as_object().is_some_and(|o| o.is_empty()) {
                continue;
            }
            let mut d = spec.at("interior").f();
            for (flag, key) in [(edge, "edge"), (tone, "tone"), (litter, "litter"), (near_tree, "tree")] {
                if flag && spec.has(key) {
                    d = d.max(spec.at(key).f());
                }
            }
            if spec.has("bare") && bare {
                d = spec.at("bare").f();
            }
            if furrow {
                d = 0.0;
            }
            let noise = value_noise(i as f64, j as f64, lattice_cells, 1000 + li as i64);
            d *= 1.0 - amp + 2.0 * amp * noise;
            let lc = mcfg.at("layers").at(&layer.name);
            if lc.at("clump").f() > 0.0 {
                d *= clump(i as f64 + 0.5, j as f64 + 0.5, lc.at("clump").f(), clump_cells, 1500 + li as i64);
            }
            if lc.has("unmown") && unmown {
                d *= lc.at("unmown").f();
            }
            let byte = (d.clamp(0.0, 1.0) * 255.0).round_ties_even() as u8;
            v[li] = byte;
            any |= byte != 0;
        }
        if any {
            v[nl] = zone_byte;
            out.gx.push(i as i32);
            out.gy.push(j as i32);
            out.vals.extend_from_slice(&v);
        }
    }
    let _ = util::in_box;
    out
}

/// The raster of the village: per chunk ``(layers + 1) * n * n`` bytes.
pub struct Raster {
    pub n: usize,
    pub nl: usize,
    pub chunks: BTreeMap<(i32, i32), Vec<u8>>,
}

impl Raster {
    pub fn new(chunk_m: f64, cell: f64, nl: usize) -> Raster {
        Raster { n: (chunk_m / cell).round() as usize, nl, chunks: BTreeMap::new() }
    }

    /// Writes cells (later cells win over earlier ones, so the order of the merge fixes the result).
    pub fn set(&mut self, c: &Cells) {
        let n = self.n as i64;
        let stride = self.nl + 1;
        for (k, (&gx, &gy)) in c.gx.iter().zip(c.gy.iter()).enumerate() {
            let (cx, cy) = ((gx as i64).div_euclid(n), (gy as i64).div_euclid(n));
            let (lx, ly) = ((gx as i64).rem_euclid(n) as usize, (gy as i64).rem_euclid(n) as usize);
            let plane = self.n * self.n;
            let arr = self.chunks.entry((cx as i32, cy as i32)).or_insert_with(|| vec![0u8; stride * plane]);
            for p in 0..stride {
                arr[p * plane + ly * self.n + lx] = c.vals[k * stride + p];
            }
        }
    }

    pub fn nonzero_cells(&self) -> usize {
        let plane = self.n * self.n;
        self.chunks.values().map(|a| (0..plane).filter(|&c| (0..self.nl).any(|p| a[p * plane + c] != 0)).count()).sum()
    }

    /// Expected instances per layer over the whole village.
    pub fn expected(&self, ls: &[Layer]) -> Vec<f64> {
        let plane = self.n * self.n;
        ls.iter()
            .enumerate()
            .map(|(i, l)| self.chunks.values().map(|a| a[i * plane..(i + 1) * plane].iter().map(|&b| b as f64).sum::<f64>()).sum::<f64>() / 255.0 * l.max_per_cell)
            .collect()
    }
}

fn put_s(buf: &mut Vec<u8>, s: &str) {
    buf.push(s.len() as u8);
    buf.extend_from_slice(s.as_bytes());
}

/// The ``meadow.bin`` bytes. ``tintable`` are the kinds whose instances carry a tint.
pub fn encode(ls: &[Layer], raster: &Raster, cell_m: f64, chunk_m: f64, jitter: f64, trunk_reach: f64, tintable: &[String]) -> Vec<u8> {
    let mut kinds: Vec<&str> = vec![];
    for l in ls {
        for m in &l.mixes {
            for (k, _) in m {
                if !kinds.contains(&k.as_str()) {
                    kinds.push(k);
                }
            }
        }
    }
    let mut head: Vec<u8> = b"MEAD".to_vec();
    head.extend_from_slice(&MEADOW_FORMAT_VERSION.to_le_bytes());
    head.extend_from_slice(&((cell_m * 1000.0).round() as u16).to_le_bytes());
    head.extend_from_slice(&(raster.n as u16).to_le_bytes());
    head.extend_from_slice(&(chunk_m as f32).to_le_bytes());
    head.extend_from_slice(&(jitter as f32).to_le_bytes());
    head.extend_from_slice(&(trunk_reach as f32).to_le_bytes());
    head.push(kinds.len() as u8);
    for k in &kinds {
        put_s(&mut head, k);
        head.push(tintable.iter().any(|t| t == k) as u8);
    }
    head.push(ls.len() as u8);
    for l in ls {
        put_s(&mut head, &l.name);
        head.extend_from_slice(&(l.max_per_cell as f32).to_le_bytes());
        head.extend_from_slice(&(l.scale[0] as f32).to_le_bytes());
        head.extend_from_slice(&(l.scale[1] as f32).to_le_bytes());
        head.push(l.mixes.len() as u8);
        for m in &l.mixes {
            head.push(m.len() as u8);
            for (k, w) in m {
                head.push(kinds.iter().position(|x| x == k).unwrap() as u8);
                head.extend_from_slice(&(*w as f32).to_le_bytes());
            }
        }
    }
    let plane = raster.n * raster.n;
    let keys: Vec<(i32, i32)> = raster.chunks.iter().filter(|(_, a)| a[..raster.nl * plane].iter().any(|&b| b != 0)).map(|(k, _)| *k).collect();
    use rayon::prelude::*;
    let blobs: Vec<Vec<u8>> = keys
        .par_iter()
        .map(|k| {
            let mut z = ZlibEncoder::new(Vec::new(), Compression::new(9));
            z.write_all(&raster.chunks[k]).unwrap();
            z.finish().unwrap()
        })
        .collect();
    let mut dir = vec![];
    let mut off = 0u32;
    for (k, b) in keys.iter().zip(&blobs) {
        dir.extend_from_slice(&k.0.to_le_bytes());
        dir.extend_from_slice(&k.1.to_le_bytes());
        dir.extend_from_slice(&off.to_le_bytes());
        dir.extend_from_slice(&(b.len() as u32).to_le_bytes());
        off += b.len() as u32;
    }
    let mut out = head;
    out.extend_from_slice(&(keys.len() as u32).to_le_bytes());
    out.extend_from_slice(&dir);
    for b in blobs {
        out.extend_from_slice(&b);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn raster_set_places_cells_by_chunk_and_later_wins() {
        let mut r = Raster::new(250.0, 2.0, 2);
        assert_eq!(r.n, 125);
        let c = Cells { gx: vec![0, -1, 125], gy: vec![0, -1, 3], vals: vec![9, 8, 1, 7, 6, 2, 5, 4, 3] };
        r.set(&c);
        assert_eq!(r.chunks.len(), 3);
        let plane = 125 * 125;
        assert_eq!(r.chunks[&(0, 0)][0], 9);
        assert_eq!(r.chunks[&(0, 0)][2 * plane], 1); // zone plane last
        assert_eq!(r.chunks[&(-1, -1)][124 * 125 + 124], 7);
        assert_eq!(r.chunks[&(1, 0)][3 * 125], 5);
        r.set(&Cells { gx: vec![0], gy: vec![0], vals: vec![1, 1, 1] });
        assert_eq!(r.chunks[&(0, 0)][0], 1);
        assert_eq!(r.nonzero_cells(), 3);
    }
}

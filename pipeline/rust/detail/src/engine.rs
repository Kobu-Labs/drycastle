//! The stage: units into parts and tiles, every item on a rayon thread, results merged in a fixed order, files written.
//!
//! Determinism: an item's result depends only on its inputs (streams are seeded by id and position), the items are numbered
//! by (unit id, part, tile), and everything is reduced in that order, so the thread count changes nothing.

use crate::cfg::Cfg;
use crate::ctx::{Ctx, Near, Shared};
use crate::emit::{Clutter, Res};
use crate::field::{self, Part, Pre};
use crate::geom::{self, PolyC, G};
use crate::meadow::{self, Cells, Raster, MEADOW_TYPES};
use crate::patches::{self, Textures};
use rayon::prelude::*;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, HashMap};
use std::io::Write;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::atomic::Ordering;
use std::time::Instant;

pub const TILE_M: f64 = 125.0;
/// A field or forest part with a side longer than this is cut into tiles.
const TILE_ABOVE_M: f64 = 160.0;

struct Item {
    part: usize,
    tile: Option<[f64; 4]>,
    cost: f64,
}

#[derive(Default)]
struct Out {
    rows: Vec<f64>,
    clutter: Vec<Clutter>,
    cells: Cells,
    tally: BTreeMap<&'static str, (usize, f64, usize)>,
    failed: Option<String>,
    secs: f64,
}

fn sort_polys_by_area(g: &G) -> Vec<PolyC> {
    let mut v: Vec<(f64, PolyC)> = geom::polys_g(g).into_iter().map(|p| (geom::area(&p), geom::polys(&p).into_iter().next().unwrap_or_default())).collect();
    v.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap());
    v.into_iter().map(|(_, p)| p).collect()
}

/// One part per polygon of the unit clipped to the terrain coverage (the largest keeps the ID); a dike unit, or a unit with a dike
/// part, also gets ``dike`` parts for it.
fn parts_of(sh: &Shared, unit: &crate::ctx::Unit) -> Vec<Part> {
    let cfg = Cfg(&sh.cfg);
    let cov = geom::from_polyc(&sh.coverage);
    let multi = geom::union_all(unit.polys.iter().map(|p| geom::inter(&geom::from_polyc(p), &cov)).collect());
    let clip = cfg.at("dike").at("clips").at(&unit.id);
    let clip_g: Option<G> = if clip.is_null() {
        None
    } else {
        let lines: Vec<G> = sh.osm_lines.get(clip.at("osm_id").s()).map(|v| v.iter().map(|&i| geom::line(&sh.roads[i].line)).collect()).unwrap_or_default();
        Some(geom::union_all(lines.iter().map(|l| geom::buffer(l, clip.at("within_m").f(), 16)).collect()))
    };
    let min_area = cfg.at("min_parcel_m2").f();
    let mut out = vec![];
    let mut k = 0;
    for part in sort_polys_by_area(&multi) {
        let pg = geom::from_polyc(&part);
        let pieces: Vec<(PolyC, bool)> = if unit.whole_dike {
            vec![(part.clone(), true)]
        } else if let Some(c) = &clip_g {
            let mut v = vec![];
            for (dike, g) in [(false, geom::diff(&pg, c)), (true, geom::inter(&pg, c))] {
                for p in geom::polys(&g) {
                    v.push((p, dike));
                }
            }
            v
        } else {
            vec![(part.clone(), false)]
        };
        for (piece, dike) in pieces {
            if geom::area(&geom::from_polyc(&piece)) < min_area {
                continue;
            }
            out.push(Part {
                cid: if k == 0 { unit.id.clone() } else { format!("{}.{}", unit.id, k) },
                kind: unit.kind.clone(),
                recipe: if dike { "dike".to_string() } else { unit.recipe.clone() },
                poly: piece,
                colour: unit.colour,
                cfg_kind: unit.kind.clone(),
            });
            k += 1;
        }
    }
    out
}

fn run_item(sh: &Shared, part: &Part, pre: &Pre, tile: Option<[f64; 4]>) -> Out {
    let cfg = sh.cfg_for(&part.cfg_kind, &part.recipe);
    let mut out = Out::default();
    let (res, cells): (Res, Cells) = match part.recipe.as_str() {
        "field" | "forest" => field::run(sh, part, cfg, pre, tile, part.recipe == "forest"),
        other => {
            let ctx = Ctx::build(sh, &part.cid, &part.kind, geom::from_polyc(&part.poly), Near::Exact, part.colour);
            let res = match other {
                "garden" => crate::garden::garden(&ctx, cfg),
                "village" => crate::village::village(&ctx, cfg),
                "dike" => {
                    let tile = sh.textures.get("dike_gravel").copied().unwrap_or((1.0, 1.0));
                    crate::dike::dike(&ctx, cfg, (sh.image.1, sh.image.0, &sh.image.2), tile)
                }
                _ => Res::default(),
            };
            (res, Cells::default())
        }
    };
    let tile_of = |n: &str| sh.textures.get(n).copied().unwrap_or((1.0, 1.0));
    let index_of = |n: &str| sh.tex_list.iter().position(|t| t == n).map_or(-1.0, |i| i as f64);
    let tex = Textures { tile: &tile_of, index: &index_of };
    patches::patch_rows(&res.patches, &sh.terrain, cfg.at("lift_m").f(), &tex, &mut out.rows, &mut out.tally);
    out.clutter = res.clutter;
    out.cells = cells;
    out
}

fn r2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

#[derive(Clone)]
struct Row {
    v: [f64; 5],
    tint: i16,
}

fn fmt_row(r: &Row, s: &mut String) {
    use std::fmt::Write as _;
    s.push('[');
    for (i, v) in r.v.iter().enumerate() {
        if i > 0 {
            s.push(',');
        }
        write!(s, "{v:?}").unwrap();
    }
    if r.tint >= 0 {
        write!(s, ",{}", r.tint).unwrap();
    }
    s.push(']');
}

fn cap_hash(r: &Row, kind: &str) -> [u8; 32] {
    let s = format!("{:?}:{:?}:{}:cap", r.v[0], r.v[1], kind);
    Sha256::digest(s.as_bytes()).into()
}

fn row_cmp(a: &Row, b: &Row) -> std::cmp::Ordering {
    for i in 0..5 {
        match a.v[i].partial_cmp(&b.v[i]) {
            Some(std::cmp::Ordering::Equal) | None => {}
            Some(o) => return o,
        }
    }
    a.tint.cmp(&b.tint)
}

fn write_npy(path: &Path, outs: &[Out], n: usize) -> std::io::Result<()> {
    let mut f = std::io::BufWriter::with_capacity(1 << 20, std::fs::File::create(path)?);
    let dict = format!("{{'descr': '<f8', 'fortran_order': False, 'shape': ({}, {}), }}", n, patches::PATCH_ROW);
    let mut header = dict.into_bytes();
    let total = 10 + header.len() + 1;
    let pad = (64 - total % 64) % 64;
    header.extend(std::iter::repeat(b' ').take(pad));
    header.push(b'\n');
    f.write_all(b"\x93NUMPY\x01\x00")?;
    f.write_all(&(header.len() as u16).to_le_bytes())?;
    f.write_all(&header)?;
    for o in outs {
        // SAFETY: f64 has no padding; the bytes are written little endian like the header says
        #[cfg(target_endian = "little")]
        f.write_all(unsafe { std::slice::from_raw_parts(o.rows.as_ptr() as *const u8, o.rows.len() * 8) })?;
        #[cfg(target_endian = "big")]
        for v in &o.rows {
            f.write_all(&v.to_le_bytes())?;
        }
    }
    f.flush()
}

pub struct Inputs {
    pub input: Value,
    pub heights: Vec<f64>,
    pub valid: Vec<bool>,
    pub image: (usize, usize, Vec<u8>),
    pub out_dir: String,
    pub only: Option<Vec<String>>,
    pub write: bool,
}

pub fn run(inp: Inputs) -> Result<Value, String> {
    let t_all = Instant::now();
    let mut timing: BTreeMap<&str, f64> = BTreeMap::new();
    let mut mark = |name: &'static str, t: &mut Instant| {
        timing.insert(name, t.elapsed().as_secs_f64());
        *t = Instant::now();
    };
    let mut t = Instant::now();
    let sh = Shared::parse(&inp.input, inp.heights, inp.valid, inp.image)?;
    mark("setup", &mut t);
    let cfg = Cfg(&sh.cfg);

    // units in id order, each into parts (parallel), parts into items
    let mut order: Vec<usize> = (0..sh.units.len()).filter(|&i| !sh.units[i].recipe.is_empty()).collect();
    if let Some(only) = &inp.only {
        order.retain(|&i| only.contains(&sh.units[i].id));
    }
    order.sort_by(|&a, &b| sh.units[a].id.cmp(&sh.units[b].id));
    let parts: Vec<Part> = order.par_iter().map(|&i| parts_of(&sh, &sh.units[i])).collect::<Vec<_>>().into_iter().flatten().collect();
    mark("parts", &mut t);

    let pres: Vec<Pre> = parts
        .par_iter()
        .map(|p| if p.recipe == "field" { field::prepare_field(p, sh.cfg_for(&p.cfg_kind, "field")) } else { Pre::default() })
        .collect();
    let mut items: Vec<Item> = vec![];
    for (pi, p) in parts.iter().enumerate() {
        let b = p.poly.bounds();
        let big = (p.recipe == "field" || p.recipe == "forest") && ((b[2] - b[0]) > TILE_ABOVE_M || (b[3] - b[1]) > TILE_ABOVE_M);
        if !big {
            items.push(Item { part: pi, tile: None, cost: geom::area(&geom::from_polyc(&p.poly)) });
            continue;
        }
        let g = geom::from_polyc(&p.poly);
        let (i0, i1) = ((b[0] / TILE_M).floor() as i64, (b[2] / TILE_M).floor() as i64);
        let (j0, j1) = ((b[1] / TILE_M).floor() as i64, (b[3] / TILE_M).floor() as i64);
        for j in j0..=j1 {
            for i in i0..=i1 {
                let e = [i as f64 * TILE_M, j as f64 * TILE_M, (i + 1) as f64 * TILE_M, (j + 1) as f64 * TILE_M];
                let clipped = geom::clip_rect(&g, e[0], e[1], e[2], e[3]);
                if geom::area(&clipped) > 1e-6 {
                    items.push(Item { part: pi, tile: Some(e), cost: geom::area(&clipped) });
                }
            }
        }
    }
    mark("items", &mut t);

    // heaviest first, results placed by item number
    let mut by_cost: Vec<usize> = (0..items.len()).collect();
    by_cost.sort_by(|&a, &b| items[b].cost.partial_cmp(&items[a].cost).unwrap());
    let done: Vec<(usize, Out)> = by_cost
        .par_iter()
        .with_max_len(1)
        .map(|&k| {
            let it = &items[k];
            let part = &parts[it.part];
            let t0 = Instant::now();
            let r = catch_unwind(AssertUnwindSafe(|| run_item(&sh, part, &pres[it.part], it.tile)));
            match r {
                Ok(mut o) => {
                    o.secs = t0.elapsed().as_secs_f64();
                    (k, o)
                }
                Err(e) => {
                    let msg = e.downcast_ref::<String>().cloned().or_else(|| e.downcast_ref::<&str>().map(|s| s.to_string())).unwrap_or_default();
                    (k, Out { failed: Some(format!("{}: {}", part.cid, msg)), ..Default::default() })
                }
            }
        })
        .collect();
    let mut outs: Vec<Out> = (0..items.len()).map(|_| Out::default()).collect();
    for (k, o) in done {
        outs[k] = o;
    }
    mark("items_run", &mut t);

    // --- merge in item order ---
    let chunk_m = sh.chunk_m;
    let mut chunks: BTreeMap<(i64, i64), BTreeMap<u16, Vec<Row>>> = BTreeMap::new();
    let mut by_type: BTreeMap<String, BTreeMap<&str, usize>> = BTreeMap::new();
    let mut area_by_type: BTreeMap<String, BTreeMap<&str, f64>> = BTreeMap::new();
    let mut count_by_type: BTreeMap<String, BTreeMap<&str, usize>> = BTreeMap::new();
    let mut clutter_by_recipe: BTreeMap<String, BTreeMap<String, usize>> = BTreeMap::new();
    let mut failed = vec![];
    let mut tris = 0usize;
    let mcfg = cfg.at("meadow");
    let ls = meadow::layers(mcfg);
    let mut raster = Raster::new(chunk_m, mcfg.at("cell_m").f(), ls.len());
    let mut cell_count = 0usize;
    for (k, o) in outs.iter().enumerate() {
        let part = &parts[items[k].part];
        if let Some(f) = &o.failed {
            failed.push(f.clone());
        }
        for (layer, (n, a, c)) in &o.tally {
            *by_type.entry(part.recipe.clone()).or_default().entry(layer).or_default() += n;
            *area_by_type.entry(part.recipe.clone()).or_default().entry(layer).or_default() += a;
            *count_by_type.entry(part.recipe.clone()).or_default().entry(layer).or_default() += c;
            tris += n;
        }
        for c in &o.clutter {
            let kind = &sh.kinds[c.kind as usize];
            *clutter_by_recipe.entry(part.recipe.clone()).or_default().entry(kind.clone()).or_default() += 1;
            let key = ((c.x / chunk_m).floor() as i64, (c.y / chunk_m).floor() as i64);
            chunks.entry(key).or_default().entry(c.kind).or_default().push(Row { v: [r2(c.x), r2(c.y), r2(c.z), r2(c.rot), r2(c.scale)], tint: c.tint });
        }
        cell_count += o.cells.gx.len();
        raster.set(&o.cells);
    }
    mark("merge", &mut t);

    let count = |chunks: &BTreeMap<(i64, i64), BTreeMap<u16, Vec<Row>>>| -> BTreeMap<String, usize> {
        sh.kinds.iter().enumerate().map(|(i, k)| (k.clone(), chunks.values().map(|m| m.get(&(i as u16)).map_or(0, |v| v.len())).sum())).collect()
    };
    let before = count(&chunks);
    let caps = cfg.at("caps_per_chunk");
    let kinds = &sh.kinds;
    chunks.par_iter_mut().for_each(|(_, by_kind)| {
        for (ki, rows) in by_kind.iter_mut() {
            let kind = &kinds[*ki as usize];
            let limit = caps.at(kind).i();
            if limit > 0 && rows.len() as i64 > limit {
                let mut keyed: Vec<([u8; 32], Row)> = rows.drain(..).map(|r| (cap_hash(&r, kind), r)).collect();
                keyed.sort_by(|a, b| a.0.cmp(&b.0));
                keyed.truncate(limit as usize);
                *rows = keyed.into_iter().map(|(_, r)| r).collect();
                rows.sort_by(row_cmp);
            }
        }
    });
    let after = count(&chunks);
    let max_per_chunk: BTreeMap<String, usize> =
        sh.kinds.iter().enumerate().map(|(i, k)| (k.clone(), chunks.values().map(|m| m.get(&(i as u16)).map_or(0, |v| v.len())).max().unwrap_or(0))).collect();
    mark("caps", &mut t);

    // --- files ---
    if inp.write {
        let dir = Path::new(&inp.out_dir);
        let mut kind_order: Vec<(usize, &String)> = sh.kinds.iter().enumerate().collect();
        kind_order.sort_by(|a, b| a.1.cmp(b.1));
        let chunk_json: Vec<String> = chunks
            .par_iter()
            .filter_map(|((ix, iy), by_kind)| {
                let mut s = String::new();
                for (ki, name) in &kind_order {
                    let Some(rows) = by_kind.get(&(*ki as u16)).filter(|r| !r.is_empty()) else { continue };
                    s.push_str(if s.is_empty() { "{" } else { "," });
                    s.push_str(&format!("{name:?}:["));
                    for (n, r) in rows.iter().enumerate() {
                        if n > 0 {
                            s.push(',');
                        }
                        fmt_row(r, &mut s);
                    }
                    s.push(']');
                }
                if s.is_empty() {
                    return None;
                }
                s.push('}');
                Some(format!("\"{ix}_{iy}\":{s}"))
            })
            .collect();
        let tintable = &sh.tintable;
        let kinds_json: Vec<String> =
            sh.kinds.iter().map(|k| if tintable.contains(k) { format!("{k:?}:{{\"mesh\":{k:?},\"tint\":true}}") } else { format!("{k:?}:{{\"mesh\":{k:?}}}") }).collect();
        let doc = format!(
            "{{\"format_version\":1,\"axes\":\"map metres: x east, y north, z up (ground height at the instance)\",\"chunk_size_m\":{:?},\"kinds\":{{{}}},\"chunks\":{{{}}}}}\n",
            chunk_m,
            kinds_json.join(","),
            chunk_json.join(",")
        );
        std::fs::write(dir.join("detail.json"), doc).map_err(|e| e.to_string())?;
        write_npy(&dir.join("detail_patches.npy"), &outs, outs.iter().map(|o| o.rows.len() / patches::PATCH_ROW).sum()).map_err(|e| e.to_string())?;
        let tex_json: Vec<String> = sh.tex_list.iter().map(|t| format!("  {t:?}")).collect();
        std::fs::write(
            dir.join("detail_patches.json"),
            format!(
                "{{\n \"format_version\": 1,\n \"textures\": [\n{}\n ],\n \"row\": \"x0 y0 z0 x1 y1 z1 x2 y2 z2 r g b u0 v0 u1 v1 u2 v2 texture_index(-1: flat colour)\"\n}}\n",
                tex_json.join(",\n")
            ),
        )
        .map_err(|e| e.to_string())?;
        let bin = dir.join("detail_meadow.bin");
        if raster.chunks.is_empty() {
            let _ = std::fs::remove_file(&bin);
        } else {
            let bytes = meadow::encode(&ls, &raster, mcfg.at("cell_m").f(), chunk_m, mcfg.at("jitter_m").f(), mcfg.at("trunk_reach_m").f(), &sh.tintable);
            std::fs::write(&bin, bytes).map_err(|e| e.to_string())?;
        }
    }
    mark("write", &mut t);
    let _ = MEADOW_TYPES;
    let mut slow: Vec<(f64, String)> = outs.iter().enumerate().map(|(k, o)| (o.secs, format!("{}:{}{}", parts[items[k].part].recipe, parts[items[k].part].cid, items[k].tile.map_or(String::new(), |e| format!("@{},{}", e[0], e[1]))))).collect();
    slow.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap());
    slow.truncate(12);
    let mut secs_by_recipe: BTreeMap<String, f64> = BTreeMap::new();
    for (k, o) in outs.iter().enumerate() {
        *secs_by_recipe.entry(parts[items[k].part].recipe.clone()).or_default() += o.secs;
    }

    let expected: BTreeMap<String, f64> = ls.iter().zip(raster.expected(&ls)).map(|(l, e)| (l.name.clone(), e.round())).collect();
    let stats = json!({
        "clutter_before_caps": before,
        "clutter": after,
        "max_per_chunk": max_per_chunk,
        "patch_triangles": tris,
        "patch_triangles_by_type": by_type,
        "patch_area_by_type": area_by_type,
        "patch_count_by_type": count_by_type,
        "clutter_by_recipe": clutter_by_recipe,
        "meadow_cells": cell_count,
        "meadow_nonzero_cells": raster.nonzero_cells(),
        "meadow_expected": expected,
        "items": items.len(),
        "parts": parts.len(),
        "failed": failed,
        "geos_errors": geom::GEOS_ERRORS.load(Ordering::Relaxed),
        "geos_messages": geom::GEOS_MESSAGES.lock().map(|m| m.clone()).unwrap_or_default(),
        "timing_s": timing,
        "seconds": t_all.elapsed().as_secs_f64(),
        "threads": rayon::current_num_threads(),
        "slowest_items": slow.iter().map(|(s, n)| json!([n, s])).collect::<Vec<_>>(),
        "cpu_seconds_by_recipe": secs_by_recipe,
    });
    let _ = HashMap::<u8, u8>::new();
    Ok(stats)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn npy_file_is_a_valid_version_1_array() {
        let dir = std::env::temp_dir().join(format!("dd_npy_{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let outs = vec![Out { rows: (0..38).map(|v| v as f64).collect(), ..Default::default() }, Out::default()];
        write_npy(&dir.join("a.npy"), &outs, 2).unwrap();
        let b = std::fs::read(dir.join("a.npy")).unwrap();
        assert_eq!(&b[..8], b"\x93NUMPY\x01\x00");
        let hl = u16::from_le_bytes([b[8], b[9]]) as usize;
        assert_eq!((10 + hl) % 64, 0);
        let header = std::str::from_utf8(&b[10..10 + hl]).unwrap();
        assert!(header.contains("'<f8'") && header.contains("(2, 19)") && header.ends_with('\n'));
        assert_eq!(b.len(), 10 + hl + 38 * 8);
        assert_eq!(f64::from_le_bytes(b[10 + hl + 8..10 + hl + 16].try_into().unwrap()), 1.0);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn cap_keeps_the_same_rows_whatever_their_order() {
        let rows: Vec<Row> = (0..50).map(|i| Row { v: [i as f64 * 0.5, 3.25, 100.0, 1.0, 1.0], tint: -1 }).collect();
        let pick = |mut r: Vec<Row>| {
            let mut keyed: Vec<_> = r.drain(..).map(|x| (cap_hash(&x, "tuft"), x)).collect();
            keyed.sort_by(|a, b| a.0.cmp(&b.0));
            keyed.truncate(10);
            let mut kept: Vec<Row> = keyed.into_iter().map(|(_, r)| r).collect();
            kept.sort_by(row_cmp);
            kept.iter().map(|r| r.v[0]).collect::<Vec<_>>()
        };
        let mut shuffled = rows.clone();
        shuffled.reverse();
        assert_eq!(pick(rows), pick(shuffled));
    }

    #[test]
    fn rows_print_like_python_floats() {
        let mut s = String::new();
        fmt_row(&Row { v: [12.0, -0.5, 100.25, 0.0, 1.1], tint: 130 }, &mut s);
        assert_eq!(s, "[12.0,-0.5,100.25,0.0,1.1,130]");
    }
}

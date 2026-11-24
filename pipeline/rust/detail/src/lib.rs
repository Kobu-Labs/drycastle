//! ``drycastle_detail``: the ground-detail stage of the Drycastle pipeline in Rust (see docs/pipeline.md, "Rust engine").
//!
//! Python reads the work files and the rules and hands them over as one JSON document plus the terrain arrays; this crate does
//! the per-unit work on all cores (rayon), merges it in a fixed order and writes ``detail.json``, ``detail_patches.npy``,
//! ``detail_patches.json`` and ``detail_meadow.bin`` exactly in the formats of the Python stage.

mod cfg;
mod ctx;
mod dike;
mod emit;
mod engine;
mod field;
mod floorplan;
mod garden;
mod geom;
mod index;
mod meadow;
mod noise;
mod patches;
mod pyrand;
mod crop;
mod planting;
mod rng;
mod roads;
mod terrain;
mod trees;
mod util;
mod verge;
mod village;
mod wet;

use pyo3::exceptions::PyRuntimeError;
use pyo3::prelude::*;
use pyo3::types::PyBytes;

/// ``floor_plans(houses_json) -> plans_json``: the floor plans of hollow houses, each in its entry frame (floorplan.rs).
#[pyfunction]
fn floor_plans(py: Python<'_>, houses: &str) -> PyResult<String> {
    py.detach(|| floorplan::run(houses)).map_err(PyRuntimeError::new_err)
}

/// ``run(input_json, heights, valid, image, image_size, out_dir, threads, only, write) -> stats json``.
///
/// ``heights`` is the little endian float64 bytes of the terrain grid, ``valid`` one byte per vertex, ``image`` the rgb bytes
/// (row by row) of the dike texture of ``image_size = (width, height)``. ``threads`` 0 = all cores. ``only``: just these unit
/// ids (tests, previews). ``write`` false: compute and return the statistics only.
#[pyfunction]
#[pyo3(signature = (input_json, heights, valid, image, image_size, out_dir, threads=0, only=None, write=true))]
#[allow(clippy::too_many_arguments)]
fn run(
    py: Python<'_>, input_json: &str, heights: &[u8], valid: &[u8], image: &[u8], image_size: (usize, usize), out_dir: &str, threads: usize,
    only: Option<Vec<String>>, write: bool,
) -> PyResult<String> {
    let input: serde_json::Value = serde_json::from_str(input_json).map_err(|e| PyRuntimeError::new_err(format!("input json: {e}")))?;
    let h: Vec<f64> = heights.chunks_exact(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
    let v: Vec<bool> = valid.iter().map(|&b| b != 0).collect();
    let inp = engine::Inputs { input, heights: h, valid: v, image: (image_size.0, image_size.1, image.to_vec()), out_dir: out_dir.to_string(), only, write };
    let result = py.detach(|| {
        let pool = rayon::ThreadPoolBuilder::new().num_threads(threads).stack_size(16 << 20).build().map_err(|e| e.to_string())?;
        pool.install(|| engine::run(inp))
    });
    match result {
        Ok(stats) => Ok(stats.to_string()),
        Err(e) => Err(PyRuntimeError::new_err(e)),
    }
}

/// ``drape(jobs_json, wkb, heights, valid, threads) -> (rows, counts)``: geometries built in Python draped on the terrain.
///
/// ``jobs_json``: ``{"terrain": {chunk_m, n, i0, j0, rows, cols}, "jobs": [{"wkb": [start, length], "colour": [r, g, b],
/// "tex": [tile_w, tile_h, index] | null, "angle", "origin": [x, y], "lift"}]}``; ``wkb`` holds every job's WKB one after the other.
/// ``rows`` is the little endian float64 bytes of the patch rows (19 numbers each) in job order, ``counts`` the triangles per job.
#[pyfunction]
#[pyo3(signature = (jobs_json, wkb, heights, valid, threads=0))]
fn drape<'py>(py: Python<'py>, jobs_json: &str, wkb: &[u8], heights: &[u8], valid: &[u8], threads: usize) -> PyResult<(Bound<'py, PyBytes>, Vec<usize>)> {
    let bad = |m: String| PyRuntimeError::new_err(m);
    let doc: serde_json::Value = serde_json::from_str(jobs_json).map_err(|e| bad(format!("jobs json: {e}")))?;
    let num = |v: &serde_json::Value, what: &str| v.as_f64().ok_or_else(|| bad(format!("drape: {what} is not a number")));
    let tj = &doc["terrain"];
    let h: Vec<f64> = heights.chunks_exact(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
    let v: Vec<bool> = valid.iter().map(|&b| b != 0).collect();
    let (rows, cols) = (num(&tj["rows"], "rows")? as usize, num(&tj["cols"], "cols")? as usize);
    if h.len() != rows * cols || v.len() != rows * cols {
        return Err(bad("drape: the terrain arrays do not have rows x cols values".into()));
    }
    let t = terrain::Terrain::new(num(&tj["chunk_m"], "chunk_m")?, num(&tj["n"], "n")? as i64, num(&tj["i0"], "i0")? as i64, num(&tj["j0"], "j0")? as i64, rows, cols, h, v);
    let mut jobs = vec![];
    for j in doc["jobs"].as_array().ok_or_else(|| bad("drape: no jobs".into()))? {
        let (start, len) = (num(&j["wkb"][0], "wkb start")? as usize, num(&j["wkb"][1], "wkb length")? as usize);
        let bytes = wkb.get(start..start + len).ok_or_else(|| bad("drape: a wkb range is outside the bytes".into()))?;
        let tex = if j["tex"].is_null() { None } else { Some((num(&j["tex"][0], "tile_w")?, num(&j["tex"][1], "tile_h")?, num(&j["tex"][2], "texture index")?)) };
        jobs.push(patches::DrapeJob {
            wkb: bytes.to_vec(),
            colour: [num(&j["colour"][0], "colour")? as u8, num(&j["colour"][1], "colour")? as u8, num(&j["colour"][2], "colour")? as u8],
            tex,
            angle: num(&j["angle"], "angle")?,
            origin: [num(&j["origin"][0], "origin")?, num(&j["origin"][1], "origin")?],
            lift: num(&j["lift"], "lift")?,
        });
    }
    let (out, counts) = py
        .detach(|| {
            let pool = rayon::ThreadPoolBuilder::new().num_threads(threads).stack_size(16 << 20).build().map_err(|e| e.to_string())?;
            Ok::<_, String>(pool.install(|| patches::drape_jobs(&jobs, &t)))
        })
        .map_err(bad)?;
    let mut bytes = Vec::with_capacity(out.len() * 8);
    for x in &out {
        bytes.extend_from_slice(&x.to_le_bytes());
    }
    Ok((PyBytes::new(py, &bytes), counts))
}

/// ``drape_roads(terrain_json, feet, lifts, heights, valid, threads) -> [triangles]``: road footprints laid on the terrain.
///
/// ``terrain_json``: ``{chunk_m, n, i0, j0, rows, cols}``; ``feet``: the WKB of every footprint; ``lifts``: its height above the
/// terrain. One result per footprint: the little endian float64 bytes of its triangles (9 numbers each), as ``roads.drape_mesh``,
/// or None for a footprint the engine does not cover (the caller drapes it in Python).
#[pyfunction]
#[pyo3(signature = (terrain_json, feet, lifts, heights, valid, threads=0))]
fn drape_roads<'py>(py: Python<'py>, terrain_json: &str, feet: Vec<Vec<u8>>, lifts: Vec<f64>, heights: &[u8], valid: &[u8], threads: usize) -> PyResult<Vec<Option<Bound<'py, PyBytes>>>> {
    let bad = |m: String| PyRuntimeError::new_err(format!("drape_roads: {m}"));
    let tj: serde_json::Value = serde_json::from_str(terrain_json).map_err(|e| bad(format!("terrain json: {e}")))?;
    let num = |k: &str| tj[k].as_f64().ok_or_else(|| bad(format!("{k} is not a number")));
    let (rows, cols) = (num("rows")? as usize, num("cols")? as usize);
    if heights.len() != rows * cols * 8 || valid.len() != rows * cols || feet.len() != lifts.len() {
        return Err(bad("the terrain arrays do not have rows x cols values, or the lifts do not match the footprints".into()));
    }
    let h: Vec<f64> = heights.chunks_exact(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
    let v: Vec<bool> = valid.iter().map(|&b| b != 0).collect();
    let t = terrain::Terrain::new(num("chunk_m")?, num("n")? as i64, num("i0")? as i64, num("j0")? as i64, rows, cols, h, v);
    let jobs: Vec<(&[u8], f64)> = feet.iter().map(|f| f.as_slice()).zip(lifts.iter().copied()).collect();
    let done = py
        .detach(|| {
            let pool = rayon::ThreadPoolBuilder::new().num_threads(threads).stack_size(16 << 20).build().map_err(|e| e.to_string())?;
            Ok::<_, String>(pool.install(|| roads::drape_feet(&jobs, &t)))
        })
        .map_err(bad)?;
    Ok(done
        .iter()
        .map(|tris| {
            let tris = tris.as_ref()?;
            let mut bytes = Vec::with_capacity(tris.len() * 8);
            for x in tris {
                bytes.extend_from_slice(&x.to_le_bytes());
            }
            Some(PyBytes::new(py, &bytes))
        })
        .collect())
}

/// ``plant_trees(cfg_json, mask, geoms, placed) -> rows``: the forest trees of the props stage (``props.ortho_trees``).
///
/// ``cfg_json``: the numbers of the planting (see ``props.plant_canopy_rust``); ``mask``: the canopy mask, one byte per pixel;
/// ``geoms``: WKB of the terrain coverage, then the keep-out zones, then (when ``cfg.swamp_zone``) the swamp reach; ``placed``:
/// float64 x, y of the trees that stand already. ``rows``: float64, 9 per tree in planting order: x, y, scale, rot, cone (0/1),
/// colour index, age class index (-1: none), stand index (the number of types: swamp; -1: no stands), edge (0/1).
#[pyfunction]
fn plant_trees<'py>(py: Python<'py>, cfg_json: &str, mask: &[u8], geoms: Vec<Vec<u8>>, placed: &[u8]) -> PyResult<Bound<'py, PyBytes>> {
    let bad = |m: String| PyRuntimeError::new_err(format!("plant_trees: {m}"));
    let c: serde_json::Value = serde_json::from_str(cfg_json).map_err(|e| bad(format!("cfg json: {e}")))?;
    let num = |v: &serde_json::Value, k: &str| v[k].as_f64().ok_or_else(|| bad(format!("{k} is not a number")));
    let stand_type = |v: &serde_json::Value| -> PyResult<planting::StandType> {
        let scale = if v["scale"].is_array() { (num(&v["scale"], "0").or_else(|_| v["scale"][0].as_f64().ok_or_else(|| bad("scale".into())))?, v["scale"][1].as_f64()) } else { (num(v, "scale")?, None) };
        Ok(planting::StandType { share: v["share"].as_f64().unwrap_or(0.0), cone_share: num(v, "cone_share")?, scale, keep: num(v, "keep")? })
    };
    let stands = if c["stands"].is_null() {
        None
    } else {
        let s = &c["stands"];
        let types = s["types"].as_array().ok_or_else(|| bad("stands.types".into()))?.iter().map(stand_type).collect::<PyResult<Vec<_>>>()?;
        let swamp = if s["swamp"].is_null() { None } else { Some(stand_type(&s["swamp"])?) };
        let thicket = if s["thicket"].is_null() { None } else { Some((num(&s["thicket"], "index")? as usize, num(&s["thicket"], "density_factor")?, num(&s["thicket"], "spacing_scale")?)) };
        let edge = if s["edge"].is_null() {
            None
        } else {
            let e = &s["edge"];
            Some(planting::Edge { edge_m: num(e, "edge_m")?, lo: num(e, "lo")?, hi: num(e, "hi")?, cone_share: num(e, "cone_share")?, k: num(e, "k")? as usize, r: num(e, "r")? as usize })
        };
        Some(planting::StandsCfg { cell: num(s, "cell")?, key: s["key"].as_str().unwrap_or("").to_string(), types, swamp, thicket, edge })
    };
    let cfg = planting::Cfg {
        w: num(&c, "w")? as usize, h: num(&c, "h")? as usize, minx: num(&c, "minx")?, maxy: num(&c, "maxy")?, res: num(&c, "res")?, ox: num(&c, "ox")?, oy: num(&c, "oy")?,
        cell_px: num(&c, "cell_px")? as usize, spacing: num(&c, "spacing")?, density: num(&c, "density")?, crown: (num(&c, "crown_lo")?, num(&c, "crown_hi")?),
        ages: c["ages"].as_array().map(|a| a.iter().map(|v| (v[0].as_f64().unwrap_or(0.0), v[1].as_f64().unwrap_or(1.0))).collect()).unwrap_or_default(),
        palette: num(&c, "palette")? as u32, stands,
    };
    let swamp_zone = c["swamp_zone"].as_bool().unwrap_or(false);
    if mask.len() != cfg.w * cfg.h || cfg.cell_px == 0 || cfg.palette == 0 || geoms.len() < 1 + swamp_zone as usize {
        return Err(bad("the mask is not w x h bytes, or the geometries are missing".into()));
    }
    if swamp_zone && cfg.stands.as_ref().is_none_or(|s| s.swamp.is_none()) {
        return Err(bad("a swamp zone without the swamp stand".into()));
    }
    let gs: Vec<geom::G> = geoms.iter().map(|w| geom::from_wkb(w)).collect();
    let n_zones = gs.len() - 1 - swamp_zone as usize;
    let spots: Vec<[f64; 2]> = placed.chunks_exact(16).map(|c| [f64::from_le_bytes(c[..8].try_into().unwrap()), f64::from_le_bytes(c[8..].try_into().unwrap())]).collect();
    let trees = planting::plant(&cfg, mask, &spots, &gs[0], &gs[1..1 + n_zones], if swamp_zone { gs.last() } else { None }).map_err(bad)?;
    let mut bytes = Vec::with_capacity(trees.len() * 72);
    for t in &trees {
        for x in [t.x, t.y, t.scale, t.rot, t.cone as u8 as f64, t.colour as f64, t.age as f64, t.stand as f64, t.edge as u8 as f64] {
            bytes.extend_from_slice(&x.to_le_bytes());
        }
    }
    Ok(PyBytes::new(py, &bytes))
}

/// ``detect_trees(rgb, width, height, built, strip_rows, margin, model, params, kernels, radii, threads) -> (canopy, candidates)``.
///
/// The per-strip detection of the ``ortho-trees`` stage (``ortho_trees.detect_strip``) on all cores. ``rgb``: the photo, 3 bytes
/// per pixel row by row; ``built``: one byte per pixel of every strip (margins included), one strip after the other; ``model``:
/// the little endian float64 bytes of ``mu, sd, w`` (34 each) and ``b``; ``params``: ``(canopy_p, green, wooded_p, tau_open,
/// tau_wooded)``; ``kernels``: float32 bytes of the blur kernels ``[green, wooded, ring and core of every crown size]``;
/// ``radii``: the crown radius in pixels per crown size. ``canopy`` is one byte per pixel of the photo, ``candidates`` the float64
/// bytes of ``(score, column, row, crown size index)`` rows in the order of the Python reference.
#[pyfunction]
#[pyo3(signature = (rgb, width, height, built, strip_rows, margin, model, params, kernels, radii, threads=0))]
#[allow(clippy::too_many_arguments)]
fn detect_trees<'py>(
    py: Python<'py>, rgb: &[u8], width: usize, height: usize, built: &[u8], strip_rows: usize, margin: usize, model: &[u8],
    params: (f64, f64, f64, f64, f64), kernels: Vec<Vec<u8>>, radii: Vec<f64>, threads: usize,
) -> PyResult<(Bound<'py, PyBytes>, Bound<'py, PyBytes>)> {
    let bad = |m: &str| PyRuntimeError::new_err(format!("detect_trees: {m}"));
    let n = trees::N_FEATURES * 2;
    let m: Vec<f64> = model.chunks_exact(8).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
    if m.len() != 3 * n + 1 {
        return Err(bad("the model is not mu, sd, w of 34 numbers each and b"));
    }
    if width == 0 || height == 0 || strip_rows == 0 || rgb.len() != width * height * 3 {
        return Err(bad("the photo does not have width x height x 3 bytes"));
    }
    let strip_px: usize = (0..height).step_by(strip_rows).map(|y0| ((y0 + strip_rows + margin).min(height) - y0.saturating_sub(margin)) * width).sum();
    if built.len() != strip_px {
        return Err(bad("the building masks do not cover the strips"));
    }
    let ks: Vec<Vec<f32>> = kernels.iter().map(|k| k.chunks_exact(4).map(|c| f32::from_le_bytes(c.try_into().unwrap())).collect()).collect();
    if ks.len() != 2 + 2 * radii.len() || ks.iter().any(|k| k.len() % 2 == 0) {
        return Err(bad("the kernels are not green, wooded and a ring and a core per crown size, each of odd length"));
    }
    let model = trees::Model { mu: m[..n].to_vec(), sd: m[n..2 * n].to_vec(), w: m[2 * n..3 * n].to_vec(), b: m[3 * n] };
    let blobs = radii.iter().enumerate().map(|(i, &rad)| trees::Blob { ring: ks[2 + 2 * i].clone(), core: ks[3 + 2 * i].clone(), rad }).collect();
    let p = trees::Params {
        canopy_p: params.0, green: params.1 as f32, wooded_p: params.2 as f32, tau_open: params.3, tau_wooded: params.4,
        green_k: ks[0].clone(), wooded_k: ks[1].clone(), blobs,
    };
    let (canopy, cands) = py
        .detach(|| {
            let pool = rayon::ThreadPoolBuilder::new().num_threads(threads).build().map_err(|e| e.to_string())?;
            Ok::<_, String>(pool.install(|| trees::detect(rgb, width, height, built, strip_rows, margin, &model, &p)))
        })
        .map_err(|e| bad(&e))?;
    let mut bytes = Vec::with_capacity(cands.len() * 32);
    for (s, c, r, si) in &cands {
        for x in [*s, *c as f64, *r as f64, *si as f64] {
            bytes.extend_from_slice(&x.to_le_bytes());
        }
    }
    Ok((PyBytes::new(py, &canopy), PyBytes::new(py, &bytes)))
}

#[pymodule]
fn drycastle_detail(m: &Bound<'_, PyModule>) -> PyResult<()> {
    m.add_function(wrap_pyfunction!(run, m)?)?;
    m.add_function(wrap_pyfunction!(floor_plans, m)?)?;
    m.add_function(wrap_pyfunction!(drape, m)?)?;
    m.add_function(wrap_pyfunction!(detect_trees, m)?)?;
    m.add_function(wrap_pyfunction!(drape_roads, m)?)?;
    m.add_function(wrap_pyfunction!(plant_trees, m)?)?;
    Ok(())
}

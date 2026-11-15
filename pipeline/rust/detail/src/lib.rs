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
mod garden;
mod geom;
mod index;
mod meadow;
mod noise;
mod patches;
mod rng;
mod terrain;
mod util;
mod verge;
mod village;

use pyo3::exceptions::PyRuntimeError;
use pyo3::prelude::*;

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

#[pymodule]
fn drycastle_detail(m: &Bound<'_, PyModule>) -> PyResult<()> {
    m.add_function(wrap_pyfunction!(run, m)?)?;
    Ok(())
}

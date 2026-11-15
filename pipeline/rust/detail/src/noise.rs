//! The deterministic noise of the meadow (meadow.py: ``hash01``, ``value_noise``, ``voronoi01``, ``clump``, tint). These
//! mirror the game's loader, so they follow the Python formulas exactly.

pub const NEUTRAL_TINT: f64 = 128.0;
pub const TONE_LATTICE_CELLS: f64 = 12.0;
pub const TONE_SALT: i64 = 2000;
pub const TONE_AMP: f64 = 100.0;
pub const TONE_JITTER: f64 = 14.0;

/// A deterministic number in [0, 1) per integer pair.
#[inline]
pub fn hash01(ix: i64, iy: i64, salt: i64) -> f64 {
    let mut x = (ix as u64).wrapping_mul(0x9E3779B97F4A7C15);
    x ^= (iy as u64).wrapping_mul(0xC2B2AE3D27D4EB4F);
    x ^= (salt as u64).wrapping_mul(0x165667B19E3779F9);
    x ^= 0x243F6A8885A308D3;
    x ^= x >> 33;
    x = x.wrapping_mul(0xFF51AFD7ED558CCD);
    x ^= x >> 33;
    x = x.wrapping_mul(0xC4CEB9FE1A85EC53);
    x ^= x >> 33;
    (x >> 11) as f64 / (1u64 << 53) as f64
}

/// Smooth noise in [0, 1) at (global) cell coordinates, bilinear between lattice corners of ``lattice`` cells.
pub fn value_noise(gx: f64, gy: f64, lattice: f64, salt: i64) -> f64 {
    let (fx, fy) = (gx / lattice, gy / lattice);
    let (x0, y0) = (fx.floor(), fy.floor());
    let (mut tx, mut ty) = (fx - x0, fy - y0);
    tx = tx * tx * (3.0 - 2.0 * tx);
    ty = ty * ty * (3.0 - 2.0 * ty);
    let (x0, y0) = (x0 as i64, y0 as i64);
    let (a, b) = (hash01(x0, y0, salt), hash01(x0 + 1, y0, salt));
    let (c, d) = (hash01(x0, y0 + 1, salt), hash01(x0 + 1, y0 + 1, salt));
    (a * (1.0 - tx) + b * tx) * (1.0 - ty) + (c * (1.0 - tx) + d * tx) * ty
}

/// A number in [0, 1) per Voronoi patch (one random point per ``lattice``-cell square).
pub fn voronoi01(gx: f64, gy: f64, lattice: f64, salt: i64) -> f64 {
    let (fx, fy) = (gx / lattice, gy / lattice);
    let (ix, iy) = (fx.floor() as i64, fy.floor() as i64);
    let (mut best, mut out) = (f64::INFINITY, 0.0);
    for dx in -1..=1 {
        for dy in -1..=1 {
            let (cx, cy) = (ix + dx, iy + dy);
            let ddx = fx - (cx as f64 + hash01(cx, cy, salt));
            let ddy = fy - (cy as f64 + hash01(cx, cy, salt + 1));
            let d = ddx * ddx + ddy * ddy;
            if d < best {
                best = d;
                out = hash01(cx, cy, salt + 2);
            }
        }
    }
    out
}

#[inline]
pub fn tone_noise(gx: f64, gy: f64) -> f64 {
    value_noise(gx, gy, TONE_LATTICE_CELLS, TONE_SALT)
}

#[inline]
pub fn smoothstep(x: f64, lo: f64, hi: f64) -> f64 {
    let t = ((x - lo) / (hi - lo)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// A density factor around 1 that makes growth patchy.
pub fn clump(gx: f64, gy: f64, amount: f64, lattice_cells: f64, salt: i64) -> f64 {
    (1.0 - amount) + amount * 2.0 * smoothstep(value_noise(gx, gy, lattice_cells, salt), 0.3, 0.7)
}

/// The tint byte of a point (floor(x + 0.5), clipped to 0..255).
#[inline]
pub fn tint_byte(noise: f64, jitter01: f64, bias: f64) -> i64 {
    let t = (NEUTRAL_TINT + (noise - 0.5) * 2.0 * TONE_AMP + (jitter01 - 0.5) * 2.0 * TONE_JITTER + bias + 0.5).floor();
    t.clamp(0.0, 255.0) as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hash_is_in_range_and_deterministic() {
        for i in -50..50 {
            let h = hash01(i, 3 * i + 1, 1000);
            assert!((0.0..1.0).contains(&h));
            assert_eq!(h, hash01(i, 3 * i + 1, 1000));
        }
    }

    /// Reference values computed with the numpy functions of meadow.py (the game's loader mirrors them).
    #[test]
    fn matches_the_python_functions() {
        let close = |a: f64, b: f64| assert!((a - b).abs() < 1e-12, "{a} vs {b}");
        close(hash01(0, 0, 0), 0.4797017333778095);
        close(hash01(-7, 12, 1003), 0.8440014520620761);
        close(value_noise(13.37, -4.2, 12.0, 2000), 0.1278864925708346);
        close(voronoi01(21.3, -8.9, 3.0, 3000), 0.6729178834226214);
        close(clump(5.5, 9.25, 0.7, 2.5, 1500), 0.7213563145256241);
        assert_eq!(tint_byte(0.73, 0.2, -40.0), 126);
    }

    #[test]
    fn noise_is_smooth_and_bounded() {
        let mut prev = value_noise(0.0, 5.0, 12.0, 2000);
        for i in 1..200 {
            let v = value_noise(i as f64 * 0.05, 5.0, 12.0, 2000);
            assert!((0.0..1.0).contains(&v));
            assert!((v - prev).abs() < 0.2);
            prev = v;
        }
        assert!(tint_byte(0.5, 0.5, 0.0) == 128);
    }
}

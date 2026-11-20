//! The forest trees of the props stage: `props.ortho_trees`' planting of the canopy mask, draw by draw.
//!
//! The planting is one seeded sequence (a tree keeps its distance from every earlier one), so this is a single-threaded port:
//! Python's `random.Random` (Mersenne Twister, seeded as Python seeds an int), its float floor division and the same order of
//! every draw, so the trees are the reference's. Python keeps the standalone trees, the heights and the records.

use crate::geom::G;
use geos::{Geom, PreparedGeometry};
use sha2::{Digest, Sha256};
use std::collections::HashMap;

/// Python's `random.Random(n)` for a 64-bit `n`.
pub struct PyRandom {
    mt: [u32; 624],
    at: usize,
}

impl PyRandom {
    pub fn new(seed: u64) -> PyRandom {
        let mut r = PyRandom { mt: [0; 624], at: 624 };
        r.mt[0] = 19650218;
        for i in 1..624 {
            r.mt[i] = 1812433253u32.wrapping_mul(r.mt[i - 1] ^ (r.mt[i - 1] >> 30)).wrapping_add(i as u32);
        }
        let (lo, hi) = (seed as u32, (seed >> 32) as u32);
        let key: Vec<u32> = if hi != 0 { vec![lo, hi] } else { vec![lo] };
        let (mut i, mut j) = (1usize, 0usize);
        for _ in 0..624.max(key.len()) {
            r.mt[i] = (r.mt[i] ^ (r.mt[i - 1] ^ (r.mt[i - 1] >> 30)).wrapping_mul(1664525)).wrapping_add(key[j]).wrapping_add(j as u32);
            i += 1;
            j += 1;
            if i >= 624 {
                r.mt[0] = r.mt[623];
                i = 1;
            }
            if j >= key.len() {
                j = 0;
            }
        }
        for _ in 0..623 {
            r.mt[i] = (r.mt[i] ^ (r.mt[i - 1] ^ (r.mt[i - 1] >> 30)).wrapping_mul(1566083941)).wrapping_sub(i as u32);
            i += 1;
            if i >= 624 {
                r.mt[0] = r.mt[623];
                i = 1;
            }
        }
        r.mt[0] = 0x8000_0000;
        r
    }

    /// `seeded(key)` of props.py: the first 8 bytes of sha256(key), big endian.
    pub fn keyed(key: &str) -> PyRandom {
        let d = Sha256::digest(key.as_bytes());
        PyRandom::new(u64::from_be_bytes(d[..8].try_into().unwrap()))
    }

    pub(crate) fn next_u32(&mut self) -> u32 {
        if self.at >= 624 {
            for k in 0..624 {
                let y = (self.mt[k] & 0x8000_0000) | (self.mt[(k + 1) % 624] & 0x7fff_ffff);
                self.mt[k] = self.mt[(k + 397) % 624] ^ (y >> 1) ^ if y & 1 != 0 { 0x9908_b0df } else { 0 };
            }
            self.at = 0;
        }
        let mut y = self.mt[self.at];
        self.at += 1;
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c_5680;
        y ^= (y << 15) & 0xefc6_0000;
        y ^ (y >> 18)
    }

    pub fn random(&mut self) -> f64 {
        let (a, b) = ((self.next_u32() >> 5) as f64, (self.next_u32() >> 6) as f64);
        (a * 67108864.0 + b) * (1.0 / 9007199254740992.0)
    }

    /// `randrange(n)` for `0 < n < 2^32`.
    pub fn below(&mut self, n: u32) -> u32 {
        let k = 32 - n.leading_zeros();
        loop {
            let r = self.next_u32() >> (32 - k);
            if r < n {
                return r;
            }
        }
    }
}

/// Python's `int(x // y)` for floats.
pub fn floordiv(x: f64, y: f64) -> i64 {
    let mut m = x % y;
    let mut div = (x - m) / y;
    if m != 0.0 && ((y < 0.0) != (m < 0.0)) {
        m += y;
        div -= 1.0;
    }
    let _ = m;
    if div != 0.0 {
        let mut f = div.floor();
        if div - f > 0.5 {
            f += 1.0;
        }
        f as i64
    } else {
        0
    }
}

pub struct StandType {
    pub share: f64,
    pub cone_share: f64,
    pub scale: (f64, Option<f64>), // a number, or [lo, hi]
    pub keep: f64,
}

pub struct Edge {
    pub edge_m: f64,
    pub lo: f64,
    pub hi: f64,
    pub cone_share: f64,
    pub k: usize,
    pub r: usize,
}

pub struct StandsCfg {
    pub cell: f64,
    pub key: String, // "stand:<cell as Python prints it>"
    pub types: Vec<StandType>,
    pub swamp: Option<StandType>,
    pub thicket: Option<(usize, f64, f64)>, // index of the type, density_factor, spacing_scale
    pub edge: Option<Edge>,
}

pub struct Cfg {
    pub w: usize,
    pub h: usize,
    pub minx: f64,
    pub maxy: f64,
    pub res: f64,
    pub ox: f64,
    pub oy: f64,
    pub cell_px: usize,
    pub spacing: f64,
    pub density: f64,
    pub crown: (f64, f64),
    pub ages: Vec<(f64, f64)>, // (share, scale)
    pub palette: u32,
    pub stands: Option<StandsCfg>,
}

/// What Python needs of one planted tree.
pub struct Planted {
    pub x: f64,
    pub y: f64,
    pub scale: f64,
    pub rot: f64,
    pub cone: bool,
    pub colour: u32,
    pub age: i64,   // index of the age class, -1: none
    pub stand: i64, // index of the stand type, the number of types for swamp, -1: no stands
    pub edge: bool,
}

struct Stands<'a> {
    cfg: &'a StandsCfg,
    cum: Vec<f64>,
    seeds: HashMap<(i64, i64), (f64, f64, usize)>,
}

impl Stands<'_> {
    fn seed(&mut self, i: i64, j: i64) -> (f64, f64, usize) {
        if let Some(s) = self.seeds.get(&(i, j)) {
            return *s;
        }
        let mut rng = PyRandom::keyed(&format!("{}:{}:{}", self.cfg.key, i, j));
        // random.choices: bisect the running weights with random() * total
        let n = self.cum.len();
        let v = rng.random() * (self.cum[n - 1] + 0.0);
        let name = self.cum[..n - 1].iter().position(|&c| v < c).unwrap_or(n - 1);
        let got = ((i as f64 + rng.random()) * self.cfg.cell, (j as f64 + rng.random()) * self.cfg.cell, name);
        self.seeds.insert((i, j), got);
        got
    }

    fn noise(&mut self, x: f64, y: f64) -> usize {
        let (i, j) = (floordiv(x, self.cfg.cell), floordiv(y, self.cfg.cell));
        let mut best = (f64::INFINITY, 0);
        for a in [i - 1, i, i + 1] {
            for b in [j - 1, j, j + 1] {
                let s = self.seed(a, b);
                let d = (s.0 - x) * (s.0 - x) + (s.1 - y) * (s.1 - y);
                if d < best.0 {
                    best = (d, s.2);
                }
            }
        }
        best.1
    }
}

/// Where the open ground is around every pixel (props.EdgeField).
struct EdgeField {
    open: Vec<bool>,
    w: usize,
    r: usize,
    res: f64,
    reach_m: f64,
}

impl EdgeField {
    fn new(mask: &[u8], w: usize, h: usize, res: f64, e: &Edge) -> EdgeField {
        let (k, r) = (e.k, e.r);
        let n = 2 * k + 1;
        let pad = r + k;
        let (pw, ph) = (w + 2 * pad, h + 2 * pad);
        let cw = pw + 1;
        let mut acc = vec![0i32; cw * (ph + 1)];
        for y in 0..ph {
            let mut run = 0i32;
            for x in 0..pw {
                let inside = y >= pad && y < pad + h && x >= pad && x < pad + w;
                run += if inside { (mask[(y - pad) * w + x - pad] != 0) as i32 } else { 1 }; // off the raster counts as canopy
                acc[(y + 1) * cw + x + 1] = acc[y * cw + x + 1] + run;
            }
        }
        let (ow, oh) = (w + 2 * r, h + 2 * r);
        let mut open = vec![false; ow * oh];
        for y in 0..oh {
            for x in 0..ow {
                let total = acc[(y + n) * cw + x + n] - acc[y * cw + x + n] - acc[(y + n) * cw + x] + acc[y * cw + x];
                open[y * ow + x] = 2 * total < (n * n) as i32;
            }
        }
        EdgeField { open, w: ow, r, res, reach_m: e.edge_m }
    }

    fn distance(&self, row: usize, col: usize) -> f64 {
        let r = self.r as i64;
        let mut best = i64::MAX;
        for i in 0..=2 * self.r {
            let line = &self.open[(row + i) * self.w + col..][..2 * self.r + 1];
            for (j, &o) in line.iter().enumerate() {
                if o {
                    let (dr, dc) = (i as i64 - r, j as i64 - r);
                    best = best.min(dr * dr + dc * dc);
                }
            }
        }
        if best == i64::MAX {
            return self.reach_m;
        }
        self.reach_m.min((best as f64).sqrt() * self.res)
    }
}

/// The canopy trees, in planting order. `placed`: the trees that stand already (the standalone ones); `coverage`: the terrain;
/// `zones`: where no trunk may stand; `swamp`: the swamp reach, if any.
#[allow(clippy::too_many_arguments)]
pub fn plant(cfg: &Cfg, mask: &[u8], placed: &[[f64; 2]], coverage: &G, zones: &[G], swamp: Option<&G>) -> Result<Vec<Planted>, String> {
    fn prep(g: &G) -> Result<PreparedGeometry<'_>, String> {
        g.to_prepared_geom().map_err(|e| e.to_string())
    }
    let cover = prep(coverage)?;
    let off: Vec<PreparedGeometry> = zones.iter().map(prep).collect::<Result<_, _>>()?;
    let wet = swamp.map(prep).transpose()?;
    let clear = |x: f64, y: f64| cover.contains_xy(x, y).unwrap_or(false) && !off.iter().any(|g| g.contains_xy(x, y).unwrap_or(false));
    let spacing = cfg.spacing;
    let mut grid: HashMap<(i64, i64), Vec<[f64; 2]>> = HashMap::new();
    let add = |grid: &mut HashMap<(i64, i64), Vec<[f64; 2]>>, x: f64, y: f64| grid.entry((floordiv(x, spacing), floordiv(y, spacing))).or_default().push([x, y]);
    let free = |grid: &HashMap<(i64, i64), Vec<[f64; 2]>>, x: f64, y: f64, gap: f64| {
        let (gx, gy) = (floordiv(x, spacing), floordiv(y, spacing));
        for i in gx - 1..=gx + 1 {
            for j in gy - 1..=gy + 1 {
                if let Some(c) = grid.get(&(i, j)) {
                    if c.iter().any(|p| (x - p[0]).hypot(y - p[1]) < gap) {
                        return false;
                    }
                }
            }
        }
        true
    };
    for p in placed {
        add(&mut grid, p[0], p[1]);
    }
    let mut stands = cfg.stands.as_ref().map(|s| {
        let mut cum = vec![];
        let mut total = 0.0;
        for t in &s.types {
            total += t.share;
            cum.push(total);
        }
        Stands { cfg: s, cum, seeds: HashMap::new() }
    });
    let edges = cfg.stands.as_ref().and_then(|s| s.edge.as_ref()).map(|e| EdgeField::new(mask, cfg.w, cfg.h, cfg.res, e));
    let n_types = cfg.stands.as_ref().map_or(0, |s| s.types.len());
    let age_total: f64 = cfg.ages.iter().fold(0.0, |a, s| a + s.0);
    let (res, size) = (cfg.res, cfg.cell_px as f64 * cfg.res);
    let mut out: Vec<Planted> = vec![];

    // a forest tree: crown, age class, then the stand's shape mix and size, then the edge mantle (`plant` and `tree` of props.py)
    let plant_one = |x: f64, y: f64, rng: &mut PyRandom, stand: i64, row: usize, col: usize| -> Planted {
        let crown = cfg.crown.0 + (cfg.crown.1 - cfg.crown.0) * rng.random();
        let (mut age, mut grow) = (-1i64, 1.0);
        if age_total > 0.0 {
            let mut pick = PyRandom::keyed(&format!("ortho:age:{x:.3}:{y:.3}")).random() * age_total;
            let last = cfg.ages.len() - 1;
            (age, grow) = (last as i64, cfg.ages[last].1);
            for (i, a) in cfg.ages.iter().enumerate() {
                pick -= a.0;
                if pick < 0.0 {
                    (age, grow) = (i as i64, a.1);
                    break;
                }
            }
        }
        let (mut scale, mut cone, mut edge) = (crown * grow, 0.6, false);
        if let Some(s) = cfg.stands.as_ref() {
            let st = if stand as usize == n_types { s.swamp.as_ref().unwrap() } else { &s.types[stand as usize] };
            let u = PyRandom::keyed(&format!("stand:size:{x:.3}:{y:.3}")).random();
            scale *= match st.scale.1 {
                Some(hi) => st.scale.0 + (hi - st.scale.0) * u,
                None => st.scale.0,
            };
            cone = st.cone_share;
            if let (Some(e), Some(field)) = (s.edge.as_ref(), edges.as_ref()) {
                let d = field.distance(row, col);
                if d < e.edge_m {
                    scale *= e.lo + (e.hi - e.lo) * d / e.edge_m;
                    cone = e.cone_share;
                    edge = true;
                }
            }
        }
        let is_cone = rng.random() < cone;
        let rot = rng.random() * std::f64::consts::PI / 2.0;
        let colour = rng.below(cfg.palette);
        Planted { x, y, scale, rot, cone: is_cone, colour, age, stand, edge }
    };

    let mut r0 = 0;
    while r0 < cfg.h {
        let ch = cfg.cell_px.min(cfg.h - r0);
        for c0 in (0..cfg.w).step_by(cfg.cell_px) {
            let cw = cfg.cell_px.min(cfg.w - c0);
            let cell = |row: usize, col: usize| mask[(r0 + row) * cfg.w + c0 + col] != 0;
            let n: usize = (0..ch).map(|r| (0..cw).filter(|&c| cell(r, c)).count()).sum();
            if n == 0 {
                continue;
            }
            let mut rng = PyRandom::keyed(&format!("ortho:canopy:{r0}:{c0}"));
            let expected = cfg.density * n as f64 * res * res / 1000.0;
            let count = expected as usize + (rng.random() < expected % 1.0) as usize;
            let (x_lo, y_hi) = (cfg.minx + c0 as f64 * res - cfg.ox, cfg.maxy - r0 as f64 * res - cfg.oy);
            // a random point of the canopy in this cell: (x, y, row, col), or None off the mask
            let draw = |rng: &mut PyRandom| {
                let (x, y) = (x_lo + rng.random() * size, y_hi - rng.random() * size);
                let (row, col) = (((y_hi - y) / res) as usize, ((x - x_lo) / res) as usize);
                (row < ch && col < cw && cell(row, col)).then_some((x, y, row, col))
            };
            let mut done = 0;
            for _ in 0..count * 100 {
                if done >= count {
                    break;
                }
                let Some((x, y, row, col)) = draw(&mut rng) else { continue };
                if !free(&grid, x, y, spacing) || !clear(x, y) {
                    continue;
                }
                let (mut stand, mut keep) = (-1i64, 1.0);
                if let Some(st) = stands.as_mut() {
                    let in_swamp = wet.as_ref().is_some_and(|g| g.contains_xy(x, y).unwrap_or(false));
                    stand = if in_swamp { n_types as i64 } else { st.noise(x, y) as i64 };
                    keep = if in_swamp { st.cfg.swamp.as_ref().unwrap().keep } else { st.cfg.types[stand as usize].keep };
                }
                if keep < 1.0 && PyRandom::keyed(&format!("stand:keep:{x:.3}:{y:.3}")).random() >= keep {
                    done += 1; // thinned out: no tree, and it leaves the room free
                    continue;
                }
                add(&mut grid, x, y);
                out.push(plant_one(x, y, &mut rng, stand, r0 + row, c0 + col));
                done += 1;
            }
            // a thicket is denser than the canopy rule plants: trees between the others, closer than the normal spacing
            let Some(st) = stands.as_mut() else { continue };
            let Some((thicket, factor, spacing_scale)) = st.cfg.thicket else { continue };
            let extra_n = expected * (factor - 1.0);
            if extra_n <= 0.0 {
                continue;
            }
            let mut any = false;
            'look: for fx in [0.0, 0.5, 1.0] {
                for fy in [0.0, 0.5, 1.0] {
                    if st.noise(x_lo + fx * size, y_hi - fy * size) == thicket {
                        any = true;
                        break 'look;
                    }
                }
            }
            if !any {
                continue;
            }
            let mut xrng = PyRandom::keyed(&format!("ortho:thicket:{r0}:{c0}"));
            let extra = extra_n as usize + (xrng.random() < extra_n % 1.0) as usize;
            let gap = spacing * spacing_scale;
            let mut done = 0;
            for _ in 0..extra * 60 {
                if done >= extra {
                    break;
                }
                let Some((x, y, row, col)) = draw(&mut xrng) else { continue };
                if st.noise(x, y) != thicket || !free(&grid, x, y, gap) || !clear(x, y) {
                    continue;
                }
                if wet.as_ref().is_some_and(|g| g.contains_xy(x, y).unwrap_or(false)) {
                    continue; // swamp, not thicket
                }
                add(&mut grid, x, y);
                out.push(plant_one(x, y, &mut xrng, thicket as i64, r0 + row, c0 + col));
                done += 1;
            }
        }
        r0 += cfg.cell_px;
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_random_stream_is_pythons() {
        // random.Random(12345): random() x 2, randrange(7); random.Random(2**40 + 3).random()
        let mut r = PyRandom::new(12345);
        assert_eq!(r.random(), 0.41661987254534116);
        assert_eq!(r.random(), 0.010169169457068361);
        assert_eq!(r.below(7), 6);
        assert_eq!(PyRandom::new((1 << 40) + 3).random(), 0.21978710637116716);
    }

    #[test]
    fn floor_division_is_pythons() {
        assert_eq!(floordiv(7.5, 2.0), 3);
        assert_eq!(floordiv(-7.5, 2.0), -4);
        assert_eq!(floordiv(-0.1, 110.0), -1);
        assert_eq!(floordiv(0.0, 4.0), 0);
        assert_eq!(floordiv(8.0, 4.0), 2);
    }
}

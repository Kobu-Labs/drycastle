//! The tree detector of the `ortho-trees` stage: the per-strip image maths of `ortho_trees.detect_strip`, strips in parallel.
//!
//! Python keeps the photo decoding, the building masks, the training, the suppression of overlapping crowns and the files. The
//! arithmetic follows the Python reference operation by operation (same float widths, same order of the sums), so the result is
//! the reference's: the only freedom is the last bit of the canopy probability (dot product order, `exp`), which the casts and
//! thresholds after it absorb.

use rayon::prelude::*;

pub const N_FEATURES: usize = 17;

pub struct Model {
    pub mu: Vec<f64>,
    pub sd: Vec<f64>,
    pub w: Vec<f64>,
    pub b: f64,
}

/// One crown size: the blur kernels of the ring and of the core, and the crown radius in pixels.
pub struct Blob {
    pub ring: Vec<f32>,
    pub core: Vec<f32>,
    pub rad: f64,
}

pub struct Params {
    pub canopy_p: f64,
    pub green: f32,
    pub wooded_p: f32,
    pub tau_open: f64,
    pub tau_wooded: f64,
    pub green_k: Vec<f32>,
    pub wooded_k: Vec<f32>,
    pub blobs: Vec<Blob>,
}

/// A candidate crown: (score, column, row, index of its crown size).
pub type Cand = (f64, usize, usize, usize);

/// Gaussian blur with the given kernel (odd length), edges repeated: along the rows axis, then along the columns axis.
pub fn smooth(a: &[f32], w: usize, h: usize, k: &[f32]) -> Vec<f32> {
    let r = k.len() / 2;
    let mut t = vec![0f32; w * h];
    t.par_chunks_mut(w).enumerate().for_each(|(y, o)| {
        for (i, &kw) in k.iter().enumerate() {
            let yy = (y + i).saturating_sub(r).min(h - 1);
            let src = &a[yy * w..yy * w + w];
            for x in 0..w {
                o[x] += kw * src[x];
            }
        }
    });
    let mut out = vec![0f32; w * h];
    out.par_chunks_mut(w).enumerate().for_each_init(
        || vec![0f32; w + 2 * r],
        |pad, (y, o)| {
            let src = &t[y * w..y * w + w];
            pad[..r].fill(src[0]);
            pad[r..r + w].copy_from_slice(src);
            pad[r + w..].fill(src[w - 1]);
            for (i, &kw) in k.iter().enumerate() {
                let p = &pad[i..i + w];
                for x in 0..w {
                    o[x] += kw * p[x];
                }
            }
        },
    );
    out
}

fn reflect(i: isize, n: usize) -> usize {
    // numpy "reflect": the edge value is not repeated
    let n = n as isize;
    let mut i = i;
    loop {
        if i < 0 {
            i = -i;
        } else if i >= n {
            i = 2 * (n - 1) - i;
        } else {
            return i as usize;
        }
    }
}

/// Mean over a k x k window (edge-reflected), via an integral image in f64, like the reference.
pub fn box_mean(a: &[f32], w: usize, h: usize, k: usize) -> Vec<f32> {
    let r = (k / 2) as isize;
    let (pw, ph) = (w + 2 * r as usize, h + 2 * r as usize);
    let cw = pw + 1;
    let xs: Vec<usize> = (0..pw as isize).map(|j| reflect(j - r, w)).collect();
    let mut c = vec![0f64; cw * (ph + 1)];
    let mut col = vec![0f64; pw];
    for i in 0..ph {
        let src = &a[reflect(i as isize - r, h) * w..][..w];
        let row = &mut c[(i + 1) * cw..(i + 2) * cw];
        let mut run = 0f64;
        for j in 0..pw {
            col[j] += src[xs[j]] as f64;
            run += col[j];
            row[j + 1] = run;
        }
    }
    let kk = (k * k) as f64;
    let mut out = vec![0f32; w * h];
    out.par_chunks_mut(w).enumerate().for_each(|(y, o)| {
        let (top, bot) = (&c[y * cw..(y + 1) * cw], &c[(y + k) * cw..(y + k + 1) * cw]);
        for x in 0..w {
            let s = bot[x + k] - top[x + k] - bot[x] + top[x];
            o[x] = (s / kk) as f32;
        }
    });
    out
}

/// True where at least `need` of the size x size neighbours (edges repeated) are set: a rank filter on a mask.
pub fn rank_mask(mask: &[bool], w: usize, h: usize, size: usize, need: u32) -> Vec<bool> {
    let m = size / 2;
    let (pw, ph) = (w + 2 * m, h + 2 * m);
    let cw = pw + 1;
    let mut c = vec![0u32; cw * (ph + 1)];
    for i in 0..ph {
        let src = &mask[(i.saturating_sub(m)).min(h - 1) * w..][..w];
        let mut run = 0u32;
        for j in 0..pw {
            run += src[j.saturating_sub(m).min(w - 1)] as u32;
            c[(i + 1) * cw + j + 1] = c[i * cw + j + 1] + run;
        }
    }
    let mut out = vec![false; w * h];
    out.par_chunks_mut(w).enumerate().for_each(|(y, o)| {
        let (top, bot) = (&c[y * cw..(y + 1) * cw], &c[(y + size) * cw..(y + size + 1) * cw]);
        for x in 0..w {
            o[x] = bot[x + size] + top[x] - top[x + size] - bot[x] >= need;
        }
    });
    out
}

struct Planes {
    lum: Vec<f32>,
    exg: Vec<f32>,
    nodata: Vec<bool>,
}

/// The canopy probability of every pixel (the model on the 17 features and their squares) and the planes detection reuses.
fn probability(rgb: &[u8], w: usize, h: usize, model: &Model) -> (Vec<f64>, Planes) {
    let n = w * h;
    let (mut lum, mut exg, mut rt, mut bt) = (vec![0f32; n], vec![0f32; n], vec![0f32; n], vec![0f32; n]);
    let mut nodata = vec![false; n];
    for i in 0..n {
        let (r, g, b) = (rgb[3 * i] as f32, rgb[3 * i + 1] as f32, rgb[3 * i + 2] as f32);
        let tot = (r + g + b).max(1.0);
        lum[i] = 0.3f32 * r + 0.59f32 * g + 0.11f32 * b;
        exg[i] = (2.0 * g - r - b) / tot;
        rt[i] = r / tot;
        bt[i] = b / tot;
        nodata[i] = rgb[3 * i] as u32 + rgb[3 * i + 1] as u32 + rgb[3 * i + 2] as u32 == 0;
    }
    // z = sum over the features j of ((x_j - mu_j) / sd_j) w_j, then the same for the squares, then b
    let (mut z1, mut z2) = (vec![0f64; n], vec![0f64; n]);
    let mut j = 0;
    let mut add = |f: &(dyn Fn(usize) -> f32 + Sync)| {
        let (mu, sd, wt) = (model.mu[j], model.sd[j], model.w[j]);
        let (mu2, sd2, wt2) = (model.mu[N_FEATURES + j], model.sd[N_FEATURES + j], model.w[N_FEATURES + j]);
        z1.par_iter_mut().zip(z2.par_iter_mut()).enumerate().for_each(|(i, (a, b))| {
            let x = f(i);
            *a += ((x as f64 - mu) / sd) * wt;
            *b += (((x * x) as f64 - mu2) / sd2) * wt2;
        });
        j += 1;
    };
    add(&|i| lum[i] / 255.0);
    add(&|i| rt[i]);
    add(&|i| bt[i]);
    add(&|i| exg[i]);
    let lum2: Vec<f32> = lum.iter().map(|v| v * v).collect();
    for k in [5, 13, 31] {
        let m = box_mean(&lum, w, h, k);
        let m2 = box_mean(&lum2, w, h, k);
        add(&|i| m[i] / 255.0);
        add(&|i| (m2[i] - m[i] * m[i]).max(0.0).sqrt() / 64.0);
        add(&|i| lum[i] / 255.0 - m[i] / 255.0);
    }
    let mut grad = vec![0f32; n];
    grad.par_chunks_mut(w).enumerate().for_each(|(y, o)| {
        for x in 0..w {
            let at = |yy: usize, xx: usize| lum[yy * w + xx];
            let gy = if h == 1 { 0.0 } else if y == 0 { at(1, x) - at(0, x) } else if y == h - 1 { at(y, x) - at(y - 1, x) } else { (at(y + 1, x) - at(y - 1, x)) / 2.0 };
            let gx = if w == 1 { 0.0 } else if x == 0 { at(y, 1) - at(y, 0) } else if x == w - 1 { at(y, x) - at(y, x - 1) } else { (at(y, x + 1) - at(y, x - 1)) / 2.0 };
            o[x] = gx.hypot(gy);
        }
    });
    for k in [5, 13] {
        let m = box_mean(&grad, w, h, k);
        add(&|i| m[i] / 32.0);
    }
    for k in [13, 31] {
        let m = box_mean(&exg, w, h, k);
        add(&|i| m[i]);
    }
    let b = model.b;
    let prob: Vec<f64> = z1.par_iter().zip(z2.par_iter()).map(|(a, c)| 1.0 / (1.0 + (-(a + c + b)).exp())).collect();
    (prob, Planes { lum, exg, nodata })
}

/// (canopy mask, candidate crowns) of one strip of the photo: `ortho_trees.detect_strip`.
pub fn detect_strip(rgb: &[u8], w: usize, h: usize, built: &[u8], model: &Model, p: &Params) -> (Vec<bool>, Vec<Cand>) {
    let n = w * h;
    let (prob, pl) = probability(rgb, w, h, model);
    let nodata = rank_mask(&pl.nodata, w, h, 21, 1);
    let raw: Vec<bool> = (0..n).map(|i| prob[i] > p.canopy_p && built[i] == 0 && !nodata[i]).collect();
    let canopy = rank_mask(&raw, w, h, 7, 25);

    let green = smooth(&pl.exg, w, h, &p.green_k);
    let prob32: Vec<f32> = prob.iter().map(|&v| v as f32).collect();
    drop(prob);
    let wooded = smooth(&prob32, w, h, &p.wooded_k);
    let allowed: Vec<bool> = (0..n).map(|i| green[i] > p.green && built[i] == 0 && !canopy[i] && !nodata[i]).collect();
    let (low, open) = (p.tau_wooded.min(p.tau_open), p.tau_open);

    let mut cands = vec![];
    for (si, blob) in p.blobs.iter().enumerate() {
        let ring = smooth(&pl.lum, w, h, &blob.ring);
        let core = smooth(&pl.lum, w, h, &blob.core);
        let score: Vec<f32> = ring.iter().zip(&core).map(|(r, c)| (r - c) / r.max(25.0)).collect();
        let r = blob.rad as usize;
        // the maximum of the square neighbourhood, separable: down the columns first, then along the row where it is needed
        let mut colmax = vec![0f32; n];
        colmax.par_chunks_mut(w).enumerate().for_each(|(y, o)| {
            o.copy_from_slice(&score[y * w..y * w + w]);
            for yy in y.saturating_sub(r)..=(y + r).min(h - 1) {
                let src = &score[yy * w..yy * w + w];
                for x in 0..w {
                    o[x] = o[x].max(src[x]);
                }
            }
        });
        let rows: Vec<Vec<Cand>> = (0..h)
            .into_par_iter()
            .map(|y| {
                let mut out = vec![];
                for x in 0..w {
                    let i = y * w + x;
                    let s = score[i];
                    if !allowed[i] || s as f64 <= (if wooded[i] > p.wooded_p { low } else { open }) {
                        continue;
                    }
                    if colmax[y * w + x.saturating_sub(r)..=y * w + (x + r).min(w - 1)].iter().all(|&m| s >= m) {
                        out.push((s as f64, x, y, si));
                    }
                }
                out
            })
            .collect();
        cands.extend(rows.into_iter().flatten());
    }
    (canopy, cands)
}

/// The whole photo in strips of `strip_rows` rows plus `margin` each side: (canopy mask, candidates with photo rows).
///
/// `built` holds the building mask of every strip (margins included) one after the other. Candidates keep the reference's
/// order: strip by strip, crown size by crown size, row by row.
pub fn detect(rgb: &[u8], w: usize, h: usize, built: &[u8], strip_rows: usize, margin: usize, model: &Model, p: &Params) -> (Vec<u8>, Vec<Cand>) {
    let mut strips = vec![];
    let mut at = 0;
    let mut y0 = 0;
    while y0 < h {
        let (ya, yb) = (y0.saturating_sub(margin), (y0 + strip_rows + margin).min(h));
        strips.push((y0, ya, yb, at));
        at += (yb - ya) * w;
        y0 += strip_rows;
    }
    let done: Vec<(Vec<bool>, Vec<Cand>)> = strips
        .par_iter()
        .map(|&(_, ya, yb, at)| detect_strip(&rgb[ya * w * 3..yb * w * 3], w, yb - ya, &built[at..at + (yb - ya) * w], model, p))
        .collect();
    let mut canopy = vec![0u8; w * h];
    let mut cands = vec![];
    for (&(y0, ya, _, _), (mask, found)) in strips.iter().zip(done) {
        let y1 = (y0 + strip_rows).min(h);
        for y in y0..y1 {
            for x in 0..w {
                canopy[y * w + x] = mask[(y - ya) * w + x] as u8;
            }
        }
        cands.extend(found.into_iter().map(|(s, c, r, si)| (s, c, r + ya, si)).filter(|c| c.2 >= y0 && c.2 < y0 + strip_rows));
    }
    (canopy, cands)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn kernel(sigma: f64) -> Vec<f32> {
        let r = ((3.0 * sigma) as i64 + 1).max(1);
        let k: Vec<f64> = (-r..=r).map(|i| (-0.5 * (i as f64 / sigma).powi(2)).exp()).collect();
        let s: f64 = k.iter().sum();
        k.iter().map(|v| (v / s) as f32).collect()
    }

    #[test]
    fn smooth_keeps_a_constant_and_spreads_an_impulse() {
        let flat = smooth(&vec![7.0; 40 * 30], 40, 30, &kernel(3.0));
        assert!(flat.iter().all(|v| (v - 7.0).abs() < 1e-4));
        let mut a = vec![0f32; 61 * 61];
        a[30 * 61 + 30] = 1.0;
        let s = smooth(&a, 61, 61, &kernel(3.0));
        assert!((s.iter().sum::<f32>() - 1.0).abs() < 1e-4);
        assert!((s[30 * 61 + 20] - s[20 * 61 + 30]).abs() < 1e-7);
    }

    #[test]
    fn box_mean_is_the_mean_of_the_reflected_window() {
        let (w, h) = (25, 20);
        let a: Vec<f32> = (0..w * h).map(|i| ((i * 7919) % 101) as f32 / 101.0).collect();
        let got = box_mean(&a, w, h, 5);
        let brute = |y: isize, x: isize| {
            let mut s = 0.0;
            for dy in -2..=2 {
                for dx in -2..=2 {
                    s += a[reflect(y + dy, h) * w + reflect(x + dx, w)] as f64;
                }
            }
            (s / 25.0) as f32
        };
        for (y, x) in [(0, 0), (7, 9), (19, 24), (1, 23)] {
            assert!((got[y * w + x] - brute(y as isize, x as isize)).abs() < 1e-5);
        }
    }

    #[test]
    fn rank_mask_counts_the_neighbours_with_repeated_edges() {
        let (w, h) = (9, 9);
        let mut m = vec![false; w * h];
        m[0] = true; // a corner: repeated edges count it 4 times in a 3 x 3 window at the corner
        assert!(rank_mask(&m, w, h, 3, 4)[0]);
        assert!(!rank_mask(&m, w, h, 3, 5)[0]);
        assert!(rank_mask(&m, w, h, 3, 1)[w + 1] && !rank_mask(&m, w, h, 3, 1)[2 * w + 2]);
    }

    #[test]
    fn the_strips_do_not_change_the_result_away_from_the_cuts() {
        // a dark green disc on a lawn, detected with one strip and with strips that cut the photo elsewhere
        let (w, h) = (120, 400);
        let mut rgb = vec![0u8; w * h * 3];
        for y in 0..h {
            for x in 0..w {
                let tree = ((x as f64 - 60.0).powi(2) + (y as f64 - 200.0).powi(2)).sqrt() < 6.0;
                rgb[(y * w + x) * 3..][..3].copy_from_slice(if tree { &[50, 100, 40] } else { &[120, 160, 70] });
            }
        }
        let n = 2 * N_FEATURES;
        let model = Model { mu: vec![0.0; n], sd: vec![1.0; n], w: vec![0.0; n], b: -30.0 };
        let p = Params {
            canopy_p: 0.5, green: 0.11, wooded_p: 0.12, tau_open: 0.18, tau_wooded: 0.10, green_k: kernel(2.5), wooded_k: kernel(13.3),
            blobs: vec![Blob { ring: kernel(2.4 * 4.2), core: kernel(4.2), rad: 5.9 }],
        };
        let one = detect(&rgb, w, h, &vec![0u8; w * h], 1024, 64, &model, &p);
        let strip_px = |rows: usize| (0..h).step_by(rows).map(|y0| ((y0 + rows + 64).min(h) - y0.saturating_sub(64)) * w).sum::<usize>();
        let cut = detect(&rgb, w, h, &vec![0u8; strip_px(128)], 128, 64, &model, &p);
        assert_eq!(one.1.len(), 1);
        assert_eq!((one.1[0].1, one.1[0].2), (60, 200));
        assert_eq!(one.1, cut.1);
        assert!(one.0.iter().all(|&c| c == 0));
    }
}

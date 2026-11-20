//! Python's random machinery, bit for bit, for the recipes that are a port of a Python twin (crop.rs): the rest of
//! `random.Random` (:class:`crate::planting::PyRandom` is its Mersenne Twister), numpy's default generator (PCG64 seeded
//! through a `SeedSequence`), Python's `round(x, n)` and `math.hypot`/`math.dist`.

use crate::planting::PyRandom;

impl PyRandom {
    /// `getrandbits(64)`: two words, the first the low one.
    pub fn bits64(&mut self) -> u64 {
        let lo = self.next_u32() as u64;
        let hi = self.next_u32() as u64;
        lo | hi << 32
    }

    pub fn bits32(&mut self) -> u32 {
        self.next_u32()
    }

    pub fn uniform(&mut self, a: f64, b: f64) -> f64 {
        a + (b - a) * self.random()
    }

    /// `randint(a, b)`, both ends included.
    pub fn randint(&mut self, a: i64, b: i64) -> i64 {
        a + self.below((b + 1 - a) as u32) as i64
    }

    /// `random.shuffle`.
    pub fn shuffle<T>(&mut self, v: &mut [T]) {
        for i in (1..v.len()).rev() {
            let j = self.below(i as u32 + 1) as usize;
            v.swap(i, j);
        }
    }

    /// `random.sample(population, k)` of a list of at most 21 (the pool method): the indices picked, in order.
    pub fn sample(&mut self, n: usize, k: usize) -> Vec<usize> {
        assert!(n <= 21 && k <= n, "sample: only the pool method is ported");
        let mut pool: Vec<usize> = (0..n).collect();
        let mut out = Vec::with_capacity(k);
        for i in 0..k {
            let j = self.below((n - i) as u32) as usize;
            out.push(pool[j]);
            pool[j] = pool[n - i - 1];
        }
        out
    }

    /// `random.choices(population, weights)[0]`: the index drawn (one `random()`, a `bisect` into the running sums).
    pub fn choice_weighted(&mut self, weights: &[f64]) -> usize {
        let mut cum = Vec::with_capacity(weights.len());
        let mut s = 0.0;
        for w in weights {
            s += w;
            cum.push(s);
        }
        let total = cum[cum.len() - 1] + 0.0;
        let x = self.random() * total;
        let (mut lo, mut hi) = (0usize, cum.len() - 1);
        while lo < hi {
            let mid = (lo + hi) / 2;
            if x < cum[mid] {
                hi = mid;
            } else {
                lo = mid + 1;
            }
        }
        lo
    }
}

// --- numpy -----------------------------------------------------------------------------------------------------

const MULT: u128 = 0x2360ED051FC65DA44385DF649FCCF645;

fn hashmix(value: u32, hc: &mut u32, mult: u32) -> u32 {
    let mut v = value ^ *hc;
    *hc = hc.wrapping_mul(mult);
    v = v.wrapping_mul(*hc);
    v ^ (v >> 16)
}

fn mix(x: u32, y: u32) -> u32 {
    let r = 0xca01f9ddu32.wrapping_mul(x).wrapping_sub(0x4973f715u32.wrapping_mul(y));
    r ^ (r >> 16)
}

/// `np.random.SeedSequence(entropy).generate_state(4, np.uint64)` for an integer seed.
fn seed_state(seed: u64) -> [u64; 4] {
    let entropy: Vec<u32> = if seed >> 32 != 0 { vec![seed as u32, (seed >> 32) as u32] } else { vec![seed as u32] };
    let mut pool = [0u32; 4];
    let mut hc = 0x43b0d7e5u32;
    for (i, p) in pool.iter_mut().enumerate() {
        *p = hashmix(entropy.get(i).copied().unwrap_or(0), &mut hc, 0x931e8875);
    }
    for src in 0..4 {
        for dst in 0..4 {
            if src != dst {
                let h = hashmix(pool[src], &mut hc, 0x931e8875);
                pool[dst] = mix(pool[dst], h);
            }
        }
    }
    for e in entropy.iter().skip(4) {
        for dst in 0..4 {
            let h = hashmix(*e, &mut hc, 0x931e8875);
            pool[dst] = mix(pool[dst], h);
        }
    }
    let mut hc = 0x8b51f9ddu32;
    let mut words = [0u32; 8];
    for (i, w) in words.iter_mut().enumerate() {
        let mut d = pool[i % 4] ^ hc;
        hc = hc.wrapping_mul(0x58f38ded);
        d = d.wrapping_mul(hc);
        *w = d ^ (d >> 16);
    }
    [0, 1, 2, 3].map(|k| words[2 * k] as u64 | (words[2 * k + 1] as u64) << 32)
}

/// `np.random.default_rng(seed)`: PCG64 (XSL-RR 128/64).
pub struct NpGen {
    state: u128,
    inc: u128,
}

impl NpGen {
    pub fn new(seed: u64) -> NpGen {
        let v = seed_state(seed);
        let initstate = (v[0] as u128) << 64 | v[1] as u128;
        let initseq = (v[2] as u128) << 64 | v[3] as u128;
        let mut g = NpGen { state: 0, inc: (initseq << 1) | 1 };
        g.step();
        g.state = g.state.wrapping_add(initstate);
        g.step();
        g
    }

    /// `default_rng(seeded(key).getrandbits(64))`, the `_gen(pid, purpose)` of detail.py.
    pub fn keyed(key: &str) -> NpGen {
        NpGen::new(PyRandom::keyed(key).bits64())
    }

    #[inline]
    fn step(&mut self) {
        self.state = self.state.wrapping_mul(MULT).wrapping_add(self.inc);
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        self.step();
        let s = self.state;
        (((s >> 64) as u64) ^ (s as u64)).rotate_right((s >> 122) as u32)
    }

    /// `g.random()`: a double in [0, 1).
    #[inline]
    pub fn random(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 * (1.0 / 9007199254740992.0)
    }
}

/// numpy's `np.arange(start, stop, step)` values: the first two exact, the rest `start + i * (second - first)`.
pub fn arange(start: f64, stop: f64, step: f64) -> Vec<f64> {
    let n = ((stop - start) / step).ceil();
    if !(n > 0.0) {
        return vec![];
    }
    let n = n as usize;
    let mut v = Vec::with_capacity(n);
    v.push(start);
    if n > 1 {
        let second = start + step;
        v.push(second);
        let delta = second - start;
        for i in 2..n {
            v.push(start + i as f64 * delta);
        }
    }
    v
}

// --- rounding and norms ----------------------------------------------------------------------------------------

/// Python's `round(x, n)`: the correctly rounded decimal (ties to even on the exact binary value).
pub fn py_round(x: f64, n: usize) -> f64 {
    if !x.is_finite() {
        return x;
    }
    format!("{:.*}", n, x).parse().unwrap_or(x)
}

/// Python's `round(x)` for a float: half to even.
pub fn py_round0(x: f64) -> i64 {
    x.round_ties_even() as i64
}

fn dl_mul(x: f64, y: f64) -> (f64, f64) {
    let z = x * y;
    (z, x.mul_add(y, -z))
}

fn dl_fast_sum(a: f64, b: f64) -> (f64, f64) {
    let x = a + b;
    (x, (a - x) + b)
}

/// CPython's `math.hypot(x, y)` (`vector_norm`: scaled, error-compensated, nearly always the correctly rounded norm).
pub fn py_hypot(x: f64, y: f64) -> f64 {
    let (ax, ay) = (x.abs(), y.abs());
    let max = ax.max(ay);
    if max.is_infinite() {
        return max;
    }
    if x.is_nan() || y.is_nan() {
        return f64::NAN;
    }
    if max == 0.0 {
        return max;
    }
    let (_, max_e) = frexp(max);
    if max_e < -1023 {
        return (ax * 2f64.powi(1074)).hypot(ay * 2f64.powi(1074)) / 2f64.powi(1074);
    }
    let scale = 2f64.powi(-max_e);
    let (mut csum, mut frac1, mut frac2) = (1.0f64, 0.0f64, 0.0f64);
    for v in [x, y] {
        let xs = v * scale;
        let pr = dl_mul(xs, xs);
        let sm = dl_fast_sum(csum, pr.0);
        csum = sm.0;
        frac1 += pr.1;
        frac2 += sm.1;
    }
    let mut h = (csum - 1.0 + (frac1 + frac2)).sqrt();
    let pr = dl_mul(-h, h);
    let sm = dl_fast_sum(csum, pr.0);
    csum = sm.0;
    frac1 += pr.1;
    frac2 += sm.1;
    let t = csum - 1.0 + (frac1 + frac2);
    h += t / (2.0 * h);
    h / scale
}

fn frexp(x: f64) -> (f64, i32) {
    if x == 0.0 || !x.is_finite() {
        return (x, 0);
    }
    let bits = x.to_bits();
    let e = ((bits >> 52) & 0x7ff) as i32;
    if e == 0 {
        let (m, e2) = frexp(x * 2f64.powi(64));
        return (m, e2 - 64);
    }
    let m = f64::from_bits((bits & !(0x7ffu64 << 52)) | (1022u64 << 52));
    (m, e - 1022)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn random_methods_are_pythons() {
        let mut r = PyRandom::new(777);
        assert_eq!(r.uniform(-1.0, 1.0), -0.5413318209969384);
        assert_eq!(r.randint(2, 9), 9);
        assert_eq!(r.bits32(), 4260128254);
        assert_eq!(r.bits64(), 10616263673793770612);
        assert_eq!(r.below(1000), 276);
        let mut x: Vec<i32> = (0..10).collect();
        r.shuffle(&mut x);
        assert_eq!(x, [7, 1, 2, 6, 4, 9, 3, 0, 8, 5]);
        let names: Vec<char> = "abcdefghijklm".chars().collect();
        let picked: Vec<char> = r.sample(13, 4).into_iter().map(|i| names[i]).collect();
        assert_eq!(picked, ['b', 'm', 'l', 'f']);
        let w = [0.5, 1.0, 2.0, 0.25];
        let c: Vec<usize> = (0..5).map(|_| r.choice_weighted(&w) + 1).collect();
        assert_eq!(c, [1, 3, 4, 1, 3]);
        let mut s = PyRandom::keyed("P-1:detail:crop:plan");
        assert_eq!(s.random(), 0.15211878872800522);
        assert_eq!(s.bits32(), 2989071851);
    }

    #[test]
    fn numpy_generator_is_numpys() {
        let mut g = NpGen::new(12345);
        assert_eq!(g.random(), 0.22733602246716966);
        assert_eq!(g.random(), 0.31675833970975287);
        assert_eq!([g.random(), g.random(), g.random(), g.random()], [0.7973654573327341, 0.6762546707509746, 0.391109550601909, 0.33281392786638453]);
        let mut g = NpGen::new((1 << 40) + 3);
        assert_eq!((g.random(), g.random()), (0.7736560850714245, 0.9958573802012665));
        let mut g = NpGen::new(0);
        assert_eq!((g.random(), g.random()), (0.6369616873214543, 0.2697867137638703));
        let mut g = NpGen::new(4294967295);
        assert_eq!((g.random(), g.random()), (0.2519435680449965, 0.21906579628685663));
        let mut g = NpGen::new((1 << 63) + 5);
        assert_eq!((g.random(), g.random()), (0.48023310532329777, 0.9041733770137489));
    }

    #[test]
    fn round_and_norm_are_pythons() {
        for (v, r3, r2) in [(0.0625, 0.062, 0.06), (0.125, 0.125, 0.12), (2.675, 2.675, 2.67), (1.0005, 1.0, 1.0), (-0.0625, -0.062, -0.06),
                            (12.34567, 12.346, 12.35), (0.375, 0.375, 0.38), (2.5, 2.5, 2.5), (1.015, 1.015, 1.01), (0.285, 0.285, 0.28), (8.345, 8.345, 8.35)] {
            assert_eq!((py_round(v, 3), py_round(v, 2)), (r3, r2), "{v}");
        }
        assert_eq!(py_hypot(3.1, 4.2), 5.220153254455275);
        assert_eq!(py_hypot(18.7 - 1.1, 3.9 - 2.3), 17.672577627499614);
        assert_eq!(py_hypot(0.0, 0.0), 0.0);
        assert_eq!(py_hypot(-3.0, 4.0), 5.0);
    }

    #[test]
    fn arange_is_numpys() {
        // np.arange(0.5, 3.2, 0.7) -> [0.5, 1.2, 1.9000000000000001, 2.6]
        assert_eq!(arange(0.5, 3.2, 0.7), [0.5, 1.2, 0.5 + 2.0 * (1.2 - 0.5), 0.5 + 3.0 * (1.2 - 0.5)]);
        assert!(arange(1.0, 1.0, 1.0).is_empty());
    }
}


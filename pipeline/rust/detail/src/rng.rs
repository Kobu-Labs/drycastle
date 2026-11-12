//! Seeded random streams. Every stream starts from ``sha256("<id>:detail:<purpose>")`` (the design of detail.py) and
//! is a pure function of its key, so the result never depends on the order or thread that draws it. Lattice
//! features (clutter grids, blob lattices) derive one small stream per lattice cell from ``(seed, ix, iy)``.

use sha2::{Digest, Sha256};

/// First 8 bytes of the sha256 of ``key``, big endian (props.seeded).
pub fn seed_of(key: &str) -> u64 {
    let d = Sha256::digest(key.as_bytes());
    u64::from_be_bytes([d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7]])
}

#[inline]
pub fn splitmix(x: &mut u64) -> u64 {
    *x = x.wrapping_add(0x9E3779B97F4A7C15);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

/// A 64-bit hash of three words (finalizer-style mixing).
#[inline]
pub fn mix3(a: u64, b: u64, c: u64) -> u64 {
    let mut s = a ^ 0xA076_1D64_78BD_642F;
    let mut h = splitmix(&mut s);
    h ^= b.wrapping_mul(0xE703_7ED1_A0B4_28DB);
    s = h;
    h = splitmix(&mut s);
    h ^= c.wrapping_mul(0x8EBC_6AF0_9C88_C6E3);
    s = h;
    splitmix(&mut s)
}

/// xoshiro256++.
#[derive(Clone)]
pub struct Rng {
    s: [u64; 4],
}

impl Rng {
    pub fn new(seed: u64) -> Rng {
        let mut x = seed;
        let s = [splitmix(&mut x), splitmix(&mut x), splitmix(&mut x), splitmix(&mut x)];
        Rng { s }
    }

    pub fn keyed(key: &str) -> Rng {
        Rng::new(seed_of(key))
    }

    /// The stream of one lattice cell.
    pub fn cell(seed: u64, ix: i64, iy: i64) -> Rng {
        Rng::new(mix3(seed, ix as u64, iy as u64))
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let r = self.s[0].wrapping_add(self.s[3]).rotate_left(23).wrapping_add(self.s[0]);
        let t = self.s[1] << 17;
        self.s[2] ^= self.s[0];
        self.s[3] ^= self.s[1];
        self.s[1] ^= self.s[2];
        self.s[0] ^= self.s[3];
        self.s[2] ^= t;
        self.s[3] = self.s[3].rotate_left(45);
        r
    }

    /// Uniform in [0, 1).
    #[inline]
    pub fn f(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64
    }

    /// Uniform integer in [0, n).
    #[inline]
    pub fn below(&mut self, n: usize) -> usize {
        if n <= 1 {
            return 0;
        }
        ((self.next_u64() >> 11) as f64 / (1u64 << 53) as f64 * n as f64) as usize % n
    }

    /// Uniform integer in [lo, hi] (both inclusive).
    pub fn int(&mut self, lo: i64, hi: i64) -> i64 {
        lo + self.below((hi - lo + 1).max(1) as usize) as i64
    }

    pub fn shuffle<T>(&mut self, v: &mut [T]) {
        for i in (1..v.len()).rev() {
            let j = self.below(i + 1);
            v.swap(i, j);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seed_matches_python() {
        // python: int.from_bytes(hashlib.sha256(b"P-0001:detail:dirt").digest()[:8], "big")
        let d = Sha256::digest(b"abc");
        assert_eq!(u64::from_be_bytes(d[..8].try_into().unwrap()), 0xba7816bf8f01cfea);
        assert_eq!(seed_of("abc"), 0xba7816bf8f01cfea);
    }

    #[test]
    fn streams_are_pure() {
        let mut a = Rng::cell(7, 3, -4);
        let mut b = Rng::cell(7, 3, -4);
        assert_eq!(a.next_u64(), b.next_u64());
        let mut c = Rng::cell(7, 3, -5);
        assert_ne!(Rng::cell(7, 3, -4).next_u64(), c.next_u64());
        let x = Rng::new(1).f();
        assert!((0.0..1.0).contains(&x));
    }
}

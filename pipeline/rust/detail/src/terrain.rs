//! The terrain height grid (terrain.py): meshed cells, triangle surface height, the split of a cell into triangles.

pub struct Terrain {
    pub step: f64,
    pub i0: i64,
    pub j0: i64,
    pub ncols: usize,
    pub nrows: usize,
    /// ``(nrows + 1) * (ncols + 1)`` heights, row by row.
    pub heights: Vec<f64>,
    pub valid: Vec<bool>,
}

impl Terrain {
    pub fn new(chunk_m: f64, n: i64, i0: i64, j0: i64, rows: usize, cols: usize, heights: Vec<f64>, valid: Vec<bool>) -> Terrain {
        assert_eq!(heights.len(), rows * cols);
        assert_eq!(valid.len(), rows * cols);
        Terrain { step: chunk_m / n as f64, i0, j0, ncols: cols - 1, nrows: rows - 1, heights, valid }
    }

    #[inline]
    fn at(&self, r: usize, c: usize) -> f64 {
        self.heights[r * (self.ncols + 1) + c]
    }

    #[inline]
    fn ok_at(&self, r: usize, c: usize) -> bool {
        self.valid[r * (self.ncols + 1) + c]
    }

    /// Whether the cell with these indices (from the first grid column and row) has all four corners valid.
    #[inline]
    pub fn cell_valid(&self, ci: usize, cj: usize) -> bool {
        self.ok_at(cj, ci) && self.ok_at(cj, ci + 1) && self.ok_at(cj + 1, ci) && self.ok_at(cj + 1, ci + 1)
    }

    /// Whether the point stands on a meshed cell (``terrain_ok``).
    #[inline]
    pub fn ok(&self, x: f64, y: f64) -> bool {
        let gx = (x / self.step - self.i0 as f64).floor();
        let gy = (y / self.step - self.j0 as f64).floor();
        if gx < 0.0 || gy < 0.0 || gx >= self.ncols as f64 || gy >= self.nrows as f64 {
            return false;
        }
        self.cell_valid(gx as usize, gy as usize)
    }

    /// Height of the triangle surface (the diagonal of each cell is the flatter one), clamped at the grid edge.
    pub fn surface(&self, x: f64, y: f64) -> f64 {
        let gx = (x / self.step - self.i0 as f64).clamp(0.0, self.ncols as f64 - 1e-9);
        let gy = (y / self.step - self.j0 as f64).clamp(0.0, self.nrows as f64 - 1e-9);
        let (ci, cj) = (gx as usize, gy as usize);
        let (fx, fy) = (gx - ci as f64, gy - cj as f64);
        let (h00, h10, h01, h11) = (self.at(cj, ci), self.at(cj, ci + 1), self.at(cj + 1, ci), self.at(cj + 1, ci + 1));
        if (h10 - h01).abs() < (h00 - h11).abs() {
            if fx + fy <= 1.0 {
                h00 + fx * (h10 - h00) + fy * (h01 - h00)
            } else {
                h11 + (1.0 - fx) * (h01 - h11) + (1.0 - fy) * (h10 - h11)
            }
        } else if fx >= fy {
            h00 + fx * (h10 - h00) + fy * (h11 - h10)
        } else {
            h00 + fx * (h11 - h01) + fy * (h01 - h00)
        }
    }

    /// The two triangles (xy) of a meshed cell, in the mesh's own split.
    pub fn cell_triangles(&self, ci: usize, cj: usize) -> [[[f64; 2]; 3]; 2] {
        let s = self.step;
        let (x, y) = ((self.i0 + ci as i64) as f64 * s, (self.j0 + cj as i64) as f64 * s);
        let (v00, v10, v01, v11) = ([x, y], [x + s, y], [x, y + s], [x + s, y + s]);
        if (self.at(cj, ci + 1) - self.at(cj + 1, ci)).abs() < (self.at(cj, ci) - self.at(cj + 1, ci + 1)).abs() {
            [[v00, v10, v01], [v10, v11, v01]]
        } else {
            [[v00, v10, v11], [v00, v11, v01]]
        }
    }

    /// Cell index range (clamped to the grid) covering a bounding box: ``(ci0, ci1, cj0, cj1)`` inclusive, or None.
    pub fn cell_range(&self, b: [f64; 4]) -> Option<(usize, usize, usize, usize)> {
        let s = self.step;
        let ci0 = ((b[0] / s).floor() as i64 - self.i0).max(0);
        let ci1 = ((b[2] / s).floor() as i64 - self.i0).min(self.ncols as i64 - 1);
        let cj0 = ((b[1] / s).floor() as i64 - self.j0).max(0);
        let cj1 = ((b[3] / s).floor() as i64 - self.j0).min(self.nrows as i64 - 1);
        if ci1 < ci0 || cj1 < cj0 {
            None
        } else {
            Some((ci0 as usize, ci1 as usize, cj0 as usize, cj1 as usize))
        }
    }

    pub fn cell_box(&self, ci: usize, cj: usize) -> [f64; 4] {
        let s = self.step;
        let (x, y) = ((self.i0 + ci as i64) as f64 * s, (self.j0 + cj as i64) as f64 * s);
        [x, y, x + s, y + s]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A 3x3 vertex grid (2x2 cells of 10 m) whose height is the plane z = 2x + 3y: the triangle surface must reproduce it.
    #[test]
    fn surface_reproduces_a_plane_and_cells_split() {
        let mut h = vec![];
        for r in 0..3 {
            for c in 0..3 {
                h.push(2.0 * (c as f64 * 10.0) + 3.0 * (r as f64 * 10.0));
            }
        }
        let t = Terrain::new(20.0, 2, 0, 0, 3, 3, h, vec![true; 9]);
        for (x, y) in [(1.0, 1.0), (9.0, 2.0), (3.0, 8.0), (14.0, 12.0), (19.0, 19.0)] {
            assert!((t.surface(x, y) - (2.0 * x + 3.0 * y)).abs() < 1e-9, "{x} {y}");
        }
        assert!(t.ok(5.0, 5.0));
        assert!(!t.ok(25.0, 5.0));
        let tris = t.cell_triangles(1, 0);
        // two triangles tile the 10 m cell
        let area: f64 = tris.iter().map(|q| ((q[1][0] - q[0][0]) * (q[2][1] - q[0][1]) - (q[2][0] - q[0][0]) * (q[1][1] - q[0][1])).abs() / 2.0).sum();
        assert!((area - 100.0).abs() < 1e-9);
        assert_eq!(t.cell_range([5.0, 5.0, 12.0, 25.0]), Some((0, 1, 0, 1)));
    }
}

//! Spatial structures built once and read from every thread: point-in-region tests, nearest-edge distances and
//! bounding-box candidate lookups. All of them are plain data (no GEOS handles), so they are `Sync`.

use crate::geom::{self, PolyC, Pt, G};
use rstar::primitives::{GeomWithData, Rectangle};
use rstar::{PointDistance, RTree, RTreeObject, AABB};

/// One edge of a polygon, in an R-tree so that a ray can ask for the edges it may cross.
#[derive(Clone)]
struct Edge([f64; 4]);

impl RTreeObject for Edge {
    type Envelope = AABB<[f64; 2]>;
    fn envelope(&self) -> Self::Envelope {
        AABB::from_corners([self.0[0], self.0[1]], [self.0[2], self.0[3]])
    }
}

/// One polygon with an even-odd crossing test (a ray to the right). Big polygons keep their edges in an R-tree, so a test
/// touches only the edges the ray can cross, whatever the shape (a long thin dike is as cheap as a square).
struct RPoly {
    bb: [f64; 4],
    edges: Vec<[f64; 4]>,
    tree: Option<RTree<Edge>>,
}

impl RPoly {
    fn new(p: &PolyC) -> RPoly {
        let bb = p.bounds();
        let mut edges = vec![];
        for ring in std::iter::once(&p.ext).chain(p.holes.iter()) {
            for w in ring.windows(2) {
                if w[0][1] != w[1][1] {
                    edges.push([w[0][0], w[0][1], w[1][0], w[1][1]]);
                }
            }
        }
        let tree = if edges.len() > 24 { Some(RTree::bulk_load(edges.iter().map(|e| Edge(*e)).collect())) } else { None };
        RPoly { bb, edges, tree }
    }

    #[inline]
    fn crosses(e: &[f64; 4], x: f64, y: f64) -> bool {
        if (e[1] > y) != (e[3] > y) {
            let xi = e[0] + (y - e[1]) * (e[2] - e[0]) / (e[3] - e[1]);
            xi > x
        } else {
            false
        }
    }

    /// Whether the polygon may reach into the box: an edge in it, or the box's centre inside the polygon.
    fn touches_box(&self, b: [f64; 4]) -> bool {
        if self.bb[0] > b[2] || self.bb[2] < b[0] || self.bb[1] > b[3] || self.bb[3] < b[1] {
            return false;
        }
        let edge_in = match &self.tree {
            Some(t) => t.locate_in_envelope_intersecting(&AABB::from_corners([b[0], b[1]], [b[2], b[3]])).next().is_some(),
            None => self.edges.iter().any(|e| e[0].min(e[2]) <= b[2] && e[0].max(e[2]) >= b[0] && e[1].min(e[3]) <= b[3] && e[1].max(e[3]) >= b[1]),
        };
        edge_in || self.contains((b[0] + b[2]) * 0.5, (b[1] + b[3]) * 0.5)
    }

    fn contains(&self, x: f64, y: f64) -> bool {
        if x < self.bb[0] || x > self.bb[2] || y < self.bb[1] || y > self.bb[3] {
            return false;
        }
        let mut inside = false;
        match &self.tree {
            None => {
                for e in &self.edges {
                    if Self::crosses(e, x, y) {
                        inside = !inside;
                    }
                }
            }
            Some(t) => {
                for e in t.locate_in_envelope_intersecting(&AABB::from_corners([x, y], [self.bb[2] + 1.0, y])) {
                    if Self::crosses(&e.0, x, y) {
                        inside = !inside;
                    }
                }
            }
        }
        inside
    }
}

type BoxItem = GeomWithData<Rectangle<[f64; 2]>, usize>;

fn rect_of(b: [f64; 4]) -> Rectangle<[f64; 2]> {
    Rectangle::from_corners([b[0], b[1]], [b[2], b[3]])
}

/// A set of disjoint polygons (a multipolygon): ``contains(x, y)`` is shapely's ``contains_xy`` off the boundary.
pub struct Region {
    polys: Vec<RPoly>,
    tree: Option<RTree<BoxItem>>,
    pub bounds: [f64; 4],
}

impl Region {
    pub fn empty() -> Region {
        Region { polys: vec![], tree: None, bounds: [f64::INFINITY, f64::INFINITY, f64::NEG_INFINITY, f64::NEG_INFINITY] }
    }

    pub fn from_polycs(ps: &[PolyC]) -> Region {
        let polys: Vec<RPoly> = ps.iter().filter(|p| p.ext.len() >= 4).map(RPoly::new).collect();
        let mut bounds = [f64::INFINITY, f64::INFINITY, f64::NEG_INFINITY, f64::NEG_INFINITY];
        for p in &polys {
            bounds = [bounds[0].min(p.bb[0]), bounds[1].min(p.bb[1]), bounds[2].max(p.bb[2]), bounds[3].max(p.bb[3])];
        }
        let tree = if polys.len() > 6 {
            Some(RTree::bulk_load(polys.iter().enumerate().map(|(i, p)| BoxItem::new(rect_of(p.bb), i)).collect()))
        } else {
            None
        };
        Region { polys, tree, bounds }
    }

    pub fn from_geom(g: &G) -> Region {
        Region::from_polycs(&geom::polys(g))
    }

    pub fn is_empty(&self) -> bool {
        self.polys.is_empty()
    }

    pub fn contains(&self, x: f64, y: f64) -> bool {
        if self.polys.is_empty() || x < self.bounds[0] || x > self.bounds[2] || y < self.bounds[1] || y > self.bounds[3] {
            return false;
        }
        match &self.tree {
            None => self.polys.iter().any(|p| p.contains(x, y)),
            Some(t) => t.locate_in_envelope_intersecting(&AABB::from_point([x, y])).any(|it| self.polys[it.data].contains(x, y)),
        }
    }

    /// Whether the region may touch the box: false only when it surely does not (for skipping empty tiles).
    pub fn may_touch(&self, b: [f64; 4]) -> bool {
        if self.polys.is_empty() || self.bounds[0] > b[2] || self.bounds[2] < b[0] || self.bounds[1] > b[3] || self.bounds[3] < b[1] {
            return false;
        }
        match &self.tree {
            None => self.polys.iter().any(|p| p.touches_box(b)),
            Some(t) => t.locate_in_envelope_intersecting(&AABB::from_corners([b[0], b[1]], [b[2], b[3]])).any(|it| self.polys[it.data].touches_box(b)),
        }
    }
}

#[derive(Clone)]
struct Seg {
    a: Pt,
    b: Pt,
}

impl RTreeObject for Seg {
    type Envelope = AABB<[f64; 2]>;
    fn envelope(&self) -> Self::Envelope {
        AABB::from_corners(self.a, self.b)
    }
}

impl PointDistance for Seg {
    fn distance_2(&self, p: &[f64; 2]) -> f64 {
        let (dx, dy) = (self.b[0] - self.a[0], self.b[1] - self.a[1]);
        let l2 = dx * dx + dy * dy;
        let t = if l2 > 0.0 { (((p[0] - self.a[0]) * dx + (p[1] - self.a[1]) * dy) / l2).clamp(0.0, 1.0) } else { 0.0 };
        let (qx, qy) = (self.a[0] + t * dx, self.a[1] + t * dy);
        (p[0] - qx) * (p[0] - qx) + (p[1] - qy) * (p[1] - qy)
    }
}

/// Distance from a point to a set of lines and polygons (0 inside a polygon), like ``shapely.distance(point, geom)``;
/// infinite for an empty set.
pub struct DistIdx {
    tree: RTree<Seg>,
    inside: Region,
}

impl DistIdx {
    pub fn empty() -> DistIdx {
        DistIdx { tree: RTree::new(), inside: Region::empty() }
    }

    pub fn new(lines: &[Vec<Pt>], polys: &[PolyC]) -> DistIdx {
        let mut segs = vec![];
        for l in lines {
            for w in l.windows(2) {
                segs.push(Seg { a: w[0], b: w[1] });
            }
        }
        for p in polys {
            for ring in std::iter::once(&p.ext).chain(p.holes.iter()) {
                for w in ring.windows(2) {
                    segs.push(Seg { a: w[0], b: w[1] });
                }
            }
        }
        DistIdx { tree: RTree::bulk_load(segs), inside: Region::from_polycs(polys) }
    }

    pub fn is_empty(&self) -> bool {
        self.tree.size() == 0
    }

    pub fn dist(&self, x: f64, y: f64) -> f64 {
        if self.tree.size() == 0 {
            return f64::INFINITY;
        }
        if !self.inside.is_empty() && self.inside.contains(x, y) {
            return 0.0;
        }
        match self.tree.nearest_neighbor(&[x, y]) {
            Some(s) => s.distance_2(&[x, y]).sqrt(),
            None => f64::INFINITY,
        }
    }
}

/// Bounding-box lookup of numbered items.
pub struct BoxIndex {
    tree: RTree<BoxItem>,
}

impl BoxIndex {
    pub fn new(boxes: &[[f64; 4]]) -> BoxIndex {
        BoxIndex { tree: RTree::bulk_load(boxes.iter().enumerate().map(|(i, b)| BoxItem::new(rect_of(*b), i)).collect()) }
    }

    /// Items whose box meets ``b``, in index order.
    pub fn query(&self, b: [f64; 4]) -> Vec<usize> {
        let mut v: Vec<usize> =
            self.tree.locate_in_envelope_intersecting(&AABB::from_corners([b[0], b[1]], [b[2], b[3]])).map(|it| it.data).collect();
        v.sort_unstable();
        v
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ring(pts: &[[f64; 2]]) -> Vec<Pt> {
        let mut v = pts.to_vec();
        v.push(v[0]);
        v
    }

    #[test]
    fn point_in_polygon_with_hole() {
        let p = PolyC {
            ext: ring(&[[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [0.0, 10.0]]),
            holes: vec![ring(&[[4.0, 4.0], [6.0, 4.0], [6.0, 6.0], [4.0, 6.0]])],
        };
        let r = Region::from_polycs(&[p]);
        assert!(r.contains(1.0, 1.0));
        assert!(r.contains(9.0, 5.0));
        assert!(!r.contains(5.0, 5.0)); // in the hole
        assert!(!r.contains(11.0, 5.0));
        assert!(!r.contains(-1.0, 5.0));
    }

    #[test]
    fn indexed_polygon_agrees_with_plain_test() {
        // a comb with many edges: the banded and the plain crossing tests must agree everywhere
        let mut pts = vec![[0.0, 0.0]];
        for i in 0..40 {
            let x = i as f64 * 2.0;
            pts.push([x, 0.0]);
            pts.push([x, 10.0 + (i % 3) as f64]);
            pts.push([x + 1.0, 10.0 + (i % 3) as f64]);
            pts.push([x + 1.0, 0.0]);
        }
        pts.push([80.0, -3.0]);
        pts.push([0.0, -3.0]);
        let p = PolyC { ext: ring(&pts), holes: vec![] };
        let banded = RPoly::new(&p);
        assert!(banded.tree.is_some());
        let mut plain = RPoly::new(&p);
        plain.tree = None;
        let mut k = 0;
        for i in 0..400 {
            for j in 0..200 {
                let (x, y) = (i as f64 * 0.2 + 0.013, j as f64 * 0.09 - 3.5 + 0.007);
                assert_eq!(banded.contains(x, y), plain.contains(x, y), "{x} {y}");
                k += banded.contains(x, y) as usize;
            }
        }
        assert!(k > 1000);
    }

    #[test]
    fn distance_is_zero_inside_and_exact_outside() {
        let sq = PolyC { ext: ring(&[[0.0, 0.0], [4.0, 0.0], [4.0, 4.0], [0.0, 4.0]]), holes: vec![] };
        let d = DistIdx::new(&[], &[sq]);
        assert_eq!(d.dist(2.0, 2.0), 0.0);
        assert!((d.dist(7.0, 2.0) - 3.0).abs() < 1e-12);
        assert!((d.dist(7.0, 8.0) - 5.0).abs() < 1e-12);
        assert!(DistIdx::empty().dist(0.0, 0.0).is_infinite());
    }
}

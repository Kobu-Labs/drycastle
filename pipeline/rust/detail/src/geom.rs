//! A thin layer over GEOS (the same engine shapely runs on): plain-coordinate polygons in and out, boolean operations that
//! never panic (a GEOS error retries on repaired inputs, then gives an empty result and is counted).
//!
//! GEOS geometries are only ever created, used and dropped on one thread; whatever crosses threads is plain
//! coordinates (:class:`PolyC`).

use geos::{CapStyle, CoordSeq, CoordType, Geom, Geometry, GeometryTypes, JoinStyle};
use std::sync::atomic::{AtomicUsize, Ordering};

pub type G = Geometry;
pub type Pt = [f64; 2];

pub static GEOS_ERRORS: AtomicUsize = AtomicUsize::new(0);
/// The first few distinct GEOS error messages (diagnostics for the statistics).
pub static GEOS_MESSAGES: std::sync::Mutex<Vec<String>> = std::sync::Mutex::new(Vec::new());

fn note_error(op: &str, e: &geos::Error) {
    GEOS_ERRORS.fetch_add(1, Ordering::Relaxed);
    if let Ok(mut m) = GEOS_MESSAGES.lock() {
        let msg = format!("{op}: {e}");
        if m.len() < 8 && !m.contains(&msg) {
            m.push(msg);
        }
    }
}

/// A polygon as plain coordinates: closed rings (first point repeated at the end).
#[derive(Clone, Debug, Default)]
pub struct PolyC {
    pub ext: Vec<Pt>,
    pub holes: Vec<Vec<Pt>>,
}

impl PolyC {
    pub fn bounds(&self) -> [f64; 4] {
        let mut b = [f64::INFINITY, f64::INFINITY, f64::NEG_INFINITY, f64::NEG_INFINITY];
        for p in &self.ext {
            b[0] = b[0].min(p[0]);
            b[1] = b[1].min(p[1]);
            b[2] = b[2].max(p[0]);
            b[3] = b[3].max(p[1]);
        }
        b
    }
}

fn err_count<T>(r: geos::GResult<T>) -> Option<T> {
    match r {
        Ok(v) => Some(v),
        Err(e) => {
            note_error("op", &e);
            None
        }
    }
}

pub fn empty() -> G {
    Geometry::create_empty_polygon().expect("empty polygon")
}

fn closed(pts: &[Pt]) -> Vec<Pt> {
    let mut v: Vec<Pt> = pts.to_vec();
    if v.len() >= 2 && v.first() != v.last() {
        v.push(v[0]);
    }
    v
}

fn ring(pts: &[Pt]) -> Option<G> {
    let v = closed(pts);
    if v.len() < 4 {
        return None;
    }
    let seq = CoordSeq::new_from_vec(&v).ok()?;
    Geometry::create_linear_ring(seq).ok()
}

/// A polygon from rings (an empty polygon when the outline is degenerate). Not repaired: see :func:`valid`.
pub fn polygon(ext: &[Pt], holes: &[Vec<Pt>]) -> G {
    let Some(e) = ring(ext) else { return empty() };
    let hs: Vec<G> = holes.iter().filter_map(|h| ring(h)).collect();
    Geometry::create_polygon(e, hs).unwrap_or_else(|_| empty())
}

pub fn from_polyc(p: &PolyC) -> G {
    polygon(&p.ext, &p.holes)
}

/// A polygon from the points of a ring, made valid like ``Polygon(pts).buffer(0)``.
pub fn polygon_fixed(ext: &[Pt]) -> G {
    let p = polygon(ext, &[]);
    if is_empty(&p) {
        return p;
    }
    buffer0(&p)
}

pub fn line(pts: &[Pt]) -> G {
    if pts.len() < 2 {
        return Geometry::create_empty_line_string().expect("empty line");
    }
    match CoordSeq::new_from_vec(pts).and_then(Geometry::create_line_string) {
        Ok(g) => g,
        Err(_) => Geometry::create_empty_line_string().expect("empty line"),
    }
}

pub fn point(x: f64, y: f64) -> G {
    CoordSeq::new_from_vec(&[[x, y]]).and_then(Geometry::create_point).expect("point")
}

pub fn rect(x0: f64, y0: f64, x1: f64, y1: f64) -> G {
    polygon(&[[x0, y0], [x1, y0], [x1, y1], [x0, y1]], &[])
}

pub fn is_empty<T: Geom>(g: &T) -> bool {
    g.is_empty().unwrap_or(true)
}

pub fn area<T: Geom>(g: &T) -> f64 {
    g.area().unwrap_or(0.0)
}

pub fn length<T: Geom>(g: &T) -> f64 {
    g.length().unwrap_or(0.0)
}

pub fn bounds<T: Geom>(g: &T) -> [f64; 4] {
    [
        g.get_x_min().unwrap_or(0.0),
        g.get_y_min().unwrap_or(0.0),
        g.get_x_max().unwrap_or(0.0),
        g.get_y_max().unwrap_or(0.0),
    ]
}

pub fn dup<T: Geom>(g: &T) -> G {
    Geom::clone(g).unwrap_or_else(|_| empty())
}

fn repaired(g: &G) -> G {
    g.make_valid().unwrap_or_else(|_| empty())
}

fn op2(a: &G, b: &G, f: impl Fn(&G, &G) -> geos::GResult<G>) -> G {
    match f(a, b) {
        Ok(r) => r,
        Err(e) => {
            note_error("overlay", &e);
            f(&repaired(a), &repaired(b)).unwrap_or_else(|_| empty())
        }
    }
}

pub fn inter(a: &G, b: &G) -> G {
    if is_empty(a) || is_empty(b) {
        return empty();
    }
    op2(a, b, |x, y| x.intersection(y))
}

pub fn diff(a: &G, b: &G) -> G {
    if is_empty(a) {
        return empty();
    }
    if is_empty(b) {
        return dup(a);
    }
    op2(a, b, |x, y| x.difference(y))
}

pub fn union2(a: &G, b: &G) -> G {
    if is_empty(a) {
        return dup(b);
    }
    if is_empty(b) {
        return dup(a);
    }
    op2(a, b, |x, y| x.union(y))
}

/// Union of many geometries (empty ones dropped); an empty polygon for none.
pub fn union_all(parts: Vec<G>) -> G {
    let parts: Vec<G> = parts.into_iter().filter(|p| !is_empty(p)).collect();
    match parts.len() {
        0 => empty(),
        1 => parts.into_iter().next().unwrap(),
        _ => {
            let coll = match Geometry::create_geometry_collection(parts) {
                Ok(c) => c,
                Err(_) => {
                    GEOS_ERRORS.fetch_add(1, Ordering::Relaxed);
                    return empty();
                }
            };
            match coll.unary_union() {
                Ok(u) => u,
                Err(_) => {
                    GEOS_ERRORS.fetch_add(1, Ordering::Relaxed);
                    // fall back to a pairwise union
                    let mut acc = empty();
                    if let Ok(n) = coll.get_num_geometries() {
                        for i in 0..n {
                            if let Ok(c) = coll.get_geometry_n(i) {
                                acc = union2(&acc, &dup(&c));
                            }
                        }
                    }
                    acc
                }
            }
        }
    }
}

pub fn buffer0(g: &G) -> G {
    err_count(g.buffer(0.0, 8)).unwrap_or_else(empty)
}

/// ``buffer(d, quad_segs)`` with round joins.
pub fn buffer(g: &G, d: f64, quad: i32) -> G {
    err_count(g.buffer(d, quad)).unwrap_or_else(empty)
}

/// Square corners (mitre joins), flat ends: few vertices.
pub fn buffer_mitre(g: &G, d: f64) -> G {
    err_count(g.buffer_with_style(d, 8, CapStyle::Round, JoinStyle::Mitre, 5.0)).unwrap_or_else(empty)
}

pub fn buffer_flat_mitre(g: &G, d: f64) -> G {
    err_count(g.buffer_with_style(d, 8, CapStyle::Flat, JoinStyle::Mitre, 5.0)).unwrap_or_else(empty)
}

pub fn buffer_flat(g: &G, d: f64) -> G {
    err_count(g.buffer_with_style(d, 8, CapStyle::Flat, JoinStyle::Round, 5.0)).unwrap_or_else(empty)
}

pub fn simplify(g: &G, tol: f64) -> G {
    err_count(g.topology_preserve_simplify(tol)).unwrap_or_else(|| dup(g))
}

/// ``g`` cut to a box. GEOS has a fast ``clip_by_rect``, but it returns wrong rings when a hole touches the outline at a point (a
/// lawn with a dirt blob on its edge), so the exact overlay is used.
pub fn clip_rect(g: &G, x0: f64, y0: f64, x1: f64, y1: f64) -> G {
    if is_empty(g) {
        return empty();
    }
    inter(g, &rect(x0, y0, x1, y1))
}

pub fn intersects(a: &G, b: &G) -> bool {
    a.intersects(b).unwrap_or(false)
}

fn seq_pts<T: Geom>(g: &T) -> Vec<Pt> {
    let Ok(seq) = g.get_coord_seq() else { return vec![] };
    match seq.as_buffer(Some(CoordType::XY)) {
        Ok(b) => b.chunks_exact(2).map(|c| [c[0], c[1]]).collect(),
        Err(_) => vec![],
    }
}

/// The points of a line string or ring.
pub fn coords<T: Geom>(g: &T) -> Vec<Pt> {
    seq_pts(g)
}

fn poly_coords<T: Geom>(g: &T) -> PolyC {
    let mut pc = PolyC::default();
    if let Ok(e) = g.get_exterior_ring() {
        pc.ext = seq_pts(&e);
    }
    let n = g.get_num_interior_rings().unwrap_or(0);
    for i in 0..n {
        if let Ok(h) = g.get_interior_ring_n(i) {
            pc.holes.push(seq_pts(&h));
        }
    }
    pc
}

fn collect_polys<T: Geom>(g: &T, out: &mut Vec<PolyC>) {
    match g.geometry_type() {
        Ok(GeometryTypes::Polygon) => {
            if !is_empty(g) {
                out.push(poly_coords(g));
            }
        }
        Ok(GeometryTypes::MultiPolygon) | Ok(GeometryTypes::GeometryCollection) => {
            let n = g.get_num_geometries().unwrap_or(0);
            for i in 0..n {
                if let Ok(c) = g.get_geometry_n(i) {
                    collect_polys(&c, out);
                }
            }
        }
        _ => {}
    }
}

/// Every polygon of a (multi)polygon or collection, as plain coordinates.
pub fn polys<T: Geom>(g: &T) -> Vec<PolyC> {
    let mut out = vec![];
    collect_polys(g, &mut out);
    out
}

fn collect_polys_g<T: Geom>(g: &T, out: &mut Vec<G>) {
    match g.geometry_type() {
        Ok(GeometryTypes::Polygon) => {
            if !is_empty(g) {
                out.push(dup(g));
            }
        }
        Ok(GeometryTypes::MultiPolygon) | Ok(GeometryTypes::GeometryCollection) => {
            let n = g.get_num_geometries().unwrap_or(0);
            for i in 0..n {
                if let Ok(c) = g.get_geometry_n(i) {
                    collect_polys_g(&c, out);
                }
            }
        }
        _ => {}
    }
}

/// The polygons of a geometry as geometries.
pub fn polys_g<T: Geom>(g: &T) -> Vec<G> {
    let mut out = vec![];
    collect_polys_g(g, &mut out);
    out
}

fn collect_lines<T: Geom>(g: &T, out: &mut Vec<G>) {
    match g.geometry_type() {
        Ok(GeometryTypes::LineString) | Ok(GeometryTypes::LinearRing) => {
            if !is_empty(g) {
                out.push(dup(g));
            }
        }
        Ok(GeometryTypes::MultiLineString) | Ok(GeometryTypes::GeometryCollection) => {
            let n = g.get_num_geometries().unwrap_or(0);
            for i in 0..n {
                if let Ok(c) = g.get_geometry_n(i) {
                    collect_lines(&c, out);
                }
            }
        }
        _ => {}
    }
}

pub fn lines_g<T: Geom>(g: &T) -> Vec<G> {
    let mut out = vec![];
    collect_lines(g, &mut out);
    out
}

pub fn exterior_line(p: &G) -> G {
    match p.get_exterior_ring() {
        Ok(r) => line(&seq_pts(&r)),
        Err(_) => line(&[]),
    }
}

pub fn centroid(g: &G) -> Pt {
    match g.get_centroid() {
        Ok(p) => [p.get_x().unwrap_or(0.0), p.get_y().unwrap_or(0.0)],
        Err(_) => [0.0, 0.0],
    }
}

pub fn min_rotated_rect(g: &G) -> Vec<Pt> {
    match g.minimum_rotated_rectangle() {
        Ok(r) if r.geometry_type().ok() == Some(GeometryTypes::Polygon) => polys(&r).into_iter().next().map(|p| p.ext).unwrap_or_default(),
        _ => vec![],
    }
}

pub fn distance(a: &G, b: &G) -> f64 {
    a.distance(b).unwrap_or(f64::INFINITY)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn booleans_and_extraction() {
        let a = rect(0.0, 0.0, 10.0, 10.0);
        let b = rect(5.0, 5.0, 15.0, 15.0);
        assert!((area(&inter(&a, &b)) - 25.0).abs() < 1e-9);
        assert!((area(&diff(&a, &b)) - 75.0).abs() < 1e-9);
        assert!((area(&union2(&a, &b)) - 175.0).abs() < 1e-9);
        let ps = polys(&diff(&a, &rect(2.0, 2.0, 4.0, 4.0)));
        assert_eq!(ps.len(), 1);
        assert_eq!(ps[0].holes.len(), 1);
        assert_eq!(ps[0].ext.len(), 5);
    }
}

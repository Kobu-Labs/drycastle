//! What a recipe needs about one unit of ground (``ParcelContext``): the unit's polygon and its neighbours (buildings, fences,
//! trees, roads, placed objects, regions, verges, water), found through spatial indexes built once for the whole map.

use crate::cfg::Cfg;
use crate::geom::{self, PolyC, Pt, G};
use crate::index::BoxIndex;
use crate::terrain::Terrain;
use crate::util;
use rayon::prelude::*;
use serde_json::Value;
use std::cell::OnceCell;
use std::collections::HashMap;

pub const NEAR_M: f64 = 8.0;

pub struct RoadEdge {
    pub id: String,
    pub highway: String,
    pub width: f64,
    pub osm_id: String,
    pub line: Vec<Pt>,
    pub poly: PolyC,
}

pub struct Unit {
    pub id: String,
    pub kind: String,
    pub recipe: String,
    pub whole_dike: bool,
    pub colour: Option<[u8; 3]>,
    pub polys: Vec<PolyC>,
}

pub struct Shared {
    pub cfg: Value,
    pub cfgs: HashMap<String, Value>,
    pub canopy_m: f64,
    pub chunk_m: f64,
    pub terrain: Terrain,
    pub kinds: Vec<String>,
    pub tintable: Vec<String>,
    pub textures: HashMap<String, (f64, f64)>,
    pub tex_list: Vec<String>,
    pub image: (usize, usize, Vec<u8>),
    pub units: Vec<Unit>,
    pub buildings: Vec<PolyC>,
    pub b_idx: BoxIndex,
    pub fences: Vec<[Pt; 2]>,
    pub f_idx: BoxIndex,
    pub trees: Vec<[f64; 3]>,
    pub t_idx: BoxIndex,
    pub roads: Vec<RoadEdge>,
    pub r_idx: BoxIndex,
    pub rl_idx: BoxIndex,
    pub objects: Vec<PolyC>,
    pub o_idx: BoxIndex,
    pub regions: Vec<PolyC>,
    pub reg_idx: BoxIndex,
    pub water: Vec<PolyC>,
    pub w_idx: BoxIndex,
    pub verge: [Vec<PolyC>; 2],
    pub v_idx: [BoxIndex; 2],
    pub coverage: PolyC,
    pub osm_lines: HashMap<String, Vec<usize>>,
}

fn pt(v: &Value) -> Pt {
    [v[0].as_f64().unwrap_or(0.0), v[1].as_f64().unwrap_or(0.0)]
}

fn ring(v: &Value) -> Vec<Pt> {
    v.as_array().map(|a| a.iter().map(pt).collect()).unwrap_or_default()
}

/// ``{"exterior": ring, "holes": [ring...]}``.
fn polyc_ext_holes(v: &Value) -> PolyC {
    PolyC { ext: ring(&v["exterior"]), holes: v["holes"].as_array().map(|a| a.iter().map(ring).collect()).unwrap_or_default() }
}

/// A GeoJSON Polygon or MultiPolygon.
fn geojson_polys(v: &Value) -> Vec<PolyC> {
    let one = |c: &Value| {
        let rings: Vec<Vec<Pt>> = c.as_array().map(|a| a.iter().map(ring).collect()).unwrap_or_default();
        let mut it = rings.into_iter();
        PolyC { ext: it.next().unwrap_or_default(), holes: it.collect() }
    };
    match v["type"].as_str() {
        Some("MultiPolygon") => v["coordinates"].as_array().map(|a| a.iter().map(one).collect()).unwrap_or_default(),
        _ => vec![one(&v["coordinates"])],
    }
}

fn boxes<T>(items: &[T], f: impl Fn(&T) -> [f64; 4]) -> BoxIndex {
    BoxIndex::new(&items.iter().map(f).collect::<Vec<_>>())
}

fn polyc_box(p: &PolyC) -> [f64; 4] {
    p.bounds()
}

pub fn hex_rgb(h: &str) -> [u8; 3] {
    let h = h.trim_start_matches('#');
    let v = |i: usize| u8::from_str_radix(h.get(i..i + 2).unwrap_or("80"), 16).unwrap_or(128);
    [v(0), v(2), v(4)]
}

impl Shared {
    pub fn parse(input: &Value, heights: Vec<f64>, valid: Vec<bool>, image: (usize, usize, Vec<u8>)) -> Result<Shared, String> {
        let cfg = input["cfg"].clone();
        let t = &input["terrain"];
        let (rows, cols) = (t["rows"].as_u64().ok_or("terrain rows")? as usize, t["cols"].as_u64().ok_or("terrain cols")? as usize);
        let terrain = Terrain::new(
            t["chunk_m"].as_f64().ok_or("chunk_m")?, t["n"].as_i64().ok_or("n")?, t["i0"].as_i64().ok_or("i0")?, t["j0"].as_i64().ok_or("j0")?,
            rows, cols, heights, valid,
        );
        let units: Vec<Unit> = input["units"]
            .as_array()
            .ok_or("units")?
            .iter()
            .map(|u| Unit {
                id: u["id"].as_str().unwrap_or("").to_string(),
                kind: u["kind"].as_str().unwrap_or("").to_string(),
                recipe: u["recipe"].as_str().unwrap_or("").to_string(),
                whole_dike: u["whole_dike"].as_bool().unwrap_or(false),
                colour: u["colour"].as_array().map(|c| [c[0].as_u64().unwrap_or(0) as u8, c[1].as_u64().unwrap_or(0) as u8, c[2].as_u64().unwrap_or(0) as u8]),
                polys: geojson_polys(&u["polygon"]),
            })
            .collect();
        let buildings: Vec<PolyC> = input["buildings"].as_array().ok_or("buildings")?.iter().map(polyc_ext_holes).collect();
        let fences: Vec<[Pt; 2]> = input["fences"].as_array().ok_or("fences")?.iter().map(|f| [[f[0].as_f64().unwrap_or(0.0), f[1].as_f64().unwrap_or(0.0)], [f[2].as_f64().unwrap_or(0.0), f[3].as_f64().unwrap_or(0.0)]]).collect();
        let trees: Vec<[f64; 3]> = input["trees"].as_array().ok_or("trees")?.iter().map(|t| [t[0].as_f64().unwrap_or(0.0), t[1].as_f64().unwrap_or(0.0), t[2].as_f64().unwrap_or(1.0)]).collect();
        let roads_raw: Vec<(String, String, f64, String, Vec<Pt>)> = input["roads"]
            .as_array()
            .ok_or("roads")?
            .iter()
            .map(|e| (
                e["id"].as_str().unwrap_or("").to_string(), e["highway"].as_str().unwrap_or("").to_string(), e["width"].as_f64().unwrap_or(5.0),
                e["osm_id"].as_str().unwrap_or("").to_string(), ring(&e["polyline"]),
            ))
            .collect();
        // road ribbons (the edge buffered by half its width), made in parallel
        let roads: Vec<RoadEdge> = roads_raw
            .into_par_iter()
            .map(|(id, highway, width, osm_id, line)| {
                let g = geom::buffer(&geom::line(&line), width / 2.0, 16);
                let poly = geom::polys(&g).into_iter().max_by(|a, b| a.ext.len().cmp(&b.ext.len())).unwrap_or_default();
                RoadEdge { id, highway, width, osm_id, line, poly }
            })
            .collect();
        let objects: Vec<PolyC> = input["objects"]
            .as_array()
            .ok_or("objects")?
            .iter()
            .map(|o| {
                let g = geom::buffer(&geom::point(o[0].as_f64().unwrap_or(0.0), o[1].as_f64().unwrap_or(0.0)), o[2].as_f64().unwrap_or(0.5), 8);
                geom::polys(&g).into_iter().next().unwrap_or_default()
            })
            .collect();
        let regions: Vec<PolyC> = input["regions"].as_array().ok_or("regions")?.iter().map(polyc_ext_holes).collect();
        let water_kinds: Vec<&str> = input["water_kinds"].as_array().map(|a| a.iter().filter_map(|v| v.as_str()).collect()).unwrap_or_default();
        let water: Vec<PolyC> = units.iter().filter(|u| water_kinds.contains(&u.kind.as_str())).flat_map(|u| u.polys.clone()).collect();
        let mut osm_lines: HashMap<String, Vec<usize>> = HashMap::new();
        for (i, r) in roads.iter().enumerate() {
            osm_lines.entry(r.osm_id.clone()).or_default().push(i);
        }
        let textures: HashMap<String, (f64, f64)> =
            input["textures"].as_object().map(|o| o.iter().map(|(k, v)| (k.clone(), (v[0].as_f64().unwrap_or(1.0), v[1].as_f64().unwrap_or(1.0)))).collect()).unwrap_or_default();
        let mut cfgs = HashMap::new();
        if let Some(o) = input["cfgs"].as_object() {
            for (k, v) in o {
                cfgs.insert(k.clone(), v.clone());
            }
        }
        let verge = crate::verge::make_verge(&roads, Cfg(&cfg).at("verge"));
        let coverage = PolyC { ext: ring(&input["coverage"]), holes: vec![] };
        let sh = Shared {
            canopy_m: input["canopy_m"].as_f64().unwrap_or(5.0),
            chunk_m: input["chunk_m"].as_f64().unwrap_or(250.0),
            kinds: input["kinds"].as_array().map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default(),
            tintable: input["tintable"].as_array().map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default(),
            tex_list: input["tex_list"].as_array().map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default(),
            b_idx: boxes(&buildings, polyc_box),
            f_idx: boxes(&fences, |f| [f[0][0].min(f[1][0]), f[0][1].min(f[1][1]), f[0][0].max(f[1][0]), f[0][1].max(f[1][1])]),
            t_idx: boxes(&trees, |t| [t[0], t[1], t[0], t[1]]),
            r_idx: boxes(&roads, |r| polyc_box(&r.poly)),
            rl_idx: boxes(&roads, |r| {
                let mut b = [f64::INFINITY, f64::INFINITY, f64::NEG_INFINITY, f64::NEG_INFINITY];
                for p in &r.line {
                    b = [b[0].min(p[0]), b[1].min(p[1]), b[2].max(p[0]), b[3].max(p[1])];
                }
                b
            }),
            o_idx: boxes(&objects, polyc_box),
            reg_idx: boxes(&regions, polyc_box),
            w_idx: boxes(&water, polyc_box),
            v_idx: [boxes(&verge[0], polyc_box), boxes(&verge[1], polyc_box)],
            cfg,
            cfgs,
            terrain,
            textures,
            image,
            units,
            buildings,
            fences,
            trees,
            roads,
            objects,
            regions,
            water,
            verge,
            coverage,
            osm_lines,
        };
        Ok(sh)
    }

    /// The detail rules of a unit: those of its kind and recipe when the type has settings of its own, else the base section.
    pub fn cfg_for(&self, kind: &str, recipe: &str) -> Cfg<'_> {
        match self.cfgs.get(&format!("{kind}|{recipe}")) {
            Some(v) => Cfg(v),
            None => Cfg(&self.cfg),
        }
    }
}

/// How a context finds its neighbours.
pub enum Near {
    /// The context's own outline grown by :data:`NEAR_M`: exact tests like the Python stage.
    Exact,
    /// Only bounding boxes: for a tile of a big area, whose neighbours are looked up by box.
    Box,
}

pub struct Ctx<'a> {
    pub sh: &'a Shared,
    pub id: String,
    pub kind: String,
    pub polygon: G,
    pub buildings: Vec<G>,
    pub fences: Vec<G>,
    pub trees: Vec<[f64; 3]>,
    pub road: Option<G>,
    pub blocked: Option<G>,
    pub verge_g: G,
    pub verge_d: G,
    pub water: Option<G>,
    pub road_lines: Vec<(G, f64)>,
    pub colour: Option<[u8; 3]>,
    free: [OnceCell<G>; 2],
}

impl<'a> Ctx<'a> {
    /// A context over ``polygon`` (the unit, or a tile of it): ``bbox`` is the box the neighbour search covers
    /// (the polygon's own box for a unit, the tile's for a tile).
    pub fn build(sh: &'a Shared, id: &str, kind: &str, polygon: G, near: Near, colour: Option<[u8; 3]>) -> Ctx<'a> {
        let cfg = Cfg(&sh.cfg);
        let pb = geom::bounds(&polygon);
        let near_g: Option<G> = match near {
            Near::Exact => Some(geom::buffer(&polygon, NEAR_M, 16)),
            Near::Box => None,
        };
        let nb = util::grow(pb, NEAR_M);
        let hit = |g: &G| near_g.as_ref().is_none_or(|n| geom::intersects(g, n));

        let roads: Vec<G> = sh.r_idx.query(nb).into_iter().map(|i| geom::from_polyc(&sh.roads[i].poly)).filter(|g| hit(g)).collect();
        let road = if roads.is_empty() { None } else { Some(geom::union_all(roads)) };

        let obj_ids = sh.o_idx.query(pb);
        let mut blocked: Vec<G> = obj_ids.iter().map(|&i| geom::from_polyc(&sh.objects[i])).filter(|g| geom::intersects(g, &polygon)).collect();
        let reg_ids = sh.reg_idx.query(util::grow(pb, 1.0));
        if !reg_ids.is_empty() {
            let regs = geom::union_all(reg_ids.iter().map(|&i| geom::from_polyc(&sh.regions[i])).collect());
            if geom::intersects(&regs, &polygon) {
                blocked.push(geom::inter(&regs, &geom::buffer(&polygon, 1.0, 16)));
            }
        }
        let blocked = if blocked.is_empty() { None } else { Some(geom::union_all(blocked)) };

        let mut verge: [G; 2] = [geom::empty(), geom::empty()];
        for k in 0..2 {
            let parts: Vec<G> = sh.v_idx[k].query(pb).into_iter().map(|i| geom::from_polyc(&sh.verge[k][i])).filter(|g| geom::intersects(g, &polygon)).collect();
            if !parts.is_empty() {
                verge[k] = geom::inter(&geom::union_all(parts), &polygon);
            }
        }
        let [verge_g, verge_d] = verge;

        let buildings: Vec<G> = sh.b_idx.query(nb).into_iter().map(|i| geom::from_polyc(&sh.buildings[i])).filter(|g| hit(g)).collect();
        let fences: Vec<G> = sh
            .f_idx
            .query(nb)
            .into_iter()
            .map(|i| geom::line(&sh.fences[i]))
            .filter(|g| hit(g))
            .collect();
        let trees: Vec<[f64; 3]> = sh
            .t_idx
            .query(nb)
            .into_iter()
            .map(|i| sh.trees[i])
            .filter(|t| near_g.as_ref().is_none_or(|n| geom::intersects(&geom::point(t[0], t[1]), n)))
            .collect();
        let road_lines: Vec<(G, f64)> = sh
            .rl_idx
            .query(nb)
            .into_iter()
            .map(|i| (geom::line(&sh.roads[i].line), sh.roads[i].width))
            .filter(|(g, _)| hit(g))
            .collect();
        let water_m = cfg.p("tint.water_m").f();
        let water = {
            let ids = sh.w_idx.query(util::grow(pb, water_m));
            let area = match near {
                Near::Exact => Some(geom::buffer(&polygon, water_m, 16)),
                Near::Box => None,
            };
            let ps: Vec<G> = ids.iter().map(|&i| geom::from_polyc(&sh.water[i])).filter(|g| area.as_ref().is_none_or(|a| geom::intersects(g, a))).collect();
            if ps.is_empty() { None } else { Some(geom::union_all(ps)) }
        };
        Ctx {
            sh,
            id: id.to_string(),
            kind: kind.to_string(),
            polygon,
            buildings,
            fences,
            trees,
            road,
            blocked,
            verge_g,
            verge_d,
            water,
            road_lines,
            colour,
            free: [OnceCell::new(), OnceCell::new()],
        }
    }

    /// The polygon without buildings (and a wall margin), fences, trunks, placed objects, regions and the road.
    /// ``trunks = false`` (forest, field) leaves the trunk footprints in.
    pub fn free_area(&self, cfg: Cfg, trunks: bool) -> &G {
        self.free[trunks as usize].get_or_init(|| self.compute_free(cfg, trunks, &self.polygon, true))
    }

    /// The same ground rules on another outline (``use_road = false``: as if the road were not there).
    pub fn free_of(&self, cfg: Cfg, trunks: bool, polygon: &G, use_road: bool) -> G {
        self.compute_free(cfg, trunks, polygon, use_road)
    }

    fn compute_free(&self, cfg: Cfg, trunks: bool, polygon: &G, use_road: bool) -> G {
        // square corners and flat ends: few vertices, so the patches cut against this have few triangles
        let mut blocked: Vec<G> = vec![];
        let wall = cfg.p("wall_clearance_m").f();
        let fence = cfg.p("fence_clearance_m").f();
        for b in &self.buildings {
            blocked.push(geom::buffer_mitre(b, wall));
        }
        for f in &self.fences {
            blocked.push(geom::buffer_flat_mitre(f, fence));
        }
        if trunks {
            let r = cfg.p("trunk_radius_m").f();
            for t in &self.trees {
                blocked.push(geom::buffer(&geom::point(t[0], t[1]), r, 2));
            }
        }
        if let Some(road) = self.road.as_ref().filter(|_| use_road) {
            blocked.push(geom::simplify(&geom::buffer_mitre(road, cfg.p("road_clearance_m").f()), 0.1));
        }
        if let Some(b) = &self.blocked {
            if !geom::is_empty(b) {
                blocked.push(geom::dup(b));
            }
        }
        if blocked.is_empty() {
            geom::dup(polygon)
        } else {
            geom::diff(polygon, &geom::union_all(blocked))
        }
    }
}

/// Colours of a flat layer and how much they follow the parcel's terrain colour: each colour is the fixed one at mix 0 and
/// the terrain colour times its shade at 1.
pub fn palette(ctx: &Ctx, fixed: &[&str], shades: &[f64], mix: Cfg, default_mix: f64) -> (Vec<[u8; 3]>, f64) {
    let w = if mix.0.is_object() { mix.at(&ctx.kind).fo(default_mix) } else { mix.fo(default_mix) };
    let mut out = vec![];
    for (i, hx) in fixed.iter().enumerate() {
        let mut c = hex_rgb(hx);
        if let (Some(tc), true) = (ctx.colour, w > 0.0) {
            let s = shades[i % shades.len()];
            let shaded = [0, 1, 2].map(|k| (tc[k] as f64 * s).round().clamp(0.0, 255.0));
            c = [0, 1, 2].map(|k| (c[k] as f64 * (1.0 - w) + shaded[k] * w).round().clamp(0.0, 255.0) as u8);
        }
        out.push(c);
    }
    (out, w)
}


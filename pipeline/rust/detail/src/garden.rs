//! The garden recipe (``building_plot`` and ``garden`` parcels): a gravel path from the gate, vegetable beds, dirt splashes along
//! the walls and in the open, leaf litter under trees, lawn and tone blobs; tufts, flowers, pebbles, rocks, leaves and sticks.

use crate::cfg::Cfg;
use crate::ctx::{palette, Ctx};
use crate::emit::{Emitter, Res, Tint, CELL_M};
use crate::geom::{self, Pt, G};
use crate::index::{DistIdx, Region};
use crate::noise;
use crate::patches::{assemble, Flat, Rect};
use crate::rng::{seed_of, Rng};
use geos::Geom;
use crate::util::{blob, lattice, point_rng, rad, Polyline};
use std::collections::BTreeMap;
use std::f64::consts::TAU;

/// Bridson's Poisson-disc sampling inside ``region``: points at least ``min_dist`` apart. Disconnected parts are reached by
/// restarting from random seeds.
pub fn poisson_disc(rng: &mut Rng, region: &Region, bounds: [f64; 4], area: f64, min_dist: f64) -> Vec<Pt> {
    if region.is_empty() || min_dist <= 0.0 {
        return vec![];
    }
    let [minx, miny, maxx, maxy] = bounds;
    let cell = min_dist / 2f64.sqrt();
    let (gw, gh) = (((maxx - minx) / cell) as usize + 3, ((maxy - miny) / cell) as usize + 3);
    let mut grid = vec![-1i32; gw * gh];
    let mut pts: Vec<Pt> = vec![];
    let key = |x: f64, y: f64| (((x - minx) / cell) as usize, ((y - miny) / cell) as usize);
    let fits = |grid: &Vec<i32>, pts: &Vec<Pt>, x: f64, y: f64| {
        let (i, j) = key(x, y);
        for dj in 0..5usize {
            for di in 0..5usize {
                let (a, b) = ((i + di) as i64 - 2, (j + dj) as i64 - 2);
                if a < 0 || b < 0 || a as usize >= gw || b as usize >= gh {
                    continue;
                }
                let q = grid[b as usize * gw + a as usize];
                if q >= 0 && (pts[q as usize][0] - x).hypot(pts[q as usize][1] - y) < min_dist {
                    return false;
                }
            }
        }
        true
    };
    let seeds = (60.0 + 20.0 * (maxx - minx) * (maxy - miny) / area.max(1e-6)) as usize;
    for _ in 0..seeds {
        let (x, y) = (minx + rng.f() * (maxx - minx), miny + rng.f() * (maxy - miny));
        if !(region.contains(x, y) && fits(&grid, &pts, x, y)) {
            continue;
        }
        let mut active: Vec<usize> = vec![];
        let (i, j) = key(x, y);
        grid[j * gw + i] = pts.len() as i32;
        active.push(pts.len());
        pts.push([x, y]);
        while !active.is_empty() {
            let a = rng.below(active.len());
            let [px, py] = pts[active[a]];
            let mut found = false;
            for _ in 0..20 {
                let (r, t) = (min_dist * (1.0 + rng.f()), rng.f() * TAU);
                let (x, y) = (px + r * t.cos(), py + r * t.sin());
                if minx <= x && x <= maxx && miny <= y && y <= maxy && region.contains(x, y) && fits(&grid, &pts, x, y) {
                    let (i, j) = key(x, y);
                    grid[j * gw + i] = pts.len() as i32;
                    active.push(pts.len());
                    pts.push([x, y]);
                    found = true;
                    break;
                }
            }
            if !found {
                active.swap_remove(a);
            }
        }
    }
    pts
}

pub fn rotated_rect(cx: f64, cy: f64, w: f64, h: f64, angle: f64) -> G {
    let (c, s) = (angle.cos(), angle.sin());
    let pts: Vec<Pt> = [(-w / 2.0, -h / 2.0), (w / 2.0, -h / 2.0), (w / 2.0, h / 2.0), (-w / 2.0, h / 2.0)]
        .iter()
        .map(|(dx, dy)| [cx + dx * c - dy * s, cy + dx * s + dy * c])
        .collect();
    geom::polygon(&pts, &[])
}

fn own_buildings<'a>(ctx: &'a Ctx) -> Vec<&'a G> {
    ctx.buildings.iter().filter(|b| geom::intersects(b, &ctx.polygon)).collect()
}

/// Where a path meets the road: the middle of the longest unfenced stretch of the boundary that is near the road.
fn gate_point(ctx: &Ctx, cfg: Cfg) -> Option<Pt> {
    let road = ctx.road.as_ref()?;
    let g = cfg.at("gravel");
    let mut front = geom::inter(&geom::exterior_line(&ctx.polygon), &geom::buffer(road, g.at("frontage_m").f(), 16));
    if !ctx.fences.is_empty() {
        let fe = geom::union_all(ctx.fences.iter().map(geom::dup).collect());
        front = geom::diff(&front, &geom::buffer(&fe, 0.4, 16));
    }
    let min_gate = g.at("min_gate_m").f();
    let best = geom::lines_g(&front).into_iter().filter(|l| geom::length(l) >= min_gate).max_by(|a, b| geom::length(a).partial_cmp(&geom::length(b)).unwrap())?;
    let pl = Polyline::new(geom::coords(&best));
    Some(pl.at(pl.length() * 0.5))
}

fn gravel_path(ctx: &Ctx, free: &G, cfg: Cfg, rng: &mut Rng) -> G {
    let g = cfg.at("gravel");
    let own = own_buildings(ctx);
    if own.is_empty() || geom::is_empty(free) {
        return geom::empty();
    }
    let Some(start) = gate_point(ctx, cfg) else { return geom::empty() };
    let sp = geom::point(start[0], start[1]);
    let target = own.iter().min_by(|a, b| geom::distance(*a, &sp).partial_cmp(&geom::distance(*b, &sp)).unwrap()).unwrap();
    let end = match target.nearest_points(&sp) {
        Ok(seq) => [seq.get_x(0).unwrap_or(start[0]), seq.get_y(0).unwrap_or(start[1])],
        Err(_) => return geom::empty(),
    };
    let length = (end[0] - start[0]).hypot(end[1] - start[1]);
    if length < g.at("min_length_m").f() || length > g.at("max_length_m").f() {
        return geom::empty();
    }
    let mid_t = 0.35 + 0.3 * rng.f();
    let (nx, ny) = (-(end[1] - start[1]) / length, (end[0] - start[0]) / length);
    let off = (rng.f() * 2.0 - 1.0) * g.at("wobble_m").f();
    let mid = [start[0] + (end[0] - start[0]) * mid_t + nx * off, start[1] + (end[1] - start[1]) * mid_t + ny * off];
    let curve: Vec<Pt> = (0..14)
        .map(|i| {
            let t = i as f64 / 13.0;
            let a = [(1.0 - t).powi(2), 2.0 * (1.0 - t) * t, t * t];
            [a[0] * start[0] + a[1] * mid[0] + a[2] * end[0], a[0] * start[1] + a[1] * mid[1] + a[2] * end[1]]
        })
        .collect();
    let w0 = rad(rng, g.at("width_m").f2());
    let strip = geom::buffer_flat(&geom::line(&curve), w0 / 2.0);
    let dots: Vec<G> = curve.iter().step_by(2).map(|p| geom::buffer(&geom::point(p[0], p[1]), w0 / 2.0 * (0.85 + 0.3 * rng.f()), 6)).collect();
    geom::inter(&geom::union2(&strip, &geom::union_all(dots)), free)
}

/// Vegetable beds: (rectangle, angle, centre), aligned with the main building.
fn beds(ctx: &Ctx, free: &G, cfg: Cfg, rng: &mut Rng) -> Vec<Rect> {
    let b = cfg.at("beds");
    let n = (b.at("max").f()).min((b.at("per_1000m2").f() * geom::area(&ctx.polygon) / 1000.0).round()) as i64;
    let own = own_buildings(ctx);
    if n <= 0 || own.is_empty() {
        return vec![];
    }
    let main = own.iter().max_by(|a, b| geom::area(**a).partial_cmp(&geom::area(**b)).unwrap()).unwrap();
    let ring = geom::min_rotated_rect(main);
    let mut best = (0.0, 0.0);
    for w in ring.windows(2) {
        let l = (w[1][0] - w[0][0]).hypot(w[1][1] - w[0][1]);
        if l > best.0 {
            best = (l, (w[1][1] - w[0][1]).atan2(w[1][0] - w[0][0]));
        }
    }
    let angle = best.1;
    let size = b.at("size_m").f2();
    let (w, h) = (size[0], size[1]);
    let bd = b.at("building_distance_m").f();
    let mut allowed = geom::diff(free, &geom::union_all(ctx.buildings.iter().map(|p| geom::buffer(p, bd, 16)).collect()));
    if !ctx.fences.is_empty() {
        let fd = b.at("fence_distance_m").f();
        allowed = geom::diff(&allowed, &geom::union_all(ctx.fences.iter().map(|f| geom::buffer(f, fd, 16)).collect()));
    }
    let inner = geom::buffer(&allowed, -0.5, 16);
    let mut cands = poisson_disc(rng, &Region::from_geom(&inner), geom::bounds(&inner), geom::area(&inner), 1.0);
    rng.shuffle(&mut cands);
    let rim_m = b.at("rim_m").f();
    let mut out: Vec<Rect> = vec![];
    for [cx, cy] in cands {
        let rect = rotated_rect(cx, cy, w, h, angle);
        let rim = geom::buffer(&rect, rim_m, 16);
        if allowed.contains(&rim).unwrap_or(false) && out.iter().all(|r| !geom::intersects(&rim, &r.rect)) {
            out.push(Rect { rect, angle, centre: [cx, cy] });
            if out.len() as i64 == n {
                break;
            }
        }
    }
    out
}

fn reg(g: &G) -> Region {
    Region::from_geom(g)
}

/// Patches and clutter of one yard.
pub fn garden(ctx: &Ctx, cfg: Cfg) -> Res {
    let pid = ctx.id.as_str();
    let mut res = Res::default();
    let free = ctx.free_area(cfg, true);
    if geom::is_empty(free) {
        return res;
    }
    let canopy = ctx.sh.canopy_m;
    let gravel = gravel_path(ctx, free, cfg, &mut Rng::keyed(&format!("{pid}:detail:gravel")));
    let beds = beds(ctx, free, cfg, &mut Rng::keyed(&format!("{pid}:detail:beds")));
    let rim_m = cfg.p("beds.rim_m").f();
    let bed_geom = geom::union_all(beds.iter().map(|r| geom::dup(&r.rect)).collect());
    let rim = geom::union_all(beds.iter().map(|r| geom::buffer(&r.rect, rim_m, 16)).collect());

    let d = cfg.at("dirt");
    let mut rng = Rng::keyed(&format!("{pid}:detail:dirt"));
    let mut dirt_parts: Vec<G> = vec![];
    if !beds.is_empty() {
        dirt_parts.push(geom::diff(&rim, &bed_geom));
    }
    for b in &ctx.buildings {
        for pc in geom::polys(b) {
            let ring = Polyline::new(pc.ext);
            let n = ((ring.length() / d.at("wall_spacing_m").f()) as usize).max(1);
            for i in 0..n {
                if rng.f() > d.at("wall_keep").f() {
                    continue;
                }
                let p = ring.at((i as f64 + rng.f()) / n as f64 * ring.length());
                let r = rad(&mut rng, d.at("wall_radius_m").f2());
                dirt_parts.push(blob(&mut rng, p[0], p[1], r, 0.35, 1.4, d.at("blob_vertices").i() as usize, None));
            }
        }
    }
    let fb = geom::bounds(free);
    let free_reg = reg(free);
    for _ in 0..(d.at("open_per_1000m2").f() * geom::area(&ctx.polygon) / 1000.0).round() as usize {
        for _try in 0..30 {
            let (x, y) = (fb[0] + rng.f() * (fb[2] - fb[0]), fb[1] + rng.f() * (fb[3] - fb[1]));
            if free_reg.contains(x, y) {
                let r = rad(&mut rng, d.at("open_radius_m").f2());
                dirt_parts.push(blob(&mut rng, x, y, r, 0.4, 1.3, d.at("blob_vertices").i() as usize, None));
                break;
            }
        }
    }

    let lit = cfg.at("litter");
    let mut rng = Rng::keyed(&format!("{pid}:detail:litter"));
    let mut litter_parts: Vec<G> = vec![];
    for t in &ctx.trees {
        let r = canopy * t[2] * lit.at("radius_factor").f();
        for _ in 0..lit.at("blobs_per_tree").i() {
            let (a, dist) = (rng.f() * TAU, rng.f() * r * 0.5);
            let rr = r * (0.7 + 0.4 * rng.f());
            litter_parts.push(blob(&mut rng, t[0] + dist * a.cos(), t[1] + dist * a.sin(), rr, 0.35, 1.2, 8, None));
        }
    }

    let lw = cfg.at("lawn");
    let mut rng = Rng::keyed(&format!("{pid}:detail:lawn"));
    let shades = lw.at("shades").fvec();
    let (base_cols, mix) = palette(ctx, &lw.at("colours").svec(), &shades, lw.at("terrain_mix"), 1.0);
    let (tone_cols, _) = palette(ctx, &lw.at("tone_colours").svec(), &lw.at("tone_shades").fvec(), lw.at("terrain_mix"), 1.0);
    let poly_reg = reg(&ctx.polygon);
    let (pb, pa) = (geom::bounds(&ctx.polygon), geom::area(&ctx.polygon));
    let mut lawn = geom::empty();
    if mix < 0.99 {
        // the lawn is another colour than the ground under it: draw it
        let mut blobs = vec![];
        for [x, y] in poisson_disc(&mut rng, &poly_reg, pb, pa, lw.at("blob_spacing_m").f()) {
            if rng.f() < lw.at("keep").f() {
                let r = rad(&mut rng, lw.at("blob_radius_m").f2());
                blobs.push(blob(&mut rng, x, y, r, 0.3, 1.3, 10, None));
            }
        }
        lawn = geom::union_all(blobs);
    }
    let mut rng = Rng::keyed(&format!("{pid}:detail:tones"));
    let mut tone: [Vec<G>; 2] = [vec![], vec![]];
    for (i, [x, y]) in poisson_disc(&mut rng, &poly_reg, pb, pa, lw.at("tone_spacing_m").f()).into_iter().enumerate() {
        if rng.f() < lw.at("tone_keep").f() {
            let r = rad(&mut rng, lw.at("tone_radius_m").f2());
            tone[i % 2].push(blob(&mut rng, x, y, r, 0.4, 1.5, 8, None));
        }
    }

    let mut layers: BTreeMap<&'static str, G> = BTreeMap::new();
    layers.insert("gravel", gravel);
    layers.insert("verge_gravel", geom::dup(&ctx.verge_g));
    layers.insert("bed", bed_geom);
    layers.insert("dirt", geom::union_all(dirt_parts));
    layers.insert("verge_dirt", geom::dup(&ctx.verge_d));
    layers.insert("litter", geom::union_all(litter_parts));
    let [t0, t1] = tone;
    layers.insert("tone_a", geom::union_all(t0));
    layers.insert("tone_b", geom::union_all(t1));
    layers.insert("lawn", lawn);
    let mut rects: BTreeMap<&'static str, Vec<Rect>> = BTreeMap::new();
    rects.insert("bed", beds);
    let flat = Flat { lawn: &base_cols, tone_a: tone_cols.first().copied(), tone_b: tone_cols.get(1).copied() };
    let got = assemble(&mut res.patches, free, &layers, &flat, &rects, &mut Rng::keyed(&format!("{pid}:detail:colours")));
    res.clutter = clutter(ctx, cfg, &got, free, canopy);
    res
}

fn layer_g<'a>(got: &'a BTreeMap<&'static str, G>, name: &str) -> Option<&'a G> {
    got.get(name)
}

fn clutter(ctx: &Ctx, cfg: Cfg, got: &BTreeMap<&'static str, G>, free: &G, canopy: f64) -> Vec<crate::emit::Clutter> {
    let pid = ctx.id.as_str();
    let c = cfg.at("clutter");
    let mut em = Emitter::new(ctx.sh);
    let tint = Tint::new(ctx, cfg);
    let seed = |purpose: &str| seed_of(&format!("{pid}:detail:{purpose}"));

    let gravel_g = geom::union_all(vec![layer_g(got, "gravel").map_or_else(geom::empty, geom::dup), layer_g(got, "verge_gravel").map_or_else(geom::empty, geom::dup)]);
    let dirt_g = geom::union_all(vec![layer_g(got, "dirt").map_or_else(geom::empty, geom::dup), layer_g(got, "verge_dirt").map_or_else(geom::empty, geom::dup)]);
    let litter = layer_g(got, "litter").map_or_else(geom::empty, geom::dup);
    let bed = layer_g(got, "bed").map_or_else(geom::empty, geom::dup);
    let open_ground = if geom::is_empty(&bed) { geom::dup(free) } else { geom::diff(free, &bed) }; // clutter stays off the tilled soil
    let bare = geom::union_all(vec![geom::dup(&gravel_g), geom::dup(&dirt_g), geom::dup(&litter)]);
    let grass = if geom::is_empty(&bare) { geom::dup(&open_ground) } else { geom::diff(&open_ground, &bare) };

    let walls_polys: Vec<_> = ctx.buildings.iter().flat_map(geom::polys).collect();
    let d_wall = DistIdx::new(&[], &walls_polys);
    let mut edge_lines: Vec<Vec<Pt>> = vec![];
    for p in geom::polys(&ctx.polygon) {
        edge_lines.push(p.ext);
    }
    for f in &ctx.fences {
        edge_lines.push(geom::coords(f));
    }
    let d_edge = DistIdx::new(&edge_lines, &[]);
    let d_tree = |x: f64, y: f64| {
        ctx.trees.iter().map(|t| ((x - t[0]).hypot(y - t[1]) - canopy * t[2] * 0.5).max(0.0)).fold(f64::INFINITY, f64::min)
    };
    let grass_r = reg(&grass);
    let dirt_r = reg(&dirt_g);
    let gravel_r = reg(&gravel_g);
    let open_r = reg(&open_ground);
    let litter_r = reg(&litter);

    // tufts: denser by the fences and walls, in clumps with gaps between them
    let t = c.at("tuft");
    let edge_m = t.at("edge_m").f();
    let (t_lawn, t_edge) = (t.at("lawn").f(), t.at("edge").f());
    let (clump_a, clump_m) = (t.at("clump").f(), t.at("clump_m").f());
    let tuft_lawn = |x: f64, y: f64| {
        let near = d_edge.dist(x, y).min(d_wall.dist(x, y));
        let base = t_lawn + (t_edge - t_lawn) * (1.0 - near / edge_m).clamp(0.0, 1.0);
        base * noise::clump(x / CELL_M, y / CELL_M, clump_a, clump_m / CELL_M, 1500)
    };
    let vmax = t_lawn.max(t_edge).max(0.04) * (1.0 + clump_a);
    let s_tuft = seed("tuft");
    let pts = lattice(s_tuft, t.at("spacing_m").f(), geom::bounds(&open_ground), vmax, |x, y, u| {
        let dens = if grass_r.contains(x, y) {
            tuft_lawn(x, y)
        } else if dirt_r.contains(x, y) {
            0.04
        } else if open_r.contains(x, y) {
            tuft_lawn(x, y) * 0.5
        } else {
            return false;
        };
        u < dens.clamp(0.0, 1.0)
    });
    // grass nobody mows (beside a fence or a wall) grows tall, the rest of the lawn has some clover among it
    let (mut tall, mut clover, mut plain): (Vec<Pt>, Vec<Pt>, Vec<Pt>) = (vec![], vec![], vec![]);
    for p in pts {
        let unmown = d_edge.dist(p[0], p[1]).min(d_wall.dist(p[0], p[1])) < t.at("unmown_m").f();
        let r = point_rng(s_tuft ^ 0x7A11, p[0], p[1]).f();
        if unmown && r < t.at("tall").f() {
            tall.push(p);
        } else if !unmown && r < t.at("clover").f() {
            clover.push(p);
        } else {
            plain.push(p);
        }
    }
    let span = t.at("scale").f2();
    em.emit("tall_grass", s_tuft, &tall, span, Some(&tint));
    em.emit("clover", s_tuft, &clover, span, Some(&tint));
    em.emit("tuft", s_tuft, &plain, span, Some(&tint));

    // flowers in clusters of one species
    let f = c.at("flower");
    let species = f.at("species").entries();
    let weights: Vec<f64> = species.iter().map(|(_, s)| s.at("share").f()).collect();
    let wsum: f64 = weights.iter().sum();
    let inner = geom::buffer(&grass, -0.4, 16);
    let inner_r = reg(&inner);
    let s_flower = seed("flower");
    let keep = f.at("cluster_keep").f();
    let grass_r2 = reg(&grass);
    for [cx, cy] in lattice(s_flower, f.at("cluster_spacing_m").f(), geom::bounds(&inner), keep, |x, y, _| inner_r.contains(x, y)) {
        let mut r = point_rng(s_flower ^ 0xF10E, cx, cy);
        let pick = r.f();
        let mut acc = 0.0;
        let mut idx = species.len() - 1;
        for (i, w) in weights.iter().enumerate() {
            acc += w / wsum;
            if pick < acc {
                idx = i;
                break;
            }
        }
        let (kind, spec) = species[idx];
        let size = spec.at("size").fvec();
        let k = r.int(size[0] as i64, size[1] as i64);
        let radius = f.at("cluster_radius_m").f();
        let mut pts: Vec<Pt> = vec![];
        for _ in 0..k {
            let (a, rr) = (r.f() * TAU, radius * r.f().sqrt());
            let p = [cx + rr * a.cos(), cy + rr * a.sin()];
            if grass_r2.contains(p[0], p[1]) {
                pts.push(p);
            }
        }
        em.emit(kind, s_flower, &pts, f.at("scale").f2(), None);
    }

    // pebbles on gravel, soil and along walls
    let p = c.at("pebble");
    let wall_m = p.at("wall_m").f();
    let walls_buf = if ctx.buildings.is_empty() { geom::empty() } else { geom::buffer(&geom::union_all(ctx.buildings.iter().map(geom::dup).collect()), wall_m, 16) };
    let zone = geom::inter(&geom::union_all(vec![geom::dup(&gravel_g), geom::dup(&dirt_g), walls_buf]), &open_ground);
    let zone_r = reg(&zone);
    let (pg, pd, pw) = (p.at("gravel").f(), p.at("dirt").f(), p.at("wall").f());
    let s_pebble = seed("pebble");
    let pts = lattice(s_pebble, p.at("spacing_m").f(), geom::bounds(&zone), pg.max(pd).max(pw), |x, y, u| {
        if !zone_r.contains(x, y) {
            return false;
        }
        let dens = if gravel_r.contains(x, y) {
            pg
        } else if dirt_r.contains(x, y) {
            pd
        } else if open_r.contains(x, y) {
            pw * (1.0 - d_wall.dist(x, y) / wall_m).clamp(0.0, 1.0)
        } else {
            return false;
        };
        u < dens
    });
    em.emit("pebble", s_pebble, &pts, p.at("scale").f2(), None);

    // rocks: seldom, more at the edge and on bare soil
    let r_ = c.at("rock");
    let edge_r = r_.at("edge_m").f();
    let rock_grass = |x: f64, y: f64| r_.at("lawn").f() + (r_.at("edge").f() - r_.at("lawn").f()) * (1.0 - d_edge.dist(x, y) / edge_r).clamp(0.0, 1.0);
    let s_rock = seed("rock");
    let vmax = r_.at("dirt").f().max(r_.at("lawn").f()).max(r_.at("edge").f());
    let pts = lattice(s_rock, r_.at("spacing_m").f(), geom::bounds(&open_ground), vmax, |x, y, u| {
        let dens = if gravel_r.contains(x, y) {
            0.0
        } else if dirt_r.contains(x, y) {
            r_.at("dirt").f()
        } else if open_r.contains(x, y) {
            rock_grass(x, y)
        } else {
            return false;
        };
        u < dens
    });
    em.emit("rock", s_rock, &pts, r_.at("scale").f2(), None);

    // leaves under trees and on the litter
    let lf = c.at("leaf");
    let reach = lf.at("tree_reach_m").f();
    let crowns = geom::union_all(ctx.trees.iter().map(|t| geom::buffer(&geom::point(t[0], t[1]), canopy * t[2] * 0.5 + reach, 16)).collect());
    let zone = geom::inter(&geom::union_all(vec![geom::dup(&litter), crowns]), &open_ground);
    let zone_r = reg(&zone);
    let s_leaf = seed("leaf");
    let (lit_v, tree_v) = (lf.at("litter").f(), lf.at("tree_lawn").f());
    let pts = lattice(s_leaf, lf.at("spacing_m").f(), geom::bounds(&zone), lit_v.max(tree_v), |x, y, u| {
        if !zone_r.contains(x, y) {
            return false;
        }
        let dens = if litter_r.contains(x, y) {
            lit_v
        } else if open_r.contains(x, y) {
            tree_v * (1.0 - d_tree(x, y) / reach).clamp(0.0, 1.0)
        } else {
            return false;
        };
        u < dens
    });
    em.emit("leaf", s_leaf, &pts, lf.at("scale").f2(), None);

    let s_ = c.at("stick");
    let zone = geom::inter(&litter, &open_ground);
    let zone_r = reg(&zone);
    let s_stick = seed("stick");
    let pts = lattice(s_stick, s_.at("spacing_m").f(), geom::bounds(&zone), s_.at("litter").f(), |x, y, _| zone_r.contains(x, y));
    em.emit("stick", s_stick, &pts, s_.at("scale").f2(), None);
    em.out
}

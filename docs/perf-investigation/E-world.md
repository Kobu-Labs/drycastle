# E: World representation and physics world

Read-only investigation, 2026-11-13, worktree `areas-not-parcels`. Scope: `godot/MapLoader/*`, chunk GLBs and collision meshes, `godot/ShooterCore/Colliders`, `Environment`, `Water.cs`, `godot/shooter/BotNav.cs`, ADR 0004, `docs/pipeline.md`. Baseline: `docs/perf-audit.md` (2026-11-11).

Scale: the lead's facts (48 chunks, 12,175 trees, 998 visual nodes, 399 collision nodes) are used as primary. **Caveat:** the on-disk `out/village/map` in the main checkout (`/home/jjakubec/coding/drycastle/out/village/map`, read only) is a different build: `map_version` `0.10.0-village`, 41 chunks, 20,824 trees, 886 parcels, 96,846 collision triangles, 1,191,158 visual triangles. Its numbers are quoted where they change the picture; the lead should confirm which build is the reference.

---

## 1. Verdict

**Fit with changes.** One artifact (chunk GLBs, visual and collision nodes together), trimesh colliders built at load, one static body per parcel (so a parcel swap is a `Disabled` toggle), tree solids shared by shape key, and a shared lamp body is a sound representation for 16 players on this village, and Jolt's broadphase is not what limits it. What does not fit is the cost of getting that world into a process: the server reads every visual triangle it never draws (92% of triangles on the 0.10 build), builds 399 separate bodies and about 62,000 tree-solid nodes at startup, and runs terrain collision on an 85,838-triangle trimesh that is 89% of all collision. Those costs grow with map area, not with players, so the representation stops fitting at map load, not at tick load. Expect it to break first when the map grows to about three times the village (roughly 60,000 trees and 1,200 parcels, which puts about 190,000 tree-solid nodes into the scene tree; UNVERIFIED threshold, derived from the per-tree node count in E-09). Two correctness gaps matter more than speed: a mod's parcel swap leaves the parcel's trees and fences solid (E-05), and the join handshake never compares map builds (E-06).

---

## 2. What is healthy

- **One collision mesh per parcel, one body each, hidden visual.** Parcel swaps need no rebuild: `ReplaceParcel` only disables shapes (`godot/MapLoader/MapLoader.cs:563-580`, `:586-592`). Per-parcel bodies are what makes that cheap. CONFIRMED.
- **Headless load already skips the client-only work.** `CollisionOnly` is set from the role (`godot/shooter/Shooter.cs:329`). Visuals are freed before materials are applied (`godot/MapLoader/MapLoader.cs:349-355`), the triangle count is skipped (`:357`), clutter, meadow and cables return early (`godot/MapLoader/MapLoader.Clutter.cs:45`, `godot/MapLoader/MapLoader.Cables.cs:70`), placed objects get no model (`godot/MapLoader/MapLoader.Objects.cs:192`), and placed-pole lamps are dropped (`godot/shooter/Shooter.cs:342`). CONFIRMED.
- **Shared flat material.** `FlatMaterial()` returns one `StandardMaterial3D` for every map node (`godot/MapLoader/MapLoader.cs:299-313`). This is the first step of audit T2-20, now done; the 998 materials are gone. CONFIRMED.
- **Tree solids share shapes.** `SolidShapeKeys` quantises to 1 mm and `SolidShape` caches one `Shape3D` per key (`godot/ShooterCore/Colliders/PropColliders.cs:289-303`, `godot/MapLoader/MapLoader.Trees.cs:100-108`). Shapes are no longer per tree (audit T2-27, shape half, fixed). Bodies are filled detached and attached once (`godot/MapLoader/MapLoader.Trees.cs:58-61`). Bodies are per chunk and per role (trunk or crown), so the body count is at most 96. CONFIRMED.
- **Shot-only cover is its own layer, and queries pick their mask.** `CollisionLayers` (`godot/ShooterCore/Colliders/PropColliders.cs:12-33`) lets bullets stop on crowns while players and cars pass; the server mask is `Bullets` (`godot/shooter/Net/NetServer.cs:726`), the client shot mask `WorldShots`. Vehicle hulls are on their own layer and the on-foot solid is a separate body (layer 8, `PropColliders.cs:22-23`). CONFIRMED.
- **Lamp poles: one body, one shape.** All 110 pole boxes are children of one `StaticBody3D` sharing one `BoxShape3D` (`godot/shooter/StreetLamps.cs:168-175`). The headless server skips the pool and the per-frame process (`godot/shooter/StreetLamps.cs:57`, `:66`). Audit T3-021 is fixed. CONFIRMED.
- **Ground height and parcel lookups are bucketed.** `GroundHeight` uses 8 m buckets (`godot/MapLoader/MapLoader.cs:506-528`, `TerrainCell = 8f` at `:474`), `ParcelIndex` is a grid (`ParcelAt`, `godot/MapLoader/MapLoader.cs:70`), and `TreeColliders.Index` buckets trees into 32 m cells (`godot/ShooterCore/Colliders/PropColliders.cs:236-285`). CONFIRMED.
- **Bot navigation is baked from the real colliders, cached, and rationed.** The mesh is baked from the world layer with trunks taken out (`godot/shooter/BotNav.cs:115-130`), one tile per chunk, keyed by a SHA of the manifest, objects and lamps (`godot/shooter/BotNav.cs:155`). One route is answered per tick (`godot/shooter/Net/NetServer.Bots.cs:101-118`). CONFIRMED.
- **Water is pure and shared.** `ShooterCore.Water` is Godot-free; `Contains` is a bounding-box test then an even-odd scan (`godot/ShooterCore/Water.cs:79-96`). The largest body has 933 vertices (`out/village/map/water.json`), so one `BodyAt` per player per tick is at most 4 box tests plus about 1,000 edge tests, well under 0.1 ms (arithmetic; UNVERIFIED in time). ADR 0004's "flat per body, point-in-polygon plus two numbers" matches the code. CONFIRMED.
- **Physics queries are on the static world only and the world is static.** Nothing in the world moves, so lag compensation does not need to rewind terrain or trees (relevant to agent B). CONFIRMED by absence of moving static bodies in `MapLoader`.

---

## 3. Findings

Legend: audit id where one exists; status of audit items is given where the audit's citation is now stale.

### E-01 Terrain collision is a trimesh and is 89% of collision triangles
- **Mechanism.** Each `terrain_collision` mesh becomes a `ConcavePolygonShape3D` with `BackfaceCollision = true` (`godot/MapLoader/MapLoader.cs:427-446`, `:432`). Its faces are copied out with `GetFaces()` and kept for the life of the map (`:443`, field `:31`) and bucketed into the 8 m grid (`:~490-505`). Every character, vehicle and ray that touches the ground is a query against that mesh.
- **Citation.** `godot/MapLoader/MapLoader.cs:427-446`, `:31`, `:443`. Manifest totals: `out/village/map/manifest.json`.
- **Cost.** 0.10 build: `terrain_collision` 85,838 of 96,846 collision triangles (89%). Retained managed copy: 85,838 × 3 × 12 B ≈ 3.1 MB, plus the bucket dictionary (UNVERIFIED size). The audit's 72,078 collision triangles would give the same ratio (terrain is the larger share in every build).
- **Fix.** A `HeightMapShape3D` per chunk (regular grid, ADR-free), keeping the trimesh only for chunks with holes (the three bunker chunks: the hatch is a hole, ADR 0009 item 6). Keep the 4 m grid near bunkers (ADR 0009 item 4 requires the full 4 m grid there; a plain 8 m heightfield would bring back the doorway step). Terrain in the artifact is already a regular grid (`manifest.json` `terrain`: `grid_step_m` 4.03, `collision_decimate` 2, `cells_per_chunk` 62).
- **Effort.** M. Pipeline: write the height grid per chunk (no new geometry), plus a hole list. Loader: branch on the node name. No protocol bump; a map version bump (artifact shape).
- **Confidence.** Structure CONFIRMED. Runtime gain UNVERIFIED (Jolt's trimesh BVH versus a heightfield has not been timed here).
- **Audit.** T3-063 (open). This adds the 89% share and the ADR 0009 constraint.

### E-02 The server still parses and builds all visual geometry (T2-26 partly fixed)
- **Mechanism.** The glTF importer builds every mesh of every chunk (`GenerateScene`, `godot/MapLoader/MapLoader.cs:330`) before the headless branch frees the visual nodes (`:349-355`). So the server pays the full import cost for 1.19 M visual triangles (0.10 build) or 689,649 (audit build) and frees them. The collision `MeshInstance3D` is hidden but not freed (`BuildCollider`, `:429`), so its `ArrayMesh` stays resident.
- **Citation.** `godot/MapLoader/MapLoader.cs:324-357`, `:427-429`.
- **Cost.** Visual import time and transient memory: UNVERIFIED. Resident: the collision `ArrayMesh` copies of 96,846 triangles (about 3.5 MB of positions at 12 B per vertex, times 3, plus the mesh's own index and normal data; UNVERIFIED).
- **Fix.** E-03 removes the cause. Short of that: free collision meshes after `CreateTrimeshShape` in the headless path (`mi.QueueFree` after the shape is taken). S.
- **Effort.** S (free the collision mesh), M for E-03.
- **Confidence.** CONFIRMED (code path). Time saved UNVERIFIED.
- **Audit.** T2-26: the "skip materials, free visuals, lamp visuals" half is done. Remaining: the import itself, and the resident collision mesh.

### E-03 The server reads the whole chunk GLB to get collision (no collision-only artifact)
- **Mechanism.** The only artifact is `chunks/chunk_<ix>_<iy>.glb` with visual and collision nodes. The server opens every byte of every chunk; the collision it keeps is 7.6% of triangles (0.10 build). The per-parcel visuals and `_collision` nodes live in the same file by design (`docs/pipeline.md:441-453`), and the loader finds them by node name.
- **Citation.** `godot/MapLoader/MapLoader.cs:92-145` (`LoadSteps`), `:324-357`; `docs/pipeline.md:441-453`.
- **Cost (0.10 build).** Chunks 79.3 MB (41 files, largest 4.2 MB). Visual triangles 1,191,158 versus collision 96,846 (the server keeps 7.5% of triangles it reads). Audit build: 44 MB, 998 visual nodes, 689,649 visual triangles, 72,078 collision triangles.
- **Fix.** A second per-chunk file, `collision/chunk_<ix>_<iy>.bin` (float32 positions plus uint32 indices, or a GLB with only `_collision` and `terrain_collision` nodes), written by the same export from the same trimesh, so the two cannot drift. The server reads only it; the client keeps the GLB. A manifest entry under `files` and a map version bump.
- **Effort.** M. Pipeline: about one export step plus a writer (the export already has the collision arrays, `pipeline/src/drycastle_pipeline/export.py`). Loader: a reader that builds `ConcavePolygonShape3D` from the arrays (`ConcavePolygonShape3D.SetFaces` takes the same data). No protocol bump; `docs/shooter-server.md` and `docs/pipeline.md` need updating. ADR 0004's "server and client must load the same map build" still holds: both read the same manifest hash.
- **Estimated size.** 96,846 × 3 × 12 B = 3.5 MB (plus indices, about 4.6 MB), against 79.3 MB now: the server's read drops by about 94% (arithmetic).
- **Confidence.** Sizes CONFIRMED. Time and memory saved UNVERIFIED.
- **Audit.** New (the audit's T2-26 proposal was to flag the visuals away; a separate artifact is the cleaner version and is not in the audit).

### E-04 Terrain rim samples every 1 m cell of the terrain box on the main thread (T2-28 open)
- **Mechanism.** `BuildTerrainBoundary` calls `GroundHeight` once per 1 m cell over the merged terrain AABB, then allocates three arrays of that size (`godot/MapLoader/MapLoader.cs:156-168`). It runs after the last progress step, so the loading screen does not move during it.
- **Citation.** `godot/MapLoader/MapLoader.cs:156-190`, `:165-168`, `:175`, `:187`. `BuildBoundary` default on (`:151`).
- **Cost.** The 0.10 manifest gives 41 chunks in a 2,000 m square; the terrain AABB is at most that, so up to about 4.0 M cells (arithmetic, upper bound). Each `GroundHeight` is a bucket lookup plus up to a few triangle tests. Time UNVERIFIED; the `boundary` value in the load log is the measurement. Arrays: 4 M floats (16 MB) plus 4 M bools/floats, transient.
- **Fix.** Sample the 4 m terrain grid (or the rim cells of the heightfield from E-01), and move the rim into its own load step. S.
- **Effort.** S.
- **Confidence.** Mechanism CONFIRMED. Duration UNVERIFIED.
- **Audit.** T2-28, still open.

### E-05 A parcel swap leaves that parcel's trees and fences solid; trees have no parcel or ground-area identity
- **Mechanism.** `ReplaceParcel` disables only the building's `_collision` shapes (`godot/MapLoader/MapLoader.cs:571`; restore `:590`). The parcel's fence and tree props collision is in `_propsShapes` and is left alone (the summary of the swap, `godot/MapLoader/MapLoader.cs:17-25` and the method's comment). Tree solids come from `trees.json`, built per chunk and role (`godot/MapLoader/MapLoader.Trees.cs:32-61`); `TreeInstance` has no parcel, ground area or ID (`godot/ShooterCore/Colliders/PropColliders.cs:152`). So there is no way to remove the trunks and crowns of one parcel or one forest. The shooter never calls `ReplaceParcel` (grep: only `godot/viewer/Viewer.cs`), so the swap path does not exist on the server; a mod that adds it must do the same swap on both sides.
- **Citation.** `godot/MapLoader/MapLoader.cs:563-592`, `godot/MapLoader/MapLoader.Trees.cs:32-61`, `godot/ShooterCore/Colliders/PropColliders.cs:152`, `docs/pipeline.md:270-277` (trees are not in the chunk collision and are not tied to parcels; viewer edits to trees reach the game only after re-export).
- **Cost.** Not a per-tick cost. It is a correctness gap: any replaced parcel keeps its tree solids (per the 0.10 build, 20,824 trees; the number inside any parcel is UNVERIFIED).
- **Fix.** Give each tree a `parcel` (or `ground_area`) field in `trees.json` (format bump), build tree bodies per parcel or per parcel group so a swap can disable them the way it disables building shapes (the same `Disabled` toggle, one body per chunk-role-parcel bucket). Or give the mod an explicit "clear solids in polygon" API that edits the tree bodies (costlier, no ID).
- **Effort.** M (pipeline field plus per-parcel tree bodies; the loader change follows the building path).
- **Confidence.** Mechanism CONFIRMED. Whether a mod needs tree removal is OPINION (the glossary's replacement scene is "the parcel's base contents", and trees are contents).
- **Audit.** New.

### E-06 The join handshake does not compare map builds
- **Mechanism.** `HelloMsg` carries `Wire.ProtocolVersion` only, and the server rejects on protocol mismatch (`godot/shooter/Net/NetServer.cs:385-387`; `godot/ShooterCore/Net/Wire.cs:181`). The map is never compared. The map's identity affects spawn indices, glass pane indices (ADR 0007 consequence: an index is stable only between builds that read the same data), tree and parcel solids, and water. `docs/shooter-server.md:17` says "Server and clients must use the same map build"; nothing enforces it.
- **Citation.** `godot/shooter/Net/NetServer.cs:383-390`, `godot/shooter/Net/NetClient.cs:246`, `godot/ShooterCore/Net/Wire.cs:181`, `godot/shooter/Shooter.cs:350` (the map version is printed, not sent), `docs/shooter-server.md:17`, `:182`.
- **Cost.** Not a runtime cost. A mismatch shows as silent desync (a player walks into a tree the server does not have). Each mismatch case is a player who installed a different map folder by hand; `docs/shooter-server.md:182` says there is no automatic map download.
- **Fix.** Put the manifest's SHA (already hashed per chunk in `manifest.json`) in `Hello` and `Welcome`, reject on mismatch with a message. Costs a protocol bump (24 to 25) and one manifest hash on the wire (32 bytes once per join).
- **Effort.** S.
- **Confidence.** CONFIRMED (absence). Impact frequency OPINION.
- **Audit.** New. Crosses into agent A (netcode) and H (growth, distribution).

### E-07 Bot sight is a ray per enemy per think, uncached
- **Mechanism.** `Look` loops over every enemy and, for each one within 120 m and in the view cone (or already engaged), casts one ray (`godot/ShooterCore/Bots/BotBrain.cs:280-291`; `SightRangeM` 120 at `:9`). A bot thinks every 0.15 s (`:34`, jittered at `:142`, rescheduled at `:165`). Each ray is `WorldDistance`, which builds an `Exclude` array per call (`godot/shooter/Net/NetServer.cs:718-731`; the array is cached only when no skip is given, `:708-715`, `:727`).
- **Citation.** `godot/ShooterCore/Bots/BotBrain.cs:9`, `:34`, `:142`, `:165`, `:280-291`; `godot/shooter/Net/NetServer.Bots.cs:124-131`; `godot/shooter/Net/NetServer.cs:708-731`.
- **Cost.** Rays per tick at most: bots × (living players − 1) ÷ 4.5 (0.15 s at 30 Hz). 8 bots among 16 players: about 27 per tick; 16 bots among 16: about 53 per tick. This is the worst case before the 120 m and cone filters (arithmetic). Per ray: UNVERIFIED (a few µs for a short ray against this scene is the expectation).
- **Fix.** Keep the filters (they are cheap and first); cache line-of-sight per bot pair for one think; or a 2D visibility test on the buildings' outlines. S for the cache.
- **Effort.** S.
- **Confidence.** Count CONFIRMED (arithmetic on the code). Cost UNVERIFIED.
- **Audit.** Partly T2-02 (the exclude array is cached when no skip is passed; `NetServer.cs:727` still allocates for the skip case). New as a bot-sight finding.

### E-08 Per-tick physics is 45 character moves against the same trimesh, not rays
- **Mechanism.** Each server player is a `CharacterBody3D` that calls `MoveAndSlide` once per tick (`godot/shooter/Player.cs:395`); each vehicle is a `CharacterBody3D` that calls it once per tick (`godot/shooter/ServerCar.cs:14`, `:303`). `MoveAndSlide` shape-casts against the terrain trimesh (E-01), the tree compounds and the buildings. Rays are fewer: shots (one per shot, eight per shotgun shot, `PelletFan`), one per rocket per tick (`godot/shooter/Net/NetServer.cs:844`), turret sight, glass, vehicle `LiftToGround` only when leaving water (`godot/shooter/ServerCar.cs:136-147`, `:300`).
- **Citation.** `godot/shooter/Player.cs:395`, `godot/shooter/ServerCar.cs:14`, `:136-147`, `:300-303`, `godot/shooter/Net/NetServer.cs:844`.
- **Cost.** 16 players + 29 vehicles = 45 `MoveAndSlide` per tick, about 1,350 per second at 30 Hz (arithmetic). The per-call cost is UNVERIFIED; it is the number that decides whether E-01 matters for the tick.
- **Fix.** Not decided here: measure first (section 5). If E-01 is done, the same casts run against a heightfield.
- **Effort.** Measurement only.
- **Confidence.** Count CONFIRMED. Cost UNVERIFIED.
- **Audit.** T2-04 (vehicle parked cost) is still open; this adds the player side.

### E-09 Tree solids are 3 nodes per tree, about 62,000 nodes at 20,824 trees
- **Mechanism.** Each tree gives three `CollisionShape3D` nodes (one trunk, two crown parts) under one body per chunk and role (`godot/ShooterCore/Colliders/PropColliders.cs:179-202` builds the parts; `godot/MapLoader/MapLoader.Trees.cs:43-62`). Shapes are shared (E-shape-sharing above), but the nodes are not: each part is a `CollisionShape3D` node with its own transform.
- **Citation.** `godot/MapLoader/MapLoader.Trees.cs:43-62`, `:100-108`; `godot/ShooterCore/Colliders/PropColliders.cs:179-202`, `:289-303`.
- **Cost.** 3 nodes × 12,175 trees = 36,525 nodes (audit build); 3 × 20,824 = 62,472 nodes (0.10 build). Node memory in Godot is UNVERIFIED; a `Node3D`-derived `CollisionShape3D` is a heavy object (order of 1 KB each is the usual rule of thumb; UNVERIFIED here). Bodies: at most 96 (two roles per chunk).
- **Fix.** Add shapes through `PhysicsServer3D` (`body_add_shape` with a shared shape and transform) and keep `PropColliders` as the one source of geometry. No nodes, the same bodies. The audit's proposal stands. M.
- **Effort.** M.
- **Confidence.** Node count CONFIRMED. Memory and time UNVERIFIED.
- **Audit.** T2-27. The shape half is done; the node half is open.

### E-10 Trees ride on the chunk, not on the parcel: the 0.6 to 0.10 change left the two tree representations apart
- **Mechanism.** Tree visuals come from the chunk's `props` node (orthophoto trees, `docs/pipeline.md:446-453`) and parcel props (`parcel_<ID>_props`); tree solids come from `trees.json`. Nothing ties a solid to a visual, so the two can disagree (`docs/pipeline.md:277`: viewer edits reach the game only after re-export).
- **Citation.** `docs/pipeline.md:270-277`, `:446-453`, `godot/MapLoader/MapLoader.cs:385-399` (props visuals), `godot/MapLoader/MapLoader.Trees.cs:32`.
- **Cost.** Correctness and edit latency, not speed.
- **Fix.** The same ID as E-05 (a tree ID shared by its visual and its solid). M.
- **Effort.** M.
- **Confidence.** CONFIRMED (two sources, no shared key). Frequency OPINION.
- **Audit.** New.

### E-11 Water: ADR 0004 and pipeline.md describe an older model than the code builds
- **Mechanism.** ADR 0004 and `docs/pipeline.md:478-480` say "one body per parcel of kind `water`". The code builds bodies from `ground_units(parcels_doc)` (ground areas) and from regions with a `water_y` (`pipeline/src/drycastle_pipeline/export.py:358-361`, `pipeline/src/drycastle_pipeline/water.py:101-125`). The bodies carry A- and R- IDs (`out/village/map/water.json`: A-0003, A-0004, A-0006, R-0023), and the on-disk `parcels.json` has no A- entries (886 P- entries only: 526 garden, 360 building plot). Either the on-disk build is stale, or ground areas are not written to `parcels.json` as the code docstring (`pipeline/src/drycastle_pipeline/ground.py:13`) says. The glossary says ground areas have no stable ID, which the water bodies now contradict.
- **Citation.** `docs/adr/0004-water-bodies-in-map-artifact.md` (the "per parcel" rule), `docs/pipeline.md:478-480`, `pipeline/src/drycastle_pipeline/export.py:358-361`, `pipeline/src/drycastle_pipeline/water.py:101-125`, `out/village/map/water.json`, `out/village/map/parcels.json`.
- **Cost.** None at runtime. A water body is still one polygon with two numbers, which is cheap.
- **Fix.** Doc-only: amend ADR 0004 with a note that water bodies come from water ground areas and water regions (the ADR's own "revisit" clause allows it), and fix `docs/pipeline.md:478-480`. Verify the out build before trusting either. S.
- **Effort.** S.
- **Confidence.** Drift CONFIRMED by code and data. Cause of the on-disk mismatch UNVERIFIED.
- **Audit.** New.

### E-12 Map load is all at start, on the main thread, with no streaming and no cache (T2-29 open)
- **Mechanism.** `LoadSteps` loads every chunk in the manifest, then trees, objects, clutter and the rim (`godot/MapLoader/MapLoader.cs:92-145`). Each chunk parses its GLB, builds every visual node (client), and builds every collision trimesh on the main thread (`:324-357`, `:427-446`). Nothing is cached between runs; a 79 MB GLB parse runs on each start. The client loads everything before joining (`godot/shooter/Shooter.cs:320-330`).
- **Citation.** `godot/MapLoader/MapLoader.cs:92-145`, `:324-357`, `:427-446`; `godot/shooter/Shooter.cs:320-330`.
- **Cost.** Load time: UNVERIFIED (`map load phases` prints it). Resident memory on the client: visual triangles 1.19 M (0.10) or 689,649 (audit), unindexed for the props (audit T2-25, 1,147,350 vertices for 382,450 triangles, so about 3 vertices per triangle). At 28 B per vertex (position, normal, colour; UNVERIFIED layout) the audit's 689,649 triangles are about 58 MB of vertex data, before the GPU copy.
- **Fix.** At village scale: none needed beyond E-01, E-02 and E-04. Above it: a per-chunk cache of the built shape data keyed by the manifest chunk hash (audit T2-29 proposal), and a visibility range (audit T2-22).
- **Effort.** M to L (only when needed).
- **Confidence.** CONFIRMED (structure). Times and memory UNVERIFIED.
- **Audit.** T2-29, T2-25, T2-22 (open).

### E-13 Player download and map identity
- **Mechanism.** Clients install the map folder by hand (no download). The folder is the chunk GLBs plus data files; the players' download is the same folder the server reads.
- **Citation.** `docs/shooter-server.md:16-17`, `:182`.
- **Cost (0.10 build).** Folder 90.3 MB: chunks 79.3 MB, `clutter.json` 5.2 MB, `trees.json` 1.9 MB, `meadow.bin` 0.9 MB, `prims` 2.2 MB, other data under 0.5 MB. Audit build: 44 MB of chunks. Clutter and meadow are client-only and need not ship to a server (the server skips them, `MapLoader.Clutter.cs:45`), so a dedicated server folder is 81 MB here (`chunks` plus `trees`, `water`, `objects`, `prims`, `regions`, arithmetic).
- **Fix.** E-03 lets the server folder drop the visuals (the server still needs `chunks` for the collision files until E-03 is done, or a server-only export without the visual nodes is a pipeline flag). E-06 for identity.
- **Effort.** S (a server-only export flag) after E-03.
- **Confidence.** Sizes CONFIRMED (on-disk build). Audit-build size as quoted by the lead.
- **Audit.** New (distribution, not in audit).

---

## 4. Design alternatives considered (priced)

| Alternative | What it buys | Cost to move | Cheapest test |
|---|---|---|---|
| **Status quo** (one GLB per chunk, trimesh per node) | Nothing to change; works at village scale | 0 | n/a |
| **Heightfield terrain** (`HeightMapShape3D` per chunk, trimesh only for bunker chunks) (E-01) | Removes 85,838 trimesh triangles from the server and the client; no BVH build; the 8 m height grid is already there | M. Pipeline: write the grid and hole list per chunk, map version bump. Loader: a second node kind, a branch for chunks with holes. No protocol bump. Keep 4 m grid near bunkers (ADR 0009) | Time `CreateTrimeshShape` against `HeightMapShape3D` for one chunk in the load log, and the per-query cost of a ray over the chunk |
| **Collision-only artifact** for the server (E-03) | Server reads about 7.5% of the bytes it reads now (3.5 MB against 79.3 MB); no visual import on the server | M. Pipeline: one more writer in export (same arrays), a manifest entry, a version bump. Loader: a reader. Docs in `pipeline.md` and `shooter-server.md` | Write the collision arrays for one chunk and time the server load with and without visuals (`map load phases`) |
| **Merged static collision per chunk** (audit T3-062): one `StaticBody3D` per chunk, one shape per parcel | 399 bodies to at most 48 (arithmetic) | M. Loader only. Parcel swap still toggles each parcel's shape (`Disabled`), so ADR-free | Count bodies and time `ReplaceParcel` before and after |
| **Trees as bodyless instances with shape keys** (E-09) | Removes about 62k `CollisionShape3D` nodes; keeps the same geometry | M. `PhysicsServer3D` calls in `MapLoader.Trees.cs` (the audit's T2-27 route). Behaviour identical | Node count and `Performance` object count before and after `LoadSteps` |
| **Spatial grid for gameplay queries** (bots, vehicle probes) | Only helps if the broadphase is the cost, which nothing here shows; the trimesh BVH already prunes | M to L, a new index beside the physics world, and two sources of truth for the same geometry | Not worth testing before E-08 is measured |
| **Streaming the world** (load by chunk as the player moves) | Lower resident memory and load time at a much larger map | L. Changes spawns, bots' nav tiles (already per chunk, so they would help), vehicles and the server's whole-map view; needs a protocol story for out-of-range entities | Only if the map grows past about three times the village |
| **Per-parcel tree bodies with IDs** (E-05, E-10) | Parcel swaps can remove or replace trees; visual and solid agree | M. Pipeline: tree IDs and parcel link in `trees.json` (format bump). Loader: bodies per parcel bucket. Protocol: none | Swap a parcel in the viewer and check its trunks disappear (the viewer already has the probe in `ViewerEditSmoke.cs`) |
| **Map hash in the handshake** (E-06) | Prevents silent desync between builds | S. One protocol bump (24 to 25), one hash field on Hello and Welcome | Join with a deliberately altered map folder on a dev pair |

---

## 5. Needs measurement

| What | Why not static | Cheapest way |
|---|---|---|
| Visual import time and transient memory on the server (E-02) | Depends on the Godot importer's speed on the target CPU | `map load phases` log line (`godot/MapLoader/MapLoader.cs:146`, printed with `chunks`, `trees`, `objects`, `clutter`, `boundary`) and the `load=` value in `godot/shooter/Shooter.cs:350`, run headless with and without the visual free; `docker stats` memory after load |
| Rim boundary duration (E-04) | Depends on the terrain AABB and `GroundHeight` cost | The `boundary` field of the same `map load phases` line, one run |
| Trimesh versus heightfield cost (E-01) | BVH build and query cost are engine internals | Time `ConcavePolygonShape3D` build and one 1000-ray batch against a chunk's terrain (a Stopwatch in a headless scene), then the same against `HeightMapShape3D` for the same grid |
| Per-tick `MoveAndSlide` cost, 45 casts (E-08) | Needs a live tick | Tick-time monitor with 29 parked vehicles against none in loss-test mode (audit T2-04); plus a Stopwatch around `Player.cs:395` and `ServerCar.cs:303` summed per tick |
| Bot sight ray cost (E-07) | Needs bots in a match | The existing `server: bots ... routes ... mean ms` line covers routes; add a counter around `BotLineClear`, then run 8 bots with 8 humans for one minute |
| Tree-solid node memory (E-09) | Godot's node overhead is engine-specific | Object and node count monitors before and after `LoadSteps`, with `Performance.OBJECT_COUNT` and the static memory monitor |
| Resident collision mesh (E-02) | Depends on how Godot keeps `ArrayMesh` after `CreateTrimeshShape` | Static memory monitor on the server before and after the free in E-02 |
| Whether Godot-Jolt builds one compound per static body (E-09, E-01) | Addon source is not in this worktree | Read the addon's `StaticBody` shape handling, or time a ray across a 600-shape body against a 1-shape body |
| On-disk build versus the lead's reference (section header) | Two builds exist | Confirm which `out/village/map` is the reference before quoting numbers |

---

## Notes for the lead

- Items in the audit whose citations are stale: T2-20 step 1 (shared material, done), T2-26 (partly done), T2-27 (shape half done), T2-02 (PlayerRids cached, skip path still allocates), T3-021 (done), T3-064 (done for the server only).
- Other agents: **A (netcode)**: E-06 (the map hash belongs in Hello and Welcome). **D (vehicles)**: E-08 (vehicle `MoveAndSlide` per tick, `LiftToGround`). **F (rendering)**: E-12 vertex memory and T2-25 unindexed props. **G (server logic)**: E-07 (bot sight rays), the `new Array` in `NetServer.cs:727`. **H (growth)**: the threshold in section 1 and E-13 (distribution). **B (lag comp)**: static world, so terrain and trees need no rewind (section 2).

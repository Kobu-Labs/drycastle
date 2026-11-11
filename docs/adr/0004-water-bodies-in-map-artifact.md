# Water bodies: a water.json per map, flat surface and bed per water parcel

Decided for Shooter v2 (swimming and submerging, issue #138). Extends the map artifact of the base map; does not change the networking layer (ADR 0002).

**Format.** The base map gains an optional file `water.json` (listed under `files.water` in `manifest.json`). It holds one body per parcel of kind `water`, each with the parcel polygon and two heights: a flat surface (the median terrain height along the shoreline) and a flat bed (the lowest terrain height inside). `water.json` carries its own `version` (1). Adding the file is additive: maps without it, or older readers, treat the map as dry.

**Why per parcel and flat.** Parcels already carry water as a kind, so no new geometry is invented, and a flat surface and bed per body keeps the query a point-in-polygon test plus two numbers. Its cost is that depth is one value per body; a lake whose bed rises toward the shore is treated as deep everywhere inside. Revisit with a height grid if shallow shores matter for play.

**Where the logic lives.** `ShooterCore.Water` is a pure query over the parsed file (depth, surface, the feet and eye tests). It has no Godot dependency, so the client prediction and the server run the same movement step with the same answer. The shared movement step takes the water state as fields (`WaterDepth`, `WaterSurfaceY`, `HeadUnder`, `AirSpent`) that the caller fills from `Water.Sample` before each tick, so no new wire fields are needed: swim state is derived from position.

**Consequences.** Server and client must load the same map build, as they already must (`docs/shooter-server.md`). Any change to the water file format bumps its `version`.

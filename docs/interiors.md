# House interiors

A building detail turns into a **hollow house**: shell, floor plan, room types, doors. All generated; nothing drawn by hand.
Why: ADR 0040 (shell), 0041 (floor plan), 0042 (room types), 0043 (garage), 0044 (doors). Terms: GLOSSARY.

## Flow

| step | what | code (`pipeline/src/drycastle_pipeline/` unless said) |
|---|---|---|
| 1 detail | `config/buildings/<osm id>.yaml` with `interior: true`, openings (door, window, garage_door) | `photos/facade_pipeline.py apply` (docs/photo-to-building.md) |
| 2 room | floor, ceiling; which openings go through | `buildings/interior.py` (`inside_of`, `Inside.through`) |
| 3 frame | footprint minus walls, turned square to the longest wall, x inward from the door | `buildings/floorplan.py` (`_framed`) |
| 4 plan | garages, hall, programme, rooms, partitions, links; best of many candidates | `pipeline/rust/detail/src/floorplan.rs` |
| 5 meshes | inner walls, floor, ceiling, partitions (visual and collision) | `buildings/openings.py`, `buildings/interior.py` |
| 6 export | `out/village/map/interiors.json` | `export/scene.py`, `export/export.py` |
| 7 game | doors hung and opened | `godot/ShooterCore/Buildings/HouseDoors.cs`, `godot/shooter/Buildings/HouseDoorNode.cs`, `HouseDoorsNode.cs`, `godot/shooter/Net/Server/Houses/HouseDoorServer.cs` |

## Commands

```sh
make detail-rust-build     # planner engine; export and map run it first
make rules && make export  # after detail or override changes
make map                   # full village map
cd pipeline && uv run pytest tests/buildings/test_floorplan.py tests/buildings/test_interior.py
cd pipeline/rust/detail && cargo test
```

- `test_floorplan.py` is **skipped** when the engine is not installed.
- No tool in the repo draws a plan or checks that every room can be reached over the whole village.

## Add a house

1. Capture and measure it (docs/photo-to-building.md). `apply` writes `interior: true`, except for a building with `roof_part`.
2. By hand: add `interior: true` to the detail. It needs a `door` that goes through, else the plan has no hall.
3. `make rules && make export`.

Detail measured on the traced outline, and the cadastre correction changes that outline: list the id under
`keep_traced_footprint` in `config/overrides.yaml`.

## Shell rules

| value | rule |
|---|---|
| walls | `WALL_M` 0.3 m, inward of the footprint |
| floor | sill of the lowest door (else plinth, else 0.15 m), at least 0.05 m |
| ceiling | one storey: wall height / levels, 2.3 to 3.0 m, raised for door heads that fit, capped at the eave |
| window, door | goes through: sill not under the floor, head 0.05 m under the ceiling, not inside a recess |
| garage door | may start under the floor; head at least 1.5 m over it |
| partitions | 0.1 m; doorway 2.0 m high, archway 2.2 m |

## `interiors.json`

`{"version": 1, "axes": ..., "houses": [...]}`; local metres, x east, y north, z as the terrain. Per house:

| field | content |
|---|---|
| `id`, `parcel` | building, parcel |
| `room` | `polygon` (middle of the walls), `z0`, `z1`: shelter volume |
| `floor_z`, `ceiling_z`, `wall_m` | shell |
| `doorways` | per hinged door that goes through: `id`, `at` (outer face), outward `normal`, `width_m`, `height_m` |
| `doors` | leaves: `id`, `kind` hinged/garage, `pos` (mid wall), `yaw`, `width`, `height`, `colour`. The game reads these |
| `openness`, `style` | 0..1; closed/mixed/open |
| `rooms` | `id`, `kind` hall/room, `type`, `area_m2`, `polygon`, `windows`, `doors`, `garage_doors`, `links` (`to`, `kind` door/arch/open, `p`, `q`) |
| `partitions` | `p`, `q`, `gaps` (from, to, kind) |

The plan fields are missing when there is no plan (under 8 or over 350 m² inside the walls).

## Tuning

The plan depends only on footprint, openings and building id: same input, same plan; no per-house override.
Change rules, not houses. In `floorplan.rs`:

| want | look at |
|---|---|
| which rooms, how large | `programme`, `want`, `scaled` |
| room shapes, cuts | constants at the top (`SLIM`, `CLEAR_M`, `SNAP_M`, ...) |
| halls | `HALL_*`, `SPINE_MIN_DEPTH_M`, `NO_HALL_BELOW_M2` |
| which candidate wins | `faults`, `GOOD_FAULTS`, `TRIES` |
| unreachable rooms | `shut_off`, `BODY_M`, `MORE_TRIES` |
| garages | `garage_behind`, `GARAGE_*`, `CAR_M` |

A change to the search can change every house's plan.

## Not built

- furniture, upper floors and stairs, interior door leaves, interior lighting
- bots in houses, breaching
- houses as blast shelters (`room` is exported, the game does not read it)
- a hollow building cut in parts (`roof_part`)
- a drivable garage: its floor is the house floor, which can be above the ground

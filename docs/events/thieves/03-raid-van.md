# 03 Raid van

ADR 0021. The van drives in, lets the crew out, takes them back, drives off; three Raids make the event.

## Facts
- `roads.json`: 191 nodes, 251 edges (from, to, 2D polyline, width, `highway`, `oneway`). Read as raw text by `MapLoader`; `RoadNetwork.Parse` (`Minimap/MinimapMath.cs`) and `RoadSurfaces.Parse` keep polylines only. No graph, no z (sample `Map.GroundHeight`).
- No marked entry points. Chunks cover x −750..2250, y −1750..2500; 14 nodes lie outside, so edges cross the edge.
- Scripted placement precedent: `VehicleThrow` (`Events/Ballistics.cs`). Hit target with its own pose history: the Bigfoot (`PoseRing`).
- Ramming: how a `ServerCar` hurts players and shoves cars (`VehicleContact.cs`, `PushOnDeath` in `NetServer.Vehicles.cs`).

## Do
- `RoadGraph` (pure, `godot/ShooterCore/Thieves/` or `Roads/`): parse nodes and edges, shortest path (Dijkstra, length), `EntryPoints(extent)` = where an edge crosses the map extent, `PointsAlong` for stop candidates. Oneway ignored.
- Stop choice: the candidate with the most stealable targets within `stop_radius_m` (80), not used this event. Until 04 to 07 there are no targets: count spawned cars, fall back to nearest to a player.
- `RaidVan : IWorldEntity`, new wire type. State machine: Arriving, Opening, Parked, Closing, Leaving, Destroyed (wreck lies, then ends). Follows the path at `speed`, yaw from the path, height from the ground.
- Pushes through: players in its box are rammed as by a car, vehicles are shoved aside. Never brakes. Honk: an fx event at random intervals and when a player is within `honk_near_m`.
- Hit count 300 (explosive weight in data), hittable while driving, rewound from its own pose ring. Solid for players and vehicles while parked.
- Crew: on Opening, members spawn at the side-door point, one every `crew_stagger_s`. Boarding: a member reaching the door ends (counted as boarded).
- Leave when: Loot loaded ≥ `capacity` (8; counter fed by 04), `stay_seconds` (240) ran out, or the crew is dead. Call the living crew back first; wait at most `board_wait_s`.
- Destroyed: Raid over, crew switches to Flee, hook `LootReturnAll()` for 04.
- `ThievesEvent`: Raid sequence. Next Raid when the previous van left or was destroyed, or after `next_raid_s`; at most 2 vans. Crew size by player count. After the last van is gone: event won.
- Client: box van without the pack (a Kenney `van.glb` if simplest), door as a moving panel, wreck look, honk sound (reuse the closest existing id).

## Accept
- Tests: shortest path on a small graph, entry points on an extent, stop choice, van state machine (leave rules, destroyed while arriving), Raid sequencing to the win.
- `/event thieves`: three vans come and go by themselves with nobody near; the event ends won.

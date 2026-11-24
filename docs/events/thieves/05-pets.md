# 05 Pets

Pets as Loot, and the fence hop.

## Facts
- `godot/ShooterCore/Wildlife/`: `Animal` (wire type 50), `PetKeeper`, `Pen`. `AnimalState` uses 0-6 of a 3-bit field: **7 is the only free value**. `Home` and `_world` are readonly, `Position` has a private setter. `PetKeeper` keeps `Pet` private (`_byEntity` exists).
- A pet's world (`PennedWorld`) has no ground outside its Pen: released outside, it freezes. Pets do not use the navmesh and ignore fences.
- Fences: baked into chunk props + collision; solid for the navmesh (1.2 m against 0.25 m climb), gardens reachable only through the gate gap. Section geometry (a, b, za, zb, height, parcel; 4324 sections) is only in `data/village/work/props.json`, written by `pipeline/src/drycastle_pipeline/props/props.py` `make_fences`.
- Clips: jump and land exist (Base Locomotion InAir); no carry clip.

## Do
- Pipeline: export `fences.json` with the map (sections: two points with z, height, parcel/group). `docs/pipeline.md` updated. MapLoader + server read it.
- `Animal`: state Carried (value 7), allowed from every living state and back to Idle; `Carry()` / `MoveCarried(pos)` / `Release()`; while carried `Tick` does nothing. Released outside its Pen: walks straight home on open ground (an unpenned world until it is inside), then is penned again. Spec + docs regenerated.
- `PetKeeper`: look up a pet by entity, list living pets with their group for the target registry.
- Pet target: `Take` = Carry, the Thief moves it each tick (at its side, raised). Carrier killed: `Release`, the pet walks home; no lying Loot, no player use. Loaded in a van: the pet entity ends; van destroyed: it reappears at the van and walks home.
- A carried pet is still a hit target and dies of any hit as now.
- Fence hop: a scripted move for a gang member. Approach the nearest point of the group's fence line to the pet, `Hop` state (a short arc over the line, off the navmesh, jump + land clips), walk to the pet, grab (crouch), hop back out, Haul. No fence there (probability 0.5): plain walk.
- Client: carried pet drawn under the Thief's arm (the replicated position does it; a held pose, no clip).

## Accept
- Tests: Carried transitions; release outside the Pen ends back inside; carried pet killed by a hit; hop lands on the other side of the section; pipeline test for `fences.json`.
- `make map` output for the village otherwise unchanged (MAP=out/village/map).

## Pipeline half: done (`a63594d`)
- `fences.json` in the map (`files.fences`), version 1: `{"version":1,"axes":..,"fences":[{"parcel":"P-0412","group":"G-0003"|null,"a":[x,y],"b":[x,y],"za":z,"zb":z,"height_m":1.2,"type":"picket"|null}]}`. Local metres as `parcels.json`; gate gaps are simply absent. Format: `docs/pipeline.md` "Fence sections".
- Game side still to do: MapLoader + server read it. The village map must be rebuilt in the main tree (`make map-update MAP=out/village/map`) before the file exists there: the game must work without it (no hop, gate route).

## Done: notes
Code: pure `godot/ShooterCore/Wildlife/` (`Animal.cs` state Carried + `Carry` / `MoveCarried` / `Release` + `IPennedWorld`, `Pen.cs` `PennedWorld.Inside` / `Open`, `PetKeeper.cs` `TryGet` / `EntityOf` / `LivePets` / `SeedOf` / `End` / `Respawn` / `PenOf` / `Round` / `Updated`), `godot/ShooterCore/Thieves/` (`PetTarget.cs` `PetLootConfig`, `IPetWorld`, `PetSource`, `FenceHop`; `FenceLines.cs` `FenceBook` = fences.json; `GangMember.Hop.cs` the hop; small edits in `GangMember.cs`, `GangMember.Stealing.cs`, `Loot.cs`, `ThievesData.cs`), server `NetServer.Thieves.Loot.Pet.cs` (+ one registration line in `NetServer.Thieves.Loot.cs`), client `LootProp.Pet.cs`, `AnimalsClient.cs`, `GangMemberView.cs`, data `thieves.json` `loot.pet` + `view.clips.hop_in`. Tests `PetLootTests.cs`. Protocol 48 (merge renumbers).

### Wire (protocol 48)
- Animal state byte value **7 Carried** (low 3 bits, was the only free value). Gang member states **17 HopIn, 18 HopOut** (`GangRules.StateOf` knows them). No new event kind, no new entity type.
- The carried pet is the animal entity itself, moved by the Thief every tick; the Thief's hp byte carried bits say Pet (kind 1 + 1) as for any Loot, the client's carried prop for Pet is empty.

### Decisions
- **Map data**: `fences.json` is read by the server only (`FenceBook.Parse`, `NetServer.LoadFences`, once, lazily; same way as `church.json`), not by `MapLoader` (nothing in the viewer or client needs it; keeps `MapLoader` out of the merge). Local metres become Godot axes (x, z = -y, height = z). Only sections with a group count. No file, unknown version or broken file: empty book, logged: no hop.
- **Target**: id = the pet's entity id. Free = alive and not held. Plan per pet (cached until it has moved `plan_recheck_m` 2): in a pen with fence sections, try the `fence_candidates` (6) nearest sections for a hop: outside point `hop_out_m` (0.9) out, landing `hop_in_m` (0.9) in (outside must be off the pen, landing inside, outside must be on the navigation mesh within `nav_snap_m` 1.5); none works: not a target. No sections for the group, or the pet is out on open ground: plain walk to the pet, which must be on the mesh. Work spot: outside point (hop) or the pet. `Places` lists every reachable pet for the van's stop choice.
- **Hop** (`GangMember.Hop.cs`): Approach to the outside point, `HopIn` (arc, clock `hop_seconds` 0.7, off the mesh, top `height_m + hop_clear_m` above the higher end), then Steal where it first walks straight to the pet at `in_walk_speed_mps` 3 (the Steal clock is infinite until it is within `grab_m` 0.8, then `steal_seconds`), then `HopOut` (walks straight back to the landing point, no clock; then the arc out, clock `hop_seconds`), then Haul (or Seek when the pet went or died). All three are Working. The walk/arc phases are told apart by the state's clock (infinite = walking), no flag. Pet gone while it is inside: out first, then Seek. Called back or fleeing inside: put outside at once (`LeaveHop`, like `LeaveAloft`). Killed in the air: `HopGroundY`, it falls to the ground like one killed aloft.
- **Carry**: `PetSource.Carried` (every tick from `GangMember.Tick`) puts the pet at the Thief's right (`carry_right_m` 0.4, `carry_forward_m` 0.25, `carry_up_m` 0.8) facing as the Thief. A pet is a hit target as before (it dies of any hit).
- **Carrier killed**: `LiesWhenDropped` false, `Return` = `Animal.Release`: inside its Pen it stands; outside it walks straight home over open ground (`PennedWorld.Open`), penned again once inside; with no ground at the spot (water) it is home at once. No lying Loot, no player use.
- **Pet killed while carried**: `PetSource.CarriedKilled` (from the keeper's `Killed`); the server calls `GangMember.LoseCarried()` on the carrier (hands empty, Seek again).
- **Loaded in a van**: no hook exists for "loaded", so the source detects it: a held pet whose carrier said nothing (`Carried`) for 3 ticks is in a van (or off the map with a fleeing Thief): its entity ends (`PetKeeper.End`) and it is kept as loaded. Van destroyed (`Return`): `PetKeeper.Respawn` makes a new entity at the carrier's last point, outside the pen, and it walks home. Reset (`/event cancel`): loaded pets come back at their homes; carried ones are released. A round reset: `PetKeeper.Round` changed, old records bring nothing back (the new round has its own pets).
- Pets stolen and loaded stay gone until the round resets (a van that drove off keeps them).
- `Animal.Tick` does nothing while Carried (not even the clock); `OnDamage` while carried never starts a flight.
- Client: `AnimalState.Carried` freezes the standing pose (Idle clip, paused) at the replicated position; `hop_in` clip name in `view.clips` (empty: held pose). The Thief's way out has no clip of its own (walks, then the arc).

### Open
- Not run in the game (brief): the hop, the walk in the garden (straight line, ignores houses and bushes inside a Pen), the carried pose, the arc heights, every number in `loot.pet` are unit-verified only.
- The village map has no `fences.json` until it is rebuilt (`make map-update MAP=out/village/map`): then no hop; a garden whose ground is not on the navigation mesh is not a target at all.
- A pet put down outside its Pen that cannot step towards home (water, a wall) stands and tries again after its idle time; there is no teleport fallback after a time.
- No jump clip is named (`view.clips.hop_in` empty; the Synty jump and land clips of Base Locomotion InAir, ticket 08). The land clip is not played.
- A Thief that fled off the map with a pet: the pet is loaded by the 3-tick rule, so it is gone until reset (as other Loot).
- A gate gap in a fence is not used: a fenced garden is always hopped (the sections nearest the pet decide), the gap only matters to the mesh.
- `MapLoader` does not read `fences.json` (server only); the viewer shows the baked fences as before.

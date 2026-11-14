# 05 Pets

Pets as Loot, and the fence hop.

## Facts
- `godot/ShooterCore/Wildlife/`: `Animal` (wire type 50), `PetKeeper`, `Pen`. `AnimalState` uses 0-6 of a 3-bit field: **7 is the only free value**. `Home` and `_world` are readonly, `Position` has a private setter. `PetKeeper` keeps `Pet` private (`_byEntity` exists).
- A pet's world (`PennedWorld`) has no ground outside its Pen: released outside, it freezes. Pets do not use the navmesh and ignore fences.
- Fences: baked into chunk props + collision; solid for the navmesh (1.2 m against 0.25 m climb), gardens reachable only through the gate gap. Section geometry (a, b, za, zb, height, parcel; 4324 sections) is only in `data/village/work/props.json`, written by `pipeline/src/drycastle_pipeline/props.py` `make_fences`.
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

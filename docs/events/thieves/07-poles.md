# 07 Poles

Stealing connectors off utility poles, with the electrocution.

## Facts
- Poles: placed objects `prim:pole_*` (15 variants, ~109 placed), `docs/utility-poles.md`. The server has their nodes (`MapLoader.ObjectNodes`), height = asset `size_m` Y, wire seats via `SeatsOf` (`MapLoader.Cables.cs`). Collision: a 0.4 m column.
- No electric effect or sound exists. Sparks: `ShotFx.cs`, `RocketFx.cs`, `VehicleFx.cs`. Thrown bodies: `Corpses.cs` (`ThrowFromBlast`), drawn by `RagdollTumble.cs`.
- Clips: `KnockDown_Begin/Loop/End`, `Stun_*` (Sword Combat).

## Do
- Pole targets: every pole with wires near the van's stop. Nothing goes missing on the pole; a pole can be robbed again after `pole_cooldown_s`.
- The Thief uses the climb of 06 on the pole column to the top wire seat, Steal there, climbs down with a connector (small prop in one hand), Haul. The connector is Loot like the rest (lying, returnable: return just removes it).
- Electrocution: on each Steal at a pole top, `electrocute_chance` (0.05). States added: Shocked (`shock_seconds`, at the top: beams and sound), Falling (an arc to the ground, tumble), Down (`down_seconds`, knockdown loop), GetUp, then Seek the same pole again. Costs no hits. Shot while Down: normal hits.
- Fx event: electric beams between the Thief and the wire seats, sparks, a whizz/crackle sound (generate one, as `scripts/make_church_bells.py` does, into `assets/drycastle-generated-sounds/` and `sounds.json`), heard to ~40 m.
- A member killed while climbing falls the same way, then lies dead.

## Accept
- Tests: with chance 1 the Thief goes Shocked, Falling, Down, GetUp and climbs again; with 0 it never does; no hit count lost; fall ends on the ground.
- State machine docs regenerated.

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
- Fx event: electric beams between the Thief and the wire seats, sparks, a whizz/crackle sound (generate one, as `scripts/sounds/make_church_bells.py` does, into `assets/drycastle-generated-sounds/` and `sounds.json`), heard to ~40 m.
- A member killed while climbing falls the same way, then lies dead.

## Accept
- Tests: with chance 1 the Thief goes Shocked, Falling, Down, GetUp and climbs again; with 0 it never does; no hit count lost; fall ends on the ground.
- State machine docs regenerated.

## Done: notes
Code: pure `godot/ShooterCore/Thieves/` (`ConnectorTarget.cs` config + `IPoles` + `ConnectorSource`, `AloftShock.cs`, `GangMember.Shock.cs` the four states; small edits in `GangMember.cs`, `GangMember.Stealing.cs`, `Loot.cs`, `LootBook.cs`, `ThiefWork.cs`, `ThievesData.cs`, `ThievesFx.cs`), map `godot/MapLoader/Objects/MapLoader.Poles.cs`, server `NetServer.Thieves.Loot.Connector.cs`, client `ShockFx.cs`, `ThievesClient.cs`, `GangMemberView.Hazard.cs`, `LootProp.Connector.cs`, sound `scripts/sounds/make_shock_crackle.py` -> `assets/drycastle-generated-sounds/shock_crackle.ogg` (`sounds.json` `shock.crackle`, 40 m; manifest entry, assets README). Tests `PolesTests.cs`. Protocol 48 (merge renumbers).

### Decisions
- **Poles**: `MapLoader.PoleSites()` = both ends of every cable between two placed `prim:pole_*` objects (the server draws no wires but keeps `_cables` and the object nodes), height = asset `size_m` Y times the object's scale, top seat = highest `SeatsOf` seat. Server adapter `ServerPoles` builds the list on first use (needs map + nav mesh): base = of eight points `base_offset_m` (0.9) around the column the one the mesh has closest (within 1.5 m, not inside the column), top = straight above the base at the top seat's height (or `top_fraction` 0.9 of the height without a seat). A pole with no mesh point near is no target. The Thief climbs a vertical line beside the column, feet at seat height.
- **Source**: `ConnectorSource(cfg, IPoles, Func<float> seconds)`. `Take` only starts the cooldown (`pole_cooldown_s` 120 on the server's `_elapsed`), the pole is no target meanwhile; `Return` does nothing (the connector lying, sending it back just removes it); `Reset` clears cooldowns. Lying connectors are the generic `LyingLoot`.
- **Hook**: optional `IStealSource.AloftHazard(id, rng) -> AloftShock?` (default null; times shock/down/get-up). `IThiefWork.Hazard(target, thief, rng)` (default null) -> `RaidWork` -> `LootBook.AloftHazard` asks the source and raises `LootBook.Shocked(member, target)`. The member rolls it once on reaching the top (`TickClimbUp`), before WorkAloft's clock matters; the work clock restarts after the second climb.
- **States** (19-22 at the end): Shocked (clock `shock_seconds`, at the top) -> Falling (gravity 9.81 to the base's height, no clock) -> Down (clock `down_seconds`, `Stance.Prone` hit volume: normal hits count, a hit does not end it) -> GetUp (clock `get_up_seconds`) -> ClimbUp on the same target (claim kept; target gone: Seek). Shocked and Falling count as aloft (a kill there falls on and lies dead at the base, called back or Flee puts it on the ground at once); all four are Working (no fight). Costs no hits. Chance 0.05 rolled every time it reaches the top, also after a get-up.
- **Fx**: `ThievesFxKind.Shock` (1) in event 232, id = the member's world entity id, position = where it hangs. Sent from `LootBook.Shocked` (hooked inside `RegisterConnectorLoot`, the marked line of `InitLoot`: no edit of `ThiefGang`/`GangRoster`/`ThievesTick`). Client: `ShockFx` = camera-facing jagged ribbons from the man's chest to the wire seats (`MapLoader.WireSeatsNear`, poles within 3 m, nearest 8), redrawn 20 times a second for `shock_seconds`, a spark emitter, a flickering blue light, `shock.crackle` once. Nothing is built beyond 80 m or headless.
- **Sound**: `make_shock_crackle.py` (numpy + ffmpeg, deterministic): 50 Hz buzz, two whizz sweeps, crackle clicks thinning out, a snap at the start; 2.2 s mono.
- **Client body**: clips `knockdown_begin/loop/end`, `stun_loop` in `gang.view.clips` (empty): Shocked shivers, Falling tumbles onto its back, Down lies flat centred on its feet head forward (the shape of the prone hit volume), GetUp rises. With a clip named the body stays upright and the clip plays.
- **Prop**: connector = brown ceramic insulator with two rings on a steel bracket and a hook (primitives), hangs from the right hand; lying on its side.

### Open
- Not run in the game (brief): the base points beside real columns (junction/twin poles, a cabinet or lamp in the way), the vertical climb past crossarms and lamp arms, the beams' look and size, the sound by ear, the pose of the lying body, the climb and shock times.
- A pole that stands where the navigation mesh has no point within 1.5 m of any of the eight base candidates is not a target (not counted: nobody logs which).
- With ~109 poles in the village the connector will dominate the Thief's kind roll only by `weight` (1, as the other kinds); every pole also counts in the van's stop choice (`Places`).
- Feet at seat height: a Thief aloft stands on the crossarm level, the body reaches above the wires (no per-pole offset).
- `GangRules.StateOf` now accepts values up to 22: 17 and 18 (ticket 05) are undefined in this branch and read as that value's enum cast; on merge both tickets edit that line (the bound is the highest state).
- Voice lines may fire while a Thief lies Down (`GangVoice.CanSpeak` unchanged).

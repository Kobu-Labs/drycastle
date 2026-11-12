# Aircraft can be shot down, by the flak truck

Decided for the anti-aircraft work after Shooter v2. **Reverses** the v2 rule that aircraft take no damage (code comments in `Aircraft.cs` and the glossary entry "Aircraft"; "anti-air is v3").

**What changes.** An aircraft (the attack helicopter, the helicopter helper, the AC-130's airframe) has hit points (`hp` in the `aircraft` kinds of `killstreaks.json`: helicopter 600, gunship 1500). At 0 it ends as `WorldDespawnReason.Destroyed`: the manager despawns it, its streak instance ends with the new end reason `Destroyed` (`KillstreakService.EndForEntity`, so the owner is released from the gun and no longer shielded or frozen), and clients show a blast where it flew. The world entity type now sends its hp, as a percentage of the kind's (a byte holds 255, the gunship has 1500).

**The weapon.** The flak truck (`vehicles.json` `flak` block, glossary "Flak truck") is the counter: a driver and a gunner whose view aims a turret that fires airburst shells. Aircraft fly a fixed circle, so the server works out a shell's whole flight when it is fired (`FlakRules.Plan`): a march of the shell and the aircraft along their paths in 1/120 s steps, a burst at the closest approach inside the 12 m fuse. The damage lands when the shell arrives. The gunner's skill is the lead; the server, not the client, decides the hit.

**Why this and not hitscan.** An aircraft is 45 to 120 m up and 220 to 420 m out on a circle, and a hitscan round would make leading it pointless. A fast shell with a proximity fuse keeps the skill (lead the circle) and forgives the last few metres.

**Wire.** Protocol 19: entity type 4 (a turret's aim record, one per flak vehicle: the world yaw and pitch, the gunner's id in the health and weapon bytes, a fixed position so it is never re-sent), event kind 160 (the gunner's ammo), the despawn reason on the client for the blast. The gunner's tracers and bursts reuse the gunship's coalesced FX events.

**Consequences.** Killstreak aircraft are no longer safe: a team with a flak truck can end an enemy's helicopter or AC-130, which changes what a streak is worth (a balance question, see the balance pass #211). A counter for the aircraft (flares) and a hand-held lock-on launcher are not part of this decision.

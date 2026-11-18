# The Lock-on launcher locks on the server and plans its missile at the fire

Decided for issue #347. ADR 0005 made every aircraft destroyable and left out "a hand-held lock-on launcher"; this is it.

**What it is.** A secondary weapon (`weapons.json` `strela`, the `lock` block; glossary "Lock-on launcher"). It fires only at an **Aircraft** it holds a complete **Lock** on, and its **Homing missile** always arrives. There is no dumb fire: without a Lock the trigger clicks and the missile stays in the tube.

**The Lock is the server's.** Once a tick, for each player with the launcher in hand, the server picks the enemy Aircraft in the lock circle (`cone_deg` round the aim), inside `range_m` (kinds in `unlimited_range`, the UAV, at any distance) and with nothing solid between (`LockRules.Pick`, the shot query's line of sight). The Lock runs the `Launcher.Lock` machine (Idle, Locking, Locked; `LockOn`): `seconds` in the circle completes it, an Aircraft out of the circle or out of sight makes the progress wait for `grace_seconds`, then the Lock is gone. Stowing the launcher, dying or firing ends it. Never a teammate's Aircraft, never one's own. The client is told its Lock when the phase, the Aircraft or the tracking changes and counts the progress on from there; it decides nothing.

**Why not client-side.** The Lock gates a hit that cannot miss and warns another player. A client that reported its own Lock could fire at will and never warn anyone.

**The missile is planned, not simulated.** An Aircraft flies a fixed circle, so the server works out at the fire when a missile at `projectile_speed` meets it (`LockRules.Intercept`, the flak shell's march, ADR 0005) and queues the hit for that moment. No per-tick homing, no snapshot entity. Clients draw the missile closing on the Aircraft at the rate that lands it there on time (`LockRules.Chase`), so the picture and the hit agree by construction. If the Aircraft is gone when the missile arrives (expired, shot down by something else), it bursts in the air where they would have met and hurts nothing.

**Damage.** Through `DamageAircraft` like every anti-aircraft (ADR 0005): 500 a missile, so the UAV (200) falls to one, the helicopter (600) to two, the AC-130 (1500) to three. One in the tube and two in reserve, refilled at the spawn: a full load downs one AC-130 and nothing else.

**The blast is the Explosion event.** A hit that leaves the Aircraft flying uses `hit_blast_radius` (6 m); the hit that shoots it down uses `kill_blast_radius` (20 m), past the 9 m that raises a Blast wave. In the air it raises no Dust cloud (glossary). The client skips the Aircraft's own 12 m crash blast when the missile's kill blast is already there.

**The owner is warned.** The Aircraft's owner (a helicopter's pilot, gunner and passengers) hears and sees the worst of what is on their Aircraft (`LockAlert`): a Lock past `alert_at_fraction` (0.4), a complete Lock, a missile in the air. Sent to the owner only, on change.

**Wire.** Protocol 64: event kinds 246 `LockState` (to the shooter: phase, Aircraft, tracking, progress, lock seconds), 247 `LockAlert` (to the Aircraft's owner: level, Aircraft), 248 `HomingLaunch` (to everyone: id, owner, weapon, origin, Aircraft, flight seconds; the Explosion with the same id ends it). `LockCodec`.

**Consequences.** Aircraft now have a counter every player can carry, at the cost of their pistol. The flak truck keeps its place: it has the ammunition to down several Aircraft and needs no Lock. Flares are not part of this decision. Bots do not use the launcher.

**Amendment (#374): flown helicopters are targets too.** Every enemy air vehicle that is piloted and off the ground (the utility helicopter, #289's attack helicopter later) is a Lock candidate under the same rules (enemy pilot, circle, range, sight, 2.5 s, grace). A vehicle moves freely, so its missile is not planned at the fire: the server flies it (`StepVehicleHoming`), each tick re-aiming at the vehicle and moving `projectile_speed` m/s (`LockRules.Pursue`); the client draws the same pursuit (`HomingLaunch` with Seconds 0). A hit goes through `DamageVehicle` credited to the shooter, with the Aircraft's blast radii. A missile whose helicopter is destroyed, lands, empties or outlives `projectile_life` bursts in the air. Vehicle and world-entity ids do not meet, so the existing wire carries either. Everyone aboard is warned.

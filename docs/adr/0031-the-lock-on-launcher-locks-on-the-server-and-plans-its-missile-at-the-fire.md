# The Lock-on launcher locks on the server and plans its missile at the fire

Decided for issue #347. ADR 0005 made every aircraft destroyable and left out "a hand-held lock-on launcher"; this is it.

**What it is.** A secondary weapon (`weapons.json` `strela`, the `lock` block; glossary "Lock-on launcher"). It fires only at an **Aircraft** it holds a complete **Lock** on, and its **Homing missile** always arrives. There is no dumb fire: without a Lock the trigger clicks and the missile stays in the tube.

**The Lock is the server's.** Once a tick, for each player with the launcher in hand, the server picks the enemy Aircraft in the lock circle (`cone_deg` round the aim), inside `range_m` (kinds in `unlimited_range`, the UAV, at any distance) and with nothing solid between (`LockRules.Pick`, the shot query's line of sight). The Lock runs the `Launcher.Lock` machine (Idle, Locking, Locked; `LockOn`): `seconds` in the circle completes it, an Aircraft out of the circle or out of sight makes the progress wait for `grace_seconds`, then the Lock is gone. Stowing the launcher, dying or firing ends it. Never a teammate's Aircraft, never one's own. The client is told its Lock when the phase, the Aircraft or the tracking changes and counts the progress on from there; it decides nothing.

**Why not client-side.** The Lock gates a hit that cannot miss and warns another player. A client that reported its own Lock could fire at will and never warn anyone.

**The missile is planned, not simulated.** An Aircraft flies a fixed circle, so the server works out at the fire when a missile at `projectile_speed` meets it (`LockRules.Intercept`, the flak shell's march, ADR 0005) and queues the hit for that moment. No per-tick homing, no snapshot entity. Clients draw the missile closing on the Aircraft at the rate that lands it there on time (`LockRules.Chase`), so the picture and the hit agree by construction. If the Aircraft is gone when the missile arrives (expired, shot down by something else), it bursts in the air where they would have met and hurts nothing.

**Damage.** Through `DamageAircraft` like every anti-aircraft (ADR 0005): 500 a missile, so the UAV (200) falls to one, the helicopter (600) to two, the AC-130 (1500) to three. One in the tube and two in reserve, refilled at the spawn: a full load downs one AC-130 and nothing else.

**The blast is the Explosion event.** A hit that leaves the Aircraft flying uses `hit_blast_radius` (6 m); the hit that shoots it down uses `kill_blast_radius` (20 m), past the 9 m that raises a Blast wave. In the air it raises no Dust cloud (glossary). The client skips the Aircraft's own 12 m crash blast when the missile's kill blast is already there.

**The owner is warned.** The Aircraft's owner hears and sees the worst of what is on their Aircraft (`LockAlert`): a Lock past `alert_at_fraction` (0.4), a complete Lock, a missile in the air. Sent to the owner only, on change.

**Wire.** Protocol 64: event kinds 246 `LockState` (to the shooter: phase, Aircraft, tracking, progress, lock seconds), 247 `LockAlert` (to the Aircraft's owner: level, Aircraft), 248 `HomingLaunch` (to everyone: id, owner, weapon, origin, Aircraft, flight seconds; the Explosion with the same id ends it). `LockCodec`.

**Consequences.** Aircraft now have a counter every player can carry, at the cost of their pistol. The flak truck keeps its place: it has the ammunition to down several Aircraft and needs no Lock. Flares and the flown helicopters of #289 (vehicles, not Aircraft) are not part of this decision. Bots do not use the launcher.

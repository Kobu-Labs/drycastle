# Every seat declares its cover and every pane its kind

Decided for #301 and #325 (the vehicle contract). **Replaces** decision 3 of ADR 0007 as `docs/glass.md` stated it (a bullet goes through every pane, whole or shattered, and every rider inside is open to it) and **revises** ADR 0012 (exposure as a flag of the seat, `exposed_gunner` as a flag of the vehicle). ADR 0007's rectangles, hit points and channel, ADR 0008's panes with outlines and ADR 0012's exposed seat are unchanged.

**Seat cover.** Every seat of `vehicles.json` says `"cover"`:
- `armoured`: the rider is hidden behind armour. No bullet, pellet or mounted-gun round reaches them, whatever panes the hull has. Only explosive damage does (a blast, a rocket, the vehicle blowing up).
- `glass`: the rider sits behind panes. A round reaches them only through a pane, or an open side, or a shattered pane, as that pane's kind says. This is what every closed seat was already, plus the kinds.
- `exposed`: ADR 0012, unchanged.

**Pane kind.** Every pane says `"kind"` (default `normal`); `glass.json` `kinds` holds the hit points (`reinforced`: 300, `normal`: `vehicle_pane_hp`, 60; a pane's own `hp` still wins; the crack look is the shared one, driven by the hit-point fraction).
- `normal`: today. The round damages the pane and goes on with full damage.
- `reinforced`: while it holds it stops the round: the pane takes the damage, whatever is behind it none, the hull takes the round as when it stops on the hull. Shattered, it is a hole and rounds go through with full damage. There is no reduced-damage mode.

**Which pane guards which seat is geometry, not a list.** The round's way through the vehicle is `GlassRules.Through` (in by the first pane or open side the ray crosses, out by the next from inside). `PaneRules.Pass` cuts it short at a reinforced pane that holds: on the way in, nothing inside is reached; on the way out, the riders on the way are reached and the pane stops the round. An open side never stops a round. A rider between two panes is reached or not by where the shot enters and leaves, not by a seat-to-pane table.

**Who is a target.** `RiderRules.BulletsReach` drops the riders of armoured seats from the targets of every bullet (`ShotTargets`: hitscan, pellets, melee), the roof gun and the door gun. Blasts read the unfiltered targets. `exposed_gunner` stays as the shorthand for "the gunner seat is exposed" (it also keeps the ADR 0012 difference for the weapons that never reached it through the hull); a seat's own `cover` may not contradict it. `"exposed": true` on a seat still reads as `"cover": "exposed"`; `vehicles.json` uses `cover` everywhere, and `scripts/vehicles/standardize_vehicle.py` writes it. A seat that declares nothing reads as `glass` and fails the contract test (`every_seat_has_a_cover`).

**Blasts and the vehicle exploding.** Unchanged, and now stated: a blast from outside reaches every rider; a rider of a covered seat (glass or armoured) takes `CoveredSeatSplash` (half) of it when the blast is outside the hull, an exposed one all of it (`NetServer.SplashShare`). When the vehicle explodes everybody aboard dies, whatever the seat (`DriverDamage`, spawn protection aside). Armoured does not mean blast-proof.

**What a covered rider is to a bullet today.** The server body's own stance at the body's position (ADR 0012: "in every other seat it stays the body the server keeps there"), not a volume from the seat pose; riders are not yet drawn crouched on their seat for every vehicle. The rule is right in what it lets through and wrong in the exact volume: a glass rider is hit as a standing body where the body sits. The hit volume from the seat pose for every seat is #301 slice 4.

**Not changed, on purpose.** Shooting from seats (out of scope). Riders of one vehicle never hit each other. The gunship cannon, the flak shell and the killstreak aircraft routes keep their own rules (they stop on the hull volume and burst by blast; an armoured rider is not dropped from them, but those rounds do not reach covered riders by their direct hit either). A reinforced pane that stops a round also stops it for the hull: the vehicle takes the bullet's damage there as on any hull face.

**Consequences.**
- The flak truck's driver (armoured) can only be killed by a blast or by the truck exploding; its gunner is exposed as before.
- A bullet that now stops at a reinforced pane damages the pane once (the shot-glass step), not twice.
- No vehicle has a reinforced pane yet (the data keeps today's glass); the kind is there for the models of the #325 swaps to use.

# A seat can leave its rider uncovered by the hull, and the amphibious transport's all do

Decided for the amphibious transport's rework. **Extends** the exposed gunner of ADR 0005's flak truck (and the utility helicopter's door gunner): there exposure was one flag on the vehicle (`exposed_gunner`) that looked up the single gunner seat and handed one target to `GunnerCover`. **Changes** the rule stated with it, that "the driver and a passenger stay behind the hull, as in every other vehicle".

**What changes.** Exposure is a property of a seat (`vehicles.json` seat `"exposed": true`). A rider in such a seat is a target of their own: a bullet, a pellet or a rocket that reaches their body hits them and not the hull, wherever the hull's box was along the way, and the hull takes nothing from that shot; a shot that misses them hits the hull as before. All three seats of the amphibious transport are exposed, the driver's included. `GunnerCover` takes the riders of the vehicle the shot stopped on (`ExposedRiders`), one or several, and the nearest body on the ray wins.

**The flak truck and the helicopter are untouched.** They keep `exposed_gunner`, which now means "the gunner seat is exposed"; one gunner converts to the riders the rule takes, and `GunnerCoverTests` passes unchanged. The weapons that never reached their gunner through the hull (the jeep's roof gun, the killstreak aircraft guns, a flak shell on its way to the ground) still do not: those weapons reach only riders whose own seat says it is exposed. That difference is deliberate for now and is the one to revisit if the two kinds of exposure should become one.

**What a rider is to a bullet.** In an exposed seat the hit volume is the figure every client draws: as tall as a crouch seated, standing in the ring (`standing`), and on the seat where the vehicle is now. In every other seat it stays the body the server keeps there, as before.

**Riders of one vehicle never hit each other.** A shot fired from aboard (an armed seat's weapon, the ring gun) has no target aboard its own vehicle and passes its own hull. That covers a rocket's blast too (ADR 0011).

**Blast.** Nothing changed, and nothing needed to: a rocket's splash and another vehicle's explosion already reach every live body in range by distance, through walls and hulls alike, so they already hurt the riders of every vehicle. That is "unprotected" for the transport and is left as it is for the closed ones. When the vehicle itself blows up, everyone aboard dies with it, as in every vehicle.

**Considered.** A vehicle-level flag `exposed_riders` (it would have been a second special case beside `exposed_gunner`, and the next half-open vehicle would have needed a third). No hit box for the transport at all (the hull would stop nothing and could not be destroyed by fire). Raising the hit box to cover the riders (they could not be shot, which is the opposite of the request).

**Consequences.**
- A rider who dies frees their seat, as before. If it was the driver, the vehicle has nobody at the wheel and coasts to a stop (ADR 0010's hands-off step).
- The hit box is the hull only, 1.2 m high: riders stand out of it, so most shots at them never touch it, and the rule above is for the ones that come in under the top of the sides.
- Killstreak turrets and helpers aim along a line of sight that the hull's box still blocks; they engage a rider they can see over the sides. Not changed here.

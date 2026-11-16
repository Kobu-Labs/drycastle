# One WorldQuery and a mover by obstacle rule table; two shapes per entity

Issues #281, #280. Bigfoot is out of scope: its rules stay as they are and are not in the table.

**Why.** Collision was eight separate mechanisms (physics layers, kinematic bodies, the vehicle solid proxy, analytic hit volumes, server shot rays, ground rays, and point movers with their own `I*World` idea of "walkable"), each with its own rules. The differences were the bugs: animals and citizens walked through walls, trunks and vehicles, four kinds of shot disagreed on what stops them, splash ignored walls, run-over was opt-in per entity, and every World ray had to exclude player bodies by hand.

**1. One query service.** The server answers every "what is there" question through `WorldQuery`: `Ground` (what is under a point), `Blocked` (may this mover go from a to b), `Shot` (what a shot meets first), `Overlaps` (what a volume touches). No call site casts its own ray with its own mask. Players are on their own physics layer, so a world ray never needs a list of player bodies to exclude.

**2. One rule table.** `MoverKind x ObstacleClass -> Collide | Pass | RunOver`, in ShooterCore, default Collide. An exemption is a row in the table, never a mask at a call site.

| Mover | Collides with | Passes | Runs over |
|---|---|---|---|
| Player, bot | terrain, building, fence, placed solid, trunk, closed door, vehicle, parked raid van, wildlife, pet, demon | crown, bush, other player, citizen, gang, worker | |
| Ground vehicle | terrain, building, fence, placed solid, trunk, closed door, other vehicle, demon | crown, bush | player, target dummy, wildlife, pet, citizen, gang, worker |
| Aircraft | as ground vehicle, plus crown and bush | | as ground vehicle, when low |
| Raid van | follows its route; shoves vehicles; blocks a player when parked | building, trunk, door | player (moving), gang, every other NPC |
| Wildlife, pet | terrain, deep water, building, fence, placed solid, trunk, closed door, parked vehicle, player, pet, wildlife of another herd | crown, bush, same herd, citizen, gang, worker | |
| Citizen, gang, worker, bot route | terrain, building, fence, placed solid, trunk, door closed now, parked vehicle (local detour: forward ray, sidestep, no re-bake) | crown, bush, player, other NPC, wildlife, pet | |
| Shot (bullet, rocket, gunship, turret sight, fireball) | terrain, building, fence, placed solid, trunk, crown, bush, closed door, vehicle (analytic hull) | | hits player and NPC |
| Splash | any wall (one line-of-sight ray per target) | | player, NPC; x0.5 through a vehicle hull for a covered seat, full for an exposed seat |
| Drop (item, corpse, care package) | terrain, building, trunk, door (lands on it) | crown, bush, vehicle (lands under it), player, NPC | |
| Casing (#307, client only) | terrain, building, trunk, door, vehicle roof from outside | crown, bush, player, NPC; water (sinks to the bed, no clink) | |

Where a player and an NPC or animal block each other, the NPC or animal stops or steers; a player is never pushed. A moving vehicle runs a navmesh walker over; only a parked one is detoured around.

**3. Two shapes per entity.** **Hit shape**: what a weapon is tested against (bullet, blast, knife). **Body shape**: what contact is tested against (movement, blocking, run-over).

| Entity | Hit shape | Body shape |
|---|---|---|
| Player | capsule r 0.35 m + head sphere r 0.25 m | the same capsule |
| Citizen, gang, worker, demon | follows the player capsule | player capsule r 0.35 m |
| Wildlife, pet | baked outline for every species; blast and knife measure to the outline's bounding box | a box per species |
| Utility helicopter | 3 boxes (cabin, tail boom, fin) that tilt with pitch and roll | the same 3 boxes |
| Other vehicles | one upright box | the same box |

Run-over tests the victim's Body footprint scaled to 95%.

**4. Cost.** Measured 2026-11-16 (village map, 16 players, 226 animals): two rays on every animal step cost +1.6 to 2.0 ms a tick, every animal every tick +5.4 to 7.0 ms. The animal rows may cost at most 1 ms a tick: query objects are reused, an animal farther than `WorldInterest.LeaveM` from every player is not simulated, and checks are throttled.

**5. As built, where it differs from the tables above.**
- Only a standing animal (idle, eating, sitting, barking) blocks a player. A walking one is at a different place on the server and on a client by about a round trip, so it does not block; it stops or steers for the player itself.
- Animals have no physics body: animal against animal and player is an analytic Body box test. The demon has a static capsule on its own physics layer; players, ground vehicles, aircraft and animals stop against it. The raid van passes the demon.
- Navmesh walkers meet trunks, closed doors and parked vehicles at walk time (`WalkGuard`: one forward ray per 3 m, sidestep, or wait 1 s), not in the navmesh: trunks are three quarters of the polygons. Doors are baked open. Workers sit and need no guard.
- An animal's Body box is its `hit_box` width and length, centred `body_ahead` ahead of its position.
- A fence and a placed solid catch a drop (they share the world layer). Players, animals and walkers pass a target dummy.
- Splash is one rule for every explosion (rocket, missile, gunship burst, vehicle).
- The Bigfoot and the raid van keep the 0.56 m Hit radius (`PlayerHitBox.WideRadius`). Bigfoot's rays still see player bodies.
- Casings (#307, client only): the floor ray's mask is `CollisionRules.BodyMask(MoverKind.Casing)` (world and vehicle solid), and it returns a surface (height, normal, ground type, vehicle?). A casing bounces off the normal, slides down a face steeper than about 37 degrees, and under water (the client's `Water`, not a physics body) sinks slowly to the bed, never bounces and never clinks.

**Rejected.** A Godot-free static obstacle index in ShooterCore: it would let the rules run in unit tests, but it duplicates the map's collision geometry and must be kept in sync with it. The rule table is Godot-free; the geometry stays in the physics world.

**Consequences.** A player's Hit capsule narrows from 0.56 m to 0.35 m, so near misses that used to hit now miss. Players no longer block each other on the server (the client never predicted it). The gunship no longer shoots through crowns. Casings inside a vehicle stay in #316.

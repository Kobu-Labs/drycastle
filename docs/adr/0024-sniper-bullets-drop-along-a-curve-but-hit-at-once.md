# Sniper bullets drop along a curve, but still hit at once

Decided for the hunting design (bullet drop, zero dial, mil-dots, binoculars, hit diagram). Builds on ADR 0013 (shots resolve against rewound poses), which it keeps whole.

**What.** A weapon with `muzzle_velocity` (the sniper rifles) is resolved along a curve: the bullet falls under gravity as if it flew at that velocity, and the sight is set to a zero distance at which it crosses the line of sight (`BulletDrop`). The server walks the curve in straight 25 m pieces, each tested as a hitscan ray was: walls, glass, vehicles and the rewound targets. The hit is applied in the same tick the shot was fired. The player dials the zero with the mouse wheel while scoped, in 10 m steps (event 24, client to server); the server keeps one zero per weapon and resets it on death.

**Why no travel time.** A bullet that takes time to arrive is a projectile: the target moves while it flies, so the shooter leads, and the rewind of ADR 0013 no longer says where the target "was on the shooter's screen" at the moment of the hit. We would need a second lag model for one weapon category. Drop without travel time gives what the design wants (range must be known, the spotter with binoculars is worth having) and leaves lag compensation, the hit marker and the tracer as they are.

**The rise is along the world's up, whatever the pitch.** So the distance to the target is the zero to dial, uphill and downhill alike, and the binoculars can show the plain distance. Real ballistics would want the horizontal distance on a slope; the error we accept is nil by construction.

**Bots.** A bot's shots do not drop (it would otherwise need to dial). It sees the present and shoots straight, as before.

**Animals are hit by their outline.** The hit diagram needs the place the bullet entered the body, and a head shot must land on the head that is drawn. A plain body box is two thirds air (a standing deer covers 34 % of its box's side) and knows no pose (a grazing deer's head is 0.8 m lower). So a bullet tests a wild animal against a hit shape: its outline in the pose the client draws, in 5 cm cells with the half width per cell, baked from the model and its clips (`animal_shapes.json`). Standing, walking and running are one shape each (all the clip covers: the server does not know the stride a client shows); eating has a shape every 0.2 s, picked by the time in the state less the shooter's delay, since that clip moves the head slowly and starts when the state does. A species without a shape falls back to its body box (`hit_box`, `hit_box_ahead`). Blasts, knives and vehicles keep the capsule and the footprint they had. Pets keep the capsule. The animal's position is still the present one (not rewound): a running animal is ahead of where the shooter saw it.

**The binoculars measure on the client.** The distance is a ray against what the client draws; the server is not asked and nothing travels. In a closed group (no public servers) a client that lies about a distance only fools itself.

**Considered.** True projectiles for sniper rounds (above). A drop table printed in the scope and no dial (the spotter's number then needs arithmetic). A server-measured range (a message per reading for no gain). Mil-dots only, no dial (not exact enough: the owner wants the readout to give a near-certain hit).

**Consequences.**
- Protocol 52: events 24 (`SetZero`) and 25 (`HuntShot`). Protocol 53: events 26 and 27, whether a player's binoculars are up, for the others to draw; the distance still never travels.
- A sniper shot costs up to range / 25 ray tests instead of one (at most 32 for the M82), only for shots that hit nothing early.
- The client's own predicted tracer is still straight; the server's shot event ends where the bullet landed.

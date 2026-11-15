# World interest is opt-in per type, with dormant records and keyframes until acknowledged

Builds on ADR 0002 (the snapshot layer lists "optional radius interest management"; this is it, for the world channel). Wire protocol version 51.

**Why.** Measured with 16 players on the full village map: a client received about 38 kB/s, 85% of it the world channel, from about 226 animals (85 change a tick) and 15 flocks (all change every tick). Only about 15% of the changed animals and 20% of the flocks are within 800 m of a given player. The players and vehicles channel is 5 kB/s and is not touched.

**1. Opt-in per type.** `WorldEntityDef.Interest` (default false) is on for `animal`, `flock` and `poop` only. Every other type (turrets, aircraft, missiles, vans, gang members, corpses, citizens, Bigfoot, demon, loot, ground items, care packages) is sent at every range exactly as before. Spawn and despawn stay global reliable events: a filtered entity exists on every client, only its state is filtered.

**2. The band.** Horizontal (x, z) distance from the client's player body (also when dead or spectating) to the entity: it enters at 800 m and leaves at 850 m (`WorldInterest.EnterM`, `LeaveM`), so an entity at the edge does not flap. The encoder takes the viewer position as a parameter of `Encode`.

**3. Per-client relevance in the encoder.** The history of world states is shared by all clients and holds every entity, so a filtered entity cannot be left out (it would read as a removal) and a delta against the shared baseline is wrong for a client whose copy is stale. Each encoder keeps a small table of the filtered entities (`WorldRelevance`, spec `WorldInterest.Relevance`):
- **Keying**: in range and the client may not have it: a keyframe every tick until the client acknowledges a tick at or after the first keyframe's tick. Then **Synced**: deltas against the shared baseline as before.
- **Leaving**: out of range: a record with the new flag `FDormant` (64, no fields) every tick until the client acknowledges a tick at or after the first one. Then **Silent**: nothing is sent. Never `FRemove`: the entity is alive; only a real despawn removes it, and it drops the table entry.
- Entering from Leaving or Silent is Keying again.
- A client with no usable baseline (join, reconnect, baseline gone from the history) gets keyframes for what is in range and the dormant record for what is not. A never-seen entity out of range with a baseline is simply not sent.

An acknowledgement means the client committed a tick, and a tick commits only with all its packets, so a lost packet is repaired by the next tick's record, the same loss rule as the rest of the channel.

**4. Client.** `WorldReplica` keeps the dormant flag per entity and in its committed state. An entity of an opt-in type is dormant from its spawn until a keyframe shows it. A dormant entity keeps existing; its node is hidden and not animated, its flock is not flown, and on a keyframe its `Interpolator` is reset so it does not slide across the map.

**Consequences.** The world channel carries roughly the nearby 15 to 20% of animals and flocks, so a client's total is expected to fall from about 38 kB/s to about 11 kB/s (an estimate, to be measured). A player who views the world from elsewhere than their body (the gunship) sees animals and flocks only around the body. A join costs a few ticks of dormant records for the filtered entities outside the band. Protocol version 51.

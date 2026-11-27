# A Trap house is dressed by the server for its Job and stripped after

Decided with the owner on 2026-11-27 (#437, `docs/cartel.md`). ADR 0040 left furniture to an ADR of its own; the Trap house Heist needs things inside a hollow house before any furniture exists.

**1. Job state, not map.** The server picks the house when the Job starts: any hollow house with road access for the Cartel van, used by no other Job and with no player inside. It places the Rival crew's props by room type from the house's floor plan, and takes them away when the Job ends. The map artifact and `interiors.json` do not change, and a joiner is told the dressing of every running Trap house. Baking fixed trap houses into the map was rejected: a few known houses would be learnt by heart, where the Job can use any of the hollow houses.

**2. Some props are cover.** Tables and crates are solid (a box collider on the World layer: they stop players and bullets); the rest are looks only.

**3. Defenders hold posts.** No NPC goes inside a house (the navigation mesh stops at the doorways, ADR 0040 §6), so each defender stands at a spot in a room, turns and shoots, and never walks. Defenders that walk the floor plan's room graph are later work.

**Consequences.** This is not furniture: the everyday furniture of all houses is still its own decision, and may reuse the placement by room type. A house rammed open by a vehicle mid-Job (house destruction) stays a Trap house.

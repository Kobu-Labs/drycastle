# The raid van is a scripted entity, not a vehicle

For the Thieves event (`docs/events/README.md`, "Thieves storming the village") a van brings each Raid's crew into the village and takes the Loot away. It is not a `ServerCar`.

**1. Why not a vehicle.** A vehicle's seats hold player ids, its driver's client predicts it (ADR 0014), it can be entered, pushed, thrown by the Bigfoot, and it returns to its origin when abandoned. The van needs none of that and would have to switch each of them off. Nothing in the game drives a vehicle by itself, so `DriveModel` with a steering controller would be new work whose only result is a van that can get stuck.

**2. What it is.** A world entity of its own wire type with a brain: it follows a polyline at a set speed, its height sampled from the ground. The polyline is a shortest path over a graph built from `roads.json` (nodes and edges are already there; nothing read them as a graph before), from the point where a road crosses the map edge to the stop the server picked, and back out.

**3. It cannot be stopped by standing in its way.** It does not brake for players or vehicles: it pushes through (a player is rammed as by a car, a vehicle is shoved aside) and honks. A van that waits can be body-blocked for ever; the event has no other clock on the way in.

**4. It can be destroyed.** A Hit count (300; explosives weighted), also while driving in. Destroyed: its Raid is over, the Loot loaded in it goes back to its place by itself, its crew flees on foot. This is the players' second way to save Loot, and the reason it is a hit target and solid.

**5. Nobody is seen inside.** Crew appear at the side door when it opens and vanish into it when boarding. There is no seat, no sit pose and no exit animation; only the door moves.

**6. Consequences.** The road graph and the map-edge entry points become something the server has at runtime (the SWAT bus event can use both). The van is not in `vehicles.json`, not in the vehicle snapshot records and not rewound as a vehicle (ADR 0013): its hits use its own pose history, as the Bigfoot's.

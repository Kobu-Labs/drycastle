# The bus garage is enterable and players work its doors

Extends ADR 0009, ADR 0019 and ADR 0020: the bus garage (parcel P-0666, `w378900337` + `w1226408502`) is the fourth building with an interior, and the first whose doors a player opens.

**1. Only the bay is hollow.** The deep part (`w378900337`, 14.8 m) is one room with a doorway in its front wall; the side part stays a solid block with its window drawn on. Modelled from the owner's two photos of 2026-11-16 (`buildings/landmarks/garage.py`): white render, two lean-to roofs of rusty standing-seam sheet falling away from the shared wall.

**2. Sized for the bus, on the real footprint.** The footprint is unchanged (the bay is 14.2 x 5.1 m inside, the bus 12.06 x 2.56 m). The doorway (3.6 x 3.5 m) and the walls (4.0 to 5.0 m under the roof) are higher than the real building's, because the bus is 3.1 m high.

**3. `garage.json` is the contract.** Next to `urad.json` in the map: `doorway` (as the church's), `bay` (the room; not read by the game yet), `parking` (the vehicle id, place and heading). A map without it: no doors, and the bus is dealt at random as before.

**4. The bus stands in the bay.** The server parks one vehicle of the `parking` type there as a fixed spot (it returns there when destroyed) and deals one fewer of that type at random.

**5. Players work the doors.** Unlike the church's and the Urad's, which follow an event: a living player on foot within 1.2 m of the plane of the doors (either side) toggles them with the interact key; there the key belongs to the doors, not to a vehicle seat in reach. Server-owned (`GarageDoors`, machine `Garage.Door`), one reliable event (245), closed on a new server. Same box as the church door (`DoorNode`); the leaves are sheet metal and swing outward, since the bus stands a metre behind them.

**6. They never close on a vehicle.** A vehicle whose footprint is in the doorway keeps them open (the player is told); a player standing in the doorway is moved to the nearer side, as at the church.

**7. Not a shelter.** The bay is not registered for splash protection (ADR 0009 §5).

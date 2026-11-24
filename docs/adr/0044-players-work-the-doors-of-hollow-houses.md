# Players work the doors of hollow houses

Extends ADR 0027 and 0040. Asked by the owner on 2026-11-25: opening of garage and regular doors, without changing door sizes or making more houses hollow.

**1. `interiors.json` lists the doors.** Per house, `doors`: id, `kind` (`hinged` or `garage`), `pos` (middle of the bottom of the opening, mid-wall), `yaw` (bearing out), `width`, `height`, `colour`. A `garage_door` of a hollow house is now a hole through the wall; where it starts below the floor, the floor's edge closes the bottom of the hole.

**2. One owner for all of them.** `HouseDoors` (machine `House.Door`, one per door), shut on a new server. A door is named by its index in the file. Event 239 `HouseDoor` (u16 index, u8 open), protocol 86; a joiner is sent the open ones.

**3. As the bus garage's doors.** A living player on foot within 1.2 m of the door's plane, either side, toggles the nearest with the interact key, before a vehicle seat. Shut, it is solid (`DoorNode`'s box on the World layer: players, bullets, vehicles, blasts). It does not close on a vehicle; a player in the doorway is moved aside. A front door swings in, a garage door tips up.

Consequences:

- Only the doors to the outside. Doorways between rooms have no leaf.
- Bots do not open doors.
- No damage or breaching.
- The garage floor is the house's floor (ADR 0043): where it is above the ground a car meets a ledge.

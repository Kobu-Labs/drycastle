# The radar unit sweeps on the server and tells only its operator

Decided for #371 (an operator driven UAV-like vehicle). Server, core and data first; the radar screen, the HUD and the model are the client's, later.

**What it is.** `radar_unit`, a towable trailer (ADR 0036: a trailer has no drive block) with one seat, role `radar_operator`. It is built like the rocket cart (`trailer` block, `spawn_count` 0, `returns` false) and adds a `radar` block. One stands at a fixed spot at the military base next to the helicopter; `/spawn radar_unit` works as for any type.

**The seat is the tow machine's.** Nobody can enter it unless the trailer is `Hitched` (`Trailer.Tow`): `ServerCar.Boardable` reads that state, there is no second flag to keep in step. One operator at a time. When it stops being hitched (the driver's unhitch, the tractor destroyed or reset, the Bigfoot) the operator is put out beside it, the tick after. Destroyed, it kills its operator as any vehicle does. A trailer's only seat is this one: the contract allows `seats: []` or exactly the `radar_operator` seat for a type with a `radar` block.

**The sweep is server-owned and reveals positions.** The machine `RadarUnit.Sweep` (Off, Sweeping; `RadarSweep` in ShooterCore) runs while the unit is hitched: an arm turns about the unit once per `radar.revolution_seconds` (6), whole map, no range. A contact is stamped with its position at the moment the arm passes its bearing and keeps that position until the next pass; a stamp older than a revolution is dropped. The position a client sees is therefore never fresher than the sweep, and a hidden or dead contact leaves by ageing out, not by an event. The client fades a contact over its age (a revolution).

**Who is a contact: the UAV's filter, reused.** The living players and target dummies of `UavContacts`, minus the operator, minus anyone whose `uav_visibility` is 0 (Ghost). A player on foot is a `Player` contact; a player in a vehicle is not, the vehicle is a `Vehicle` contact (an empty vehicle is none), and it is hidden when an enemy aboard has Ghost. In team deathmatch the operator's teammates are not swept: they are sent live, Ghost or not, as friendlies in the same message (the minimap's friend-marker rule), a vehicle with a teammate aboard as a friendly vehicle. Free-for-all has no friendlies.

**Contacts go to the operator only.** The message `RadarSweep` (event 91) is sent to the one seated player, about ten times a second while the dish turns. No other client is told a position, so no new wire record rides the snapshot. With nobody in the seat nothing is stamped; the next operator starts with a blank screen.

**Protocol 71.** New event 91; a trailer with no `loaded_hp` sends its loaded bit clear (so no client takes it for a rocket cart); the radar unit is vehicle kind 13 (the kind is one nibble of the vehicle record: two of 16 slots are left).

**Not decided here.** The screen (a rotating arm over the map, fading blips, friend markers), the dish's spin on the model, the model itself (the grey box stands in), the prompt for entering and the sound.

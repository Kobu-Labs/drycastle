# Riders are fixed to their seats and look round with the head

Decided with #310 (a helicopter pilot's figure spun inside the cabin as the hull yawed). **Replaces** two things: the client's riders of non-`drive` vehicles being free world objects (snapshot position, yaw only, upright), and `SeatRiders` guessing a rider's seat from where their body is (`MatchSeat`, `CarryRiders`). **Extends** ADR 0011 and ADR 0012 (armed and exposed seats), which are unchanged.

**Every seat of every vehicle is fixed.** A rider is drawn on their seat's offset (`vehicles.json` `seats`, the vehicle's own frame) of the hull as it is drawn this frame, and turns, tilts and bobs with it: car, bus, jeep, flak truck, amphibious transport, helicopter alike. A vehicle with no `seats` list has the one default driver seat (the same rule, at the vehicle's origin). Nobody is a free object near a vehicle.

**The server says who sits where.** Seat 0 is the vehicle record's driver as before; a new snapshot record, `EntityType.Seats` (id range `0x3000`, one per vehicle with more than one seat), carries the occupants of seats 1..3 in the otherwise unused yaw, pitch, health and weapon fields, with the position fixed at the origin so it is only re-sent when someone changes seat. Clients draw each rider on the seat they are told, not the nearest one to their body. This also lets any client ask "is an enemy on any seat of this vehicle" (#313) by ids.

**What a rider moves is the head.** The view (yaw and pitch, the snapshot's own fields for a player) is the head's look, and the figure is posed from it against the hull (`RiderPose`): the body stays on the seat; the head turns up to 70 degrees and tips; a look past that twists the upper body over the hips, so the whole look reaches straight behind (the cockpit view's own reach). Pitch is held to -0.9..0.9 rad.
- **Only a first-person view looks.** A new input bit, `MoveButtons.HeadLook`, says the input's yaw and pitch are the head's: a passenger's own eyes, the driver's cockpit view. In the chase view the yaw is the camera's orbit and the head faces ahead.
- **A helicopter's pilot** never turns the head with the yaw (it is the heading they steer to, so the head faces where the nose goes); their pitch looks up and down.
- **The server stores the look against the hull** (`Client.HeadOff`, `HeadPitch`) and publishes `Body.Yaw` = the hull's yaw now + the turn every tick, so the figure keeps its look as the vehicle turns and a rider's body yaw, corpse and exit yaw are the hull's, never the yaw they got in with.
- **A rider who works a gun** (a `gunner` seat, an armed seat) turns the whole figure to the aim, feet on the seat, with the weapon pitched to it, as before; the door gun's mount and the flak turret are posed from their own records.

**Hit volume and lag compensation are unchanged** (riders resolve against the vehicle's rewound pose, ADR 0013); the rider's stance is still the seat's.

**Considered.** A dedicated head-look field in the snapshot (more bytes for every player to show something only seated ones have; the view already travels). Walking about inside a vehicle (server-side interior movement, collision and a hit volume that moves: a different feature; the gunners' freedom is turning on their seat). Sending the look in the world frame only (the figure would lag its hull during a turn): the look is taken against the hull's yaw of the same snapshot.

**Consequences.**
- A look with the chase view or the pilot's yaw is never shown; it is a first-person thing, as requested.
- The seat tilts with the hull, so a helicopter's pilot banks with it (22 / 26 degrees).
- A vehicle whose model has no cockpit rig still shows its riders' heads turn for passengers.

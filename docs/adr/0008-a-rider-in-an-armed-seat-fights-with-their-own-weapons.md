# A rider in an armed seat fights with their own weapons

Decided for the amphibious transport's rework. **Changes** the standing rule that a seated player has no weapon of their own: in `NetServer.SimulateClient` a client with a vehicle got "no walking or weapon timers", the client hid the weapon in the view while in a vehicle, and `WaterCraftTests` said "passengers never shoot in v2". The only shooting from a vehicle was the vehicle's own gun, from a gunner seat.

**What changes.** A seat can be armed (`vehicles.json` seat `"armed": true`, passenger seats only). Its rider keeps their loadout and uses it from the seat exactly as on foot: fire, reload, aim, the weapon change, recoil, breath, the weapon in the first-person view, and kills that are ordinary kills with that weapon. It is a property of the seat, so the bus's passengers, the boat's and every other seat stay as they were. The amphibious transport's front right seat is the first.

**The questions it raised, and the answers.**
- *Changing weapon.* The number keys are the seat switch while seated, in every seat of every vehicle, and stay that. The mouse wheel cycles the hotbar, as it does on foot; it is the one weapon change that was never on the number keys.
- *How far round.* The seat has an arc in the vehicle's frame (`aim`: the transport's runs from 60 degrees left of the nose round the right side to 150 degrees, 40 down, 70 up). The view is held inside it, so the rider cannot aim where they cannot shoot: not across the driver beside them, not back through the gunner. A full circle would have made the ring gun's seat pointless.
- *Spread.* The vehicle's speed widens it as the rider's own speed does on foot: none at a standstill, the weapon's whole movement spread at the seat's `spread_full_speed` (16 m/s), in proportion between. A seat is no crouch: no stance bonus, for spread or recoil. Otherwise the transport would be the steadiest gun platform in the game at 80 km/h.
- *The knife.* It works as on foot, from the seat, against anyone within its reach who is not aboard. It rarely matters; forbidding it would have been one more rule.
- *The shooter's own vehicle.* Its hull is not in its riders' way and its riders are not their targets (ADR 0009).

**Considered.** A second vehicle gun for that seat (it would not be the player's loadout, which is the point of an open car). A new key for the weapon change (a binding nobody has, for something the wheel already does). Letting every passenger of every vehicle shoot (the bus would become a bunker; closed hulls have no openings to shoot through).

**Consequences.**
- The server runs a seated player's weapon timers when, and only when, their seat is armed (`RiderRules.Weapons`), and resolves the shot from the seated eye (`NetServer.FireWeapon` with the vehicle). No wire format changed for it: the input already carried the view, the buttons and the wanted slot. The protocol version still had to move (22), because a server that answers a seated player's trigger is not the server an older client expects.
- The rider's view is what every client sees them aim with: the server takes it as the body's yaw and pitch.
- A rocket fired from the seat passes the shooter's own hull and spares their fellow riders, direct hit and blast, for as long as the shooter still rides that vehicle; the shooter takes their own blast as on foot, and so does the vehicle.

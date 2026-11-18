# Pole lamps break by server-owned state and flicker back

Decided for #337. Pole lamps are the luminaires of placed utility poles (`MapLoader.ObjectLights()`); the Street lamps of `lamps.json` stay unbreakable.

- **The server owns the state.** A round that passes within `hit_radius_m` (0.25 m, environment.json `lamp`) of a lamp's head breaks it. The check is the round's path (`LampGeometry.Hits`, the segment to where it stopped), beside `BaleShot`, not a collider: a lamp neither stops a round nor needs a physics body, and a lamp in front of a player a round then hits is still broken. The glass of ADR 0007 is the model for the wire (reliable events, a join catch-up of what is not whole, a new round restores) but not for the check: a pane is damaged where a round stops; a lamp is a point the round flies by.
- **Identity is the index in `ObjectLights()`**, ordered by object id and then the asset's light points. Server and client list the same placed objects from the same map build (the join checks the hash), so the index is enough on the wire (u16). A map edited live while a match runs is not supported.
- **One breaking, one flicker, no hit points.** One round breaks a lamp. Machine `Lamp.Light` (docs/state-machines.md): Lit, Broken (`respawn_seconds`, 120), Flickering (`flicker_seconds`, 3), Lit; a shot breaks a flickering lamp again. Only a lamp that is not lit has a machine.
- **The flicker is the client's.** The server says Flickering and later Lit; a client draws the flicker from the moment it heard Flickering (`LampFlicker.On`, deterministic in lamp and age) and goes steady by itself, so nothing is sent per flicker. A joiner is told every lamp that is not lit and flickers a fresh 3 s for one that was flickering.
- **Broken is dark at once and costs nothing.** No lens (the lens plate's instance shrinks to nothing: the plates share one material) and no beam (the light pool never picks a broken lamp, `LampPool.Refresh`).
- **Wire.** Events 250 `LampState` (u16 lamp, u8 state), 251 `LampSync` (every lamp not listed is lit), protocol 71.
- **Not done.** Blasts do not break lamps; no spark, smoke or sound; the lamps of `lamps.json` do not break.

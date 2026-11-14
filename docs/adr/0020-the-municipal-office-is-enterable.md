# The municipal office is enterable

Extends ADR 0009 and ADR 0019: for the rozhlas detour (`docs/events/README.md`) the Obecny Urad (parcel P-0288) becomes the third building with an interior, after the bunkers and the church.

**1. Only what the detour needs is hollow.** The south-west bar (`w378900312`: the clerks' office and the mayor's room) and the entry at the south-west end of the middle bar (`w378900355`). The rest of the middle bar and the east wing stay solid blocks. Layout from the owner's editor sketch of 2026-11-14 (`office_interior.py` names the sketch lines and points).

**2. The middle bar's road wall is set back.** It stands 1.4 m inside the footprint edge: the strip in front is a covered walkway under the roof (the porch), and the entrance is in the set-back wall. The measured windows and door of that wall (`config/buildings/w378900355.yaml`) move back with it; the solid part is built by the usual detail generator on the reduced footprint.

**3. Code model, as the church.** `office.py` stays the landmark; `office_interior.py` builds the shell in map metres on the real footprints (walls with real door and window openings, floor slab 0.15 m with a sloped porch edge, no step), and the collision is the same shell with a slab for the roof. It refuses to build if a footprint changed. The windows of the hollow bar (two measured on the road wall, two sketched in the mayor's room) are see-through breakable panes (`windows.json`), like the church's.

**4. Furniture is baked, not placed.** Desks, conference table, chairs and the set on the dictation desk are plain low-poly boxes in the map mesh (desks collide, chairs do not). Unlike the church (ADR 0019 §5) there is no private asset pack involved, so the office is never empty and the server needs no prop boxes. Rejected: Synty office props (paid pack, no fallback).

**5. `urad.json` is the contract.** Next to `church.json` in the map: `doorway` (as the church's), `volume` (entry, office, mayor: what "inside the Urad" means for the live feed), `workers` (role, seat, yaw), `table` (the dictation desk, where the set is taken), `cctv` (reserved, issue #270). A map without it: no workers, no set, no door; the loudspeaker still runs.

**6. The door is open exactly while a detour is open.** Same box and look as the church door, server-owned, one reliable event. Shut at Idle, and unlike the church nobody is locked in: whoever is inside when it shuts is put outside the door, because the next broadcast is ten minutes away. The interior doors (entry to office, office to mayor) are plain openings.

**7. Not a shelter.** The rooms are not registered for splash protection (ADR 0009 §5, ADR 0019 §7): the building is open only during the detour, and fighting inside is the point of it.

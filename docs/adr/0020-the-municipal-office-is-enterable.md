# The municipal office is enterable

Extends ADR 0009 and ADR 0019: for the rozhlas detour (`docs/events/README.md`) the Obecny Urad (parcel P-0288) becomes the third building with an interior, after the bunkers and the church.

**1. Only what the detour needs is hollow.** The south-west bar (`w378900312`: the clerks' office and the mayor's room) and the entry at the south-west end of the middle bar (`w378900355`). The rest of the middle bar and the east wing stay solid blocks. Layout from the owner's editor sketch of 2026-11-14 (`buildings/landmarks/office_interior.py` names the sketch lines and points).

**2. The middle bar's road wall is set back.** It stands 1.4 m inside the footprint edge: the strip in front is a covered walkway under the roof (the porch), and the entrance is in the set-back wall. The measured windows and door of that wall (`config/buildings/w378900355.yaml`) move back with it; the solid part is built by the usual detail generator on the reduced footprint.

**3. Code model, as the church.** `buildings/landmarks/office.py` stays the landmark; `buildings/landmarks/office_interior.py` builds the shell in map metres on the real footprints (walls with real door and window openings, floor slab 0.15 m with a sloped porch edge, no step), and the collision is the same shell with a slab for the roof. It refuses to build if a footprint changed. The windows of the hollow bar (two measured on the road wall, two sketched in the mayor's room) are see-through breakable panes (`windows.json`), like the church's.

**4. Furniture is game-placed, data-driven props.** As the church (ADR 0019 §5): `props` of `urad.json` (model, pose, collision boxes), placed by the game (`UradProps`) as Synty SIMPLE Office models from the gitignored `assets-private/` (`synty/glb/office`, built by `tools/office_convert.gd`, scale baked in). The file carries everything, so adding or moving a prop is a pipeline edit (`office_interior.PROPS`), no game code. Unlike the church there is a fallback: without the pack the collision boxes are drawn, so desks never go missing (chairs and clutter have no box and do). Only the set and its microphone on the dictation desk are baked into the map mesh.

**5. `urad.json` is the contract.** Next to `church.json` in the map: `doorway` (as the church's), `volume` (entry, office, mayor: what "inside the Urad" means for the live feed), `workers` (role, seat, yaw), `table` (the dictation desk, where the set is taken), `cctv` (reserved, issue #270). A map without it: no workers, no set, no door; the loudspeaker still runs.

**6. The door is open exactly while a detour is open.** Same box and look as the church door, server-owned, one reliable event. Shut at Idle, and unlike the church nobody is locked in: whoever is inside when it shuts is put outside the door, because the next broadcast is ten minutes away. The interior doors (entry to office, office to mayor) are plain openings.

**7. Not a shelter.** The rooms are not registered for splash protection (ADR 0009 §5, ADR 0019 §7): the building is open only during the detour, and fighting inside is the point of it.

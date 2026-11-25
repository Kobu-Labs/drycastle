# A hollow house's floor plan is generated from its door, a programme and a seed

Step 2 of ADR 0040 (a house with a building detail can be hollow). Decided with the owner on 2026-11-24: plans are generated only; some houses are open, some have clearly separated rooms; the code knows hallways, rooms, partitions and doorways. `buildings/floorplan.py`.

A first version cut the house into rooms of about one size and joined neighbours, and the owner rejected it the same day: monotonous, unrealistic, "every neighbouring room has a door". The rules below replace it: the rooms' uses come first (ADR 0042), sizes differ as uses do, and the doorways are a tree.

**1. Nothing is stored.** The plan is a function of the footprint, the openings that go through and a seed (the building's id). The same house always gets the same plan; a changed detail gives a new one. No file holds a plan and no detail can override one, the style included (no `openness` field): if one house must differ, the generator's rules change, not the house's file.

**2. Openness picks the style.** One number 0..1 from the seed: *closed* (under 0.4: a corridor, a door to every room, living room and kitchen apart), *mixed* (to 0.75: a shorter hall, living room and kitchen joined by an archway) or *open* (one space to live and cook in, a vestibule at most, under 80 m² no hall at all).

**3. The hall starts at the front door.** A strip 1.2 to 1.5 m wide from the first door that goes through, inward (the stem). A door in a long wall of a house at least 6.5 m deep gets a corridor along the house at the stem's end (the spine); each end of the spine is at the end wall or stops a room short of it. A closed house too shallow for a spine may also get a corridor along the door's wall. What is too small or too narrow for a room beside the hall is hall; a stem that would take a whole narrow wing is dropped, and the door opens into a room.

**3a. Many plans, the fewest faults.** The owner rejected a long corridor beside a long living room (P-0373). A house is planned with each kind of hall (the long one, an entry only, none, the side corridor), up to 4 times each; each plan is scored (`_faults`: floor wasted on halls, no living room, kitchen or bathroom, a front door into the wrong room, rooms too large or thin, rooms without daylight, rooms walked through), some chance is added, and the best is kept. The search ends early at a nearly faultless plan. So openness is a leaning, not a promise: a closed house may end with an entry only.

**3b. Every room can be walked to.** A candidate with a room a body (radius 0.35 m) cannot reach from a door to the outside, or a partition ending in such a door, is ruled out; drawing goes on, up to 24 times each, until one passes. Room outlines lose anything under 6 mm wide, which would otherwise count as a shared wall and draw a partition across a doorway.

**4. The programme is cut into the floor.** The floor beside the hall gets the rooms of the programme (ADR 0042), the largest rooms in the largest pieces. A piece is cut by halving its list of rooms and cutting where the halves' sizes say, each half again; what is public ends up toward the front door. Every partition is parallel or square to the house's longest wall. A cut never ends within 0.15 m of a window's or a door's side, leaves no room below its type's least size or width or longer than 2.4 times its width, and is moved onto a corner of the piece within 0.4 m (walls line up). When nothing fits, the rules loosen once (a partition may meet a window at its post; 3.6 times), and then the piece gets one room less, a small one first, never the living room.

**5. Doorways are a tree.** Every room has one way in, by type: from the hall where it touches it; a kitchen from the living room; a dining room from the kitchen or the living room; a bedroom or a study from the hall or a room one lives in; a bathroom also from a bedroom, a closet also from the kitchen or a bedroom. Only the living room, kitchen and dining room open into each other (archway, or no wall in an open house); everything else is a door (0.9 by 2.0 m, no leaf). A closed house's kitchen may have a second door, to the hall: the only loop.

**6. No row of bedrooms behind one another.** Rooms that cannot be entered by these rules (a row of them at the end of a long house) get a corridor: a strip along one long side of their floor, the side with fewer windows, from a room one lives in or the hall, and that floor is cut again beside it. It is a second `hall` room. A room still not reached may lie behind one other room, itself entered from a hall or a room one lives in; the room walked through is then a study, not a bedroom. Deeper than that, or where nothing fits, the room is merged into a neighbour that is no closet. If the merging costs the house its bathroom, a closet or study of 3 to 9 m² becomes it.

**7. Partitions are 0.1 m slabs, floor to ceiling,** in the visual mesh and the collision mesh, with the piece of wall over a door or an archway.

**8. `interiors.json` carries the plan** per house: `openness`, `style`, `rooms` (id, kind `hall` or `room`, `type`, polygon, area, the windows and outside doors on its walls, links to other rooms) and `partitions`. Furniture (the next step) reads it.

Consequences:

- A house without a door that goes through has no hall; the living room is its root.
- A house whose walls are mostly window (the first test house, P-0110) gets fewer cuts across it and some partitions on window posts.
- Merged rooms can be large (a bedroom of 30 to 40 m²), and a house can still end without a bathroom when no room is of a size to become one.
- A building with more than 350 m² inside its walls is no house (P-0675, the culture house): no plan, one open space, until it has a programme of its own. A long house under that still gets too many rooms (P-0492).
- The search is Rust (`rust/detail/src/floorplan.rs`, on the same GEOS as shapely, with Python's random numbers), houses and the candidates of one house on all cores; `floorplan.py` only turns a house into its frame and back. The export needs the built engine.

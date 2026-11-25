# A house with a building detail can be hollow

Extends ADR 0009 (bunkers are the only buildings with an interior; then the church 0019, the office 0020, the garage 0027) and changes ADR 0007's "the wall behind a pane stays solid" for these houses. Decided with the owner on 2026-11-24: every house is to get a generated interior, in steps. This ADR is step 1, the shell. The floor plan is ADR 0041, the programme of room types ADR 0042; furniture follows in its own ADR.

**1. All houses, as their details come.** ADR 0009 ruled general interiors out because no floor plan exists for some 380 buildings. The plan is now generated, never drawn by hand, and a house needs a building detail first (its door and windows are the only facts an interior can start from). The owner expects every house to get a detail. Until the generator is trusted a detail opts in with `interior: true`; the flag is a rollout switch, not a design choice per house.

**2. The outer face stays, the wall grows inward.** Walls are 0.3 m thick inward of the footprint, so everything measured from photos keeps its place. The floor is a slab at the sill of the lowest door (the steps already climb to it; at least 0.05 m above the flattened ground). The ceiling is one storey up: the eave, an equal share of the wall for a house with more levels, never more than 3.0 m above the floor. What is above the ceiling stays closed.

**3. What goes through.** A window or a door whose sill is at the floor or above and whose head is under the ceiling is a hole through the whole wall: the window keeps its pane (now two-sided), the door is an open doorway with no leaf. So does a garage door whose head is at least 1.5 m above the floor, even from below it (ADR 0044). A recess and what is cut into a recess, a window above the ceiling or below the floor stay as they were (closed).

**4. Collision is the real shell,** as for the bunkers and the church (`interior.collision`): both faces of every wall with the holes, floor, ceiling, and a top where the plain massing ended, so the roof is walked on as before. Door steps are a ramp in the collision (the church's rule: no step higher than the player's step height). The loader needs no change.

**5. `interiors.json` is the contract.** Per house: `room` (polygon in the middle of the walls, `z0`, `z1`: a shelter volume in the bunkers' format), `floor_z`, `ceiling_z`, `wall_m`, `doorways` (middle of the opening on the floor at the outer face, outward normal, size). A map without it has no hollow houses.

**6. Decided, not built yet.** Every house is one shelter (ADR 0009 §5's rule, one volume per house). Bots do not go in (a 0.95 m door against the 0.5 m navigation radius, as the bunkers). Doors were open doorways in the first version; outside doors have leaves since ADR 0044. Upper floors and stairs are a second version. Generated only: no hand-drawn plan and no per-house override of the plan.

Consequences:

- A window opening has no collider (a glass pane never had one), so a player can jump in or out through a window, broken or not, where its sill is within a jump (1.1 m). Not decided whether an unbroken pane should stop a body.
- A building cut in parts (`roof_part`) cannot be hollow yet; the pipeline refuses.
- A recess deeper than the wall (a porch) would reach into the room; it stays closed and the room ignores it.

Since 2026-11-25 every house with a detail is hollow (landmark models aside), and the photo pipeline writes `interior: true` unless the building is cut in parts. A house no capture saw a door on was given one by hand (P-0030, P-0023, P-1135).

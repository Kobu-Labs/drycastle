# A garage door gets a garage, taken off the floor first

Step 4 of ADR 0040, before furnishing. Asked by the owner on 2026-11-25: a garage room that a car fits in. `pipeline/rust/detail/src/floorplan.rs` (`garage_behind`).

**1. One garage per `garage_door` opening,** cut off the floor before the house is planned: a rectangle running square in from the door, 2.7 to 3.8 m wide, about 5.5 m deep or the whole depth, grown over floor left too narrow beside it.

**2. What counts, in order:** a car (2.4 by 4.8 m) fits; a house is left; then fewer windows and less floor taken, partitions clear of openings.

**3. The house keeps its front door.** What is left is one piece, nowhere pinched to less than about 1.1 m, and no garage wall ends in a door. A front door in the garage's way gets a notch of floor around it, even if the car then fits worse. Under 14 m² left, the whole building is the garage.

**4. One door from the house** into the garage (hall, kitchen, closet or living room first); nothing is walked through it and it is never merged away.

**5. The garage door stays shut:** it is not a way through, and is listed as the room's `garage_doors` in `interiors.json`.

Consequences:

- The floor is the house's floor, which can be above the garage door's sill. A car that can drive in needs an open doorway and a floor at ground level (not built).
- A narrow door measured as a garage door is wrong data: write it as `door` (P-0556).

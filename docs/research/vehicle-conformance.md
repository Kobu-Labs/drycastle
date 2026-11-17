# Vehicle conformance audit (#325 step 7)

Each vehicle that was not swapped to a Synty standardized model, against part A of `docs/standards/car-design.md`. Source: the model scripts (`scripts/make_*_model.py`), `vehicles.json`, `SeatEye`, the GLB node trees (read headlessly: every .glb parsed, node names, parents, translations) and `VehicleContractTests`. P = pass, F = fail, n.a. = does not apply. "Before" is the state at the start of the conform work, "After" the state of this branch.

Not machine-checked anywhere (no test reads the model): "eye in the cabin and sees a pane" (only `HumveeTests`) and "interior reads as a room from every seat". Those cells say what the model script and the hand-set eye imply, and are marked `(m)` (a manual judgement).

## Before

| Contract item | golf_mk2 | bus | flak | amphibious | utility_helicopter | harvester | scud | rocket_cart | humvee |
|---|---|---|---|---|---|---|---|---|---|
| `hull` node, body under it | P | P | F (`chassis`) | P | F (`fuselage`) | F (none, parts under the root) | P | P | P |
| wheels siblings of `hull` | F (under hull) | F (under hull) | F (under chassis) | P | n.a. | F (no hull) | P | P | P |
| standard wheel names, `_steer` parents | P | P | P (6: fl/fr/ml/mr/rl/rr) | P | n.a. | F (`SM_Veh_Harvester_Wheel_*`, bespoke code) | P (l1..r4) | P (`wheel_l/r`) | P |
| scale 1 / offset 0 / yaw 0 | P | P | P | P | P | F (yaw 180) | P | P | P |
| interior: floor, dash, seat, steering control | P | P | F (solid block, driver invisible) | P (open: wheel, seats, floor) | P in the model, but hidden: the rig hides the whole `fuselage` and draws its own | F (prefab cab, rig hides wheel and glass and draws a wheel; no seat) | P | n.a. | P |
| steering control hidden for the rig (`steering_wheel_rim/_spokes`) | P | P (rig only) | F (hides `cab`) | n.a. | F (hides `fuselage`) | F (`SM_Veh_Harvester_SteeringW`) | P | n.a. | P |
| every seat has an eye in the cabin | P (hand-set) (m) | P (m): driver hand-set, passengers standing eye 2.03 / 2.51 under the 2.77 ceiling | F: eye (-0.5, 2.05, -1.25) is inside the solid cab block | P (open, head) | P (hand-set) (m) | P (hand-set) (m) | P | n.a. | P |
| every seat's eye sees a pane | P (m) | P (m) | F (the cab hides) | n.a. (no panes) | P (m) | F (no panes) | P (m) | n.a. | P (`HumveeTests`) |
| panes declared, with outlines, `glass_N` nodes | P (6) | P (18) | P (6, panels on a solid) | n.a. (open) | P (4) | F (none: one glass mesh) | P (8) | n.a. | P (6) |
| cover declared on every seat | P | P | P (`armoured`, gunner via `exposed_gunner`) | P | P | P (`exposed`) | P | n.a. | P |
| `steer_bars` for an open vehicle | n.a. | n.a. | n.a. | n.a. (a car wheel, in the hull) | n.a. | n.a. | n.a. | n.a. | n.a. |
| hit box fits (`VehicleContractTests`) | P | P | P | P | P | P | P | P | P |
| lamp materials/empties | P | P | P | P | P | n.a. | P | P (tail) | P |

## What the contract allow-list holds

At the start `default_model_transform`: harvester (yaw 180). Now empty.

## Humvee (audited, not changed)

Every item above passes. Gaps against the standard that are not failures of the contract (reported, not touched):
- The driver's eye is hand-set (`SeatEye.HandSetEyes[("humvee", 0)]`), not the generic `feet + eye_height` (the driver seat has no `eye_height`, so the generic rule would give the standing eye, y 2.25, over the 1.86 roof; `eye_height` 1.05 would match y 1.65 but not z, 0.09 m behind the hand-set one).
- Its bullet holes and damage smoke hang under the root, like every vehicle's (standard "Known gaps").
- Rig vs model: the cockpit rig draws its own turning wheel and hands (as designed); the model's `steering_wheel_rim/_spokes` are hidden. Fine.

## After (this branch)

| Contract item | golf_mk2 | bus | flak | amphibious | utility_helicopter | harvester | scud | rocket_cart | humvee |
|---|---|---|---|---|---|---|---|---|---|
| `hull` node, body under it | P | P | P (renamed from `chassis`) | P | P (renamed from `fuselage`) | P (standardized) | P | P | P |
| wheels siblings of `hull` | P (was under hull) | P (was under hull) | P (were under chassis) | P | n.a. | P | P | P | P |
| standard wheel names, `_steer` parents | P | P | P | P | n.a. | P (`wheel_fl/fr/rl/rr`, front pair steered) | P | P | P |
| scale 1 / offset 0 / yaw 0 | P | P | P | P | P | P (yaw 180 gone) | P | P | P |
| interior: floor, dash, seat, steering control | P | P | P (hollow cab: floor, dash with gauges, column, seat, wheel) | P | F: modelled, but the rig hides the whole `hull` mesh and draws its own | F: no modelled interior (floor, dash, seat); the shell is two-sided now, so the cab walls show from inside | P | n.a. | P |
| steering control nodes hidden for the rig | P | P (rig only) | P (`steering_wheel_rim/_spokes`) | n.a. | F (no steering node, the rig hides `hull`) | P (the standardizer's `steering_wheel_rim/_spokes`) | P | n.a. | P |
| every seat has an eye in the cabin | P | P (passengers `eye_height` 1.05 now) | P (ray-checked: 0.26 m under the ceiling, 0.33 m ahead of the seat back) | P | P (hand-set) (m) | P (hand-set) (m) | P | n.a. | P |
| every seat's eye sees a pane | P (m) | P (m) | P (ray-checked: both windscreen halves and the four side panes are unobstructed from the eye) | n.a. | P (m) | F (no panes) | P (m) | n.a. | P |
| panes declared, with outlines, `glass_N` nodes | P | P | P (in real openings now, not panels on a solid) | n.a. | P | F: none (one wrap-around glass mesh, kept as a node, not a pane list; exposed driver) | P | n.a. | P |
| cover declared on every seat | P | P | P | P | P | P | P | n.a. | P |
| hit box fits | P | P | P | P | P | P (box 2.56 x 4.34 x 3.87, was 2.5 x 4.3 x 3.8: the reel and unloader made the old box 0.05 too small) | P | P | P |

Remaining failures: **utility_helicopter** interior is hidden by the cockpit rig (the model has it; replacing the rig's interior with the model's is a separate piece of work, with the pilot's hand-built view to keep); **harvester** has no modelled interior and no panes. Everything else passes. Unchanged by design: the Humvee.

Also changed along the way: bullet holes and damage smoke now hang under the hull's rest frame for every vehicle (they were on the root); the flak's glass no longer keeps a dark fill behind a shattered pane (the opening is real).

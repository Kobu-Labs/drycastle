need to implement models mentioned in killstreaks.md
additionally these will be present:
- war jeep
- amphibious transport vehicel
- boat
- quad bike

all will be military styled

## As built: water craft (#132)

- **Boat** (`medium: boat`): water only. Afloat, its origin rides the body's surface (no gravity); the drive is its own top speed with a
  planing speed (below it, half thrust). On land it has no drive and coasts to a stop at `land_decel`. No run-over, no crash damage (running aground is not damage), no gun.
- **Amphibious transport** (`medium: amphibious`): land driving as the jeep, with road kill on land only. Afloat it runs at `water_top_speed` and `water_acceleration`, and does not run anyone over.
  Four seats (driver and three passengers, none shoot).
- Both have a `lights` block. Both are primitive-built (a box hull and a cabin, or the body box with wheels), so no model licence applies.
- Placement: `docs/pipeline.md` (water craft placement).

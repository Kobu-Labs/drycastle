# Car lamps get their halo from bloom, not glow quads

**Why.** The lamp halos were additive billboard quads on the vehicle root. Body geometry clipped them, from the side they read as floating spheres, and they did not follow the leaning body.

**1. Bloom.** The one `Environment` (`DayNightCycle`) has glow on: additive, HDR threshold 2 (ramp to 3), tight low mips only. Nothing lit by day reaches 2 (white surface in full sun about 1.8, sun disc 1.4, sky below 1). Lit lamp emission is 6 and up (`CarLights`), off and dim lamps stay below 2. Exposure and brightness come after the glow input, so day and night behave the same.

**2. Lamps ride the hull.** Spot lights and fallback lens spheres hang on the model's `hull` node (falling back to the vehicle root), converted from the vehicle frame, so they lean with the body.

**Rule.** Any other emission that should not bloom stays under 2 (garage tubes, turret muzzle). Effects at 2 and above bloom on purpose.

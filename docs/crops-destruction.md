# Crops crushed by vehicles (#328)

Cosmetic only, like the crops themselves: nothing collides, slows or blocks a bullet. Pure rules `ShooterCore/Events/CropCrush.cs` (tests `CropCrushTests`), server `NetServer.Harvester.cs` (`StepCrush`), client `NetClient.Harvester.cs` (`OnCropCrush`, `FlushCrush`), map `MapLoader.Harvest.cs`.

- **Who.** Every ground vehicle (land or amphibious on land, not aircraft, not afloat), also the harvester's body, on a crop field (`CropFields`: grain, corn, potato, rapeseed; not `crop_bare`). Moving faster than 0.6 m/s with somebody at the wheel, or 2 m/s rolling free.
- **Footprint.** The body's width (95 % of `half_width` each side; vehicles.json has no wheel positions), a sample every 2 m of travel (`SwathSampler`), stamped as a strip.
- **Mask.** Per field a bit per 1 m cell of the field's bounding box (`CrushGrid`): at most 262144 cells per field (bigger fields are not tracked), 120000 crushed cells in all (`CropCrushConfig`). Ground driven over before adds nothing. Cleared at round reset, not kept across rounds.
- **Wire.** Event 238 `CropCrush`, a few times a second: the new cells as ascending grid indices, varint deltas, messages up to 200 bytes. A joiner gets each field's whole mask as alternating run lengths (clear, crushed, ...), same size limit. Nothing per plant, per vehicle or per frame.
- **Look.** The client lays the plants in the new cells flat once per frame (tilted over, squashed, not removed; corn too), per 48 m MultiMesh cell like the harvester's cut. A plant the reel cut stays stubble (cut wins over crush). A dark flat patch per crushed cell on the ground (one MultiMesh, 2048 and doubling) so the mark reads beyond the plants' 110 m range. A new round stands every plant up again (`MapLoader.CropReset`).

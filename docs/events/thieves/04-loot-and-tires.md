# 04 Loot + tires

The stealing loop with the first target. Later targets (05 to 07) only add a target kind.

## Facts
- Cars are real `ServerCar` entities (`SpawnCars`, `NetServer.Vehicles.cs`), 12 of them; the server knows where they are.
- Vehicle record (`VehicleRecord.Make`, `VehicleRules.cs`): Health byte bits 0-1 damage stage, bit 7 lights, **bits 2-6 free**.
- `kenney-car-kit/sedan.glb` has nodes `wheel-front-left`, `wheel-front-right`, `wheel-back-left`, `wheel-back-right` (`CarFx.cs`). Other models name wheels differently or not at all.
- Hold-use with prompt and ring: the care package and the Rozhlas set.

## Do
- `LootKind` (Tire, Pet, Dish, Connector) and `StealTarget` (kind, position, work spot, id of the thing) behind one interface: `Free`, `Claim`, `Take`, `Return`. A registry lists free targets near a point.
- Thief states added to the member machine: Seek (pick the nearest free target of a random kind by weight, one Thief per target), Approach, Steal (`steal_seconds` per kind, a still target), Haul (to the van door, faster), Load (Loot counted in the van, Thief seeks again). Van called the crew back: drop nothing, board.
- `Loot : IWorldEntity` (new wire type) for lying Loot: kind, origin. Carried Loot is not an entity: the carrier's hp byte says what it carries, the client attaches the prop.
- Carrier killed: Loot lies where they fell. A Thief may pick lying Loot as a target. A player holds use `return_seconds` (1) on it: it goes back to its origin (`Return`). Interrupted by moving off or taking a hit. Cap `max_lying` (24): over it the oldest returns by itself.
- Van destroyed: everything loaded returns. Van driven off: gone until the round resets.
- After the event: lying Loot stays and stays returnable. Round reset: all restored.
- Crowding hook for 02: a player within `crowd_m` of a Thief in Steal or Haul gets aggro.
- Credit: a player who sent Loot back is credited for the event XP (10).
- Tire target: one per wheel of a spawned car at rest. `Take` sets the wheel bit (mask in Health bits 2-5), `Return` and `VehicleLifecycle.Reset` clear it. The car drives exactly as before. Work spot beside the wheel; kneel pose.
- Client: hide the wheel node by mask (sedan names; models without wheel nodes: nothing hidden, noted). Tire prop carried in both hands, lying tire, use prompt + ring per the UI standard (`ui-work` skill first).

## Accept
- Tests: target claim is exclusive; full loop Seek to Load; carrier death drops Loot at the spot; Thief re-takes lying Loot; player return restores the wheel bit; van destroyed returns all; cap; reset restores.
- Protocol bumped if the vehicle record meaning changed.

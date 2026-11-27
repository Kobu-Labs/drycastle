using System.Numerics;
using Drycastle.ShooterCore.Buildings;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a stash house's pallet stands: its base centre (Godot axes), how it is turned (a node's Y turn) and whether it is inside the house (else just outside its door), and the door it is by.</summary>
public sealed record StashSpot(Vector3 At, float YawRad, bool Inside, string Door);

/// <summary>
/// Where a stash house's Receiver stands (docs/cartel.md "Receivers"; GLOSSARY "Stash house"), pure: the Job's pallet goes inside the hollow house beside the door the van's road is nearest, if a floor spot there
/// is free (the pallet's footprint and the buyer's spot all inside the door's room with a margin from its walls, the door left clear), else just outside the door. A house that is not dressed has an empty floor, so
/// the free spot is a matter of the room's outline; the buyer stands beside the pallet (<see cref="ReceiverFigure.Pos"/> in its frame), the bags lie on it.
/// Plan axes: x east, y north (a point's Godot z is -y).
/// </summary>
public static class StashPlacement
{
    /// <summary>How far the pallet's near edge stands from the door's side posts, metres (a body passes between).</summary>
    const float DoorGapM = 0.35f;
    /// <summary>The pallet and the buyer keep this far from a room's walls.</summary>
    const float WallGapM = 0.25f;
    /// <summary>The depths into the room, from the middle of the wall, the pallet's centre is tried at (the wall is 0.3 thick).</summary>
    static readonly float[] Depths = { 1.0f, 1.4f, 1.8f };

    /// <summary>
    /// The pallet's spot: of the doors of the house's rooms, the one whose middle is nearest <paramref name="road"/> (Godot x, z) is tried first, then the others by distance; the first with a free spot inside gives
    /// it, else the nearest door's outside spot. Null for a house with no door. <paramref name="size"/> is the pallet's [x, y, z], <paramref name="man"/> the buyer's place in its frame [x, y, z].
    /// </summary>
    public static StashSpot? Plan(HouseFloorPlan house, Vector2 road, float[] size, float[] man, Func<Vector2, float> ground)
    {
        var doors = new List<(PlanDoor Door, PlanRoom Room)>();
        foreach (var room in house.Rooms)
            foreach (var id in room.Doors)
                if (house.Doors.FirstOrDefault(d => d.Id == id) is { Kind: not HouseDoorKind.Garage } d && doors.All(x => x.Door.Id != id)) doors.Add((d, room));
        if (doors.Count == 0) return null;
        float Dist(PlanDoor d) { var m = (d.A + d.B) / 2; return Vector2.Distance(new Vector2(m.X, -m.Y), road); }
        doors.Sort((a, b) => Dist(a.Door).CompareTo(Dist(b.Door)));
        foreach (var (door, room) in doors)
            if (Spot(house, door, room, size, man, inside: true, ground) is { } inside) return inside;
        return Spot(house, doors[0].Door, doors[0].Room, size, man, inside: false, ground);
    }

    static StashSpot? Spot(HouseFloorPlan house, PlanDoor door, PlanRoom room, float[] size, float[] man, bool inside, Func<Vector2, float> ground)
    {
        var mid = (door.A + door.B) / 2;
        var width = Vector2.Distance(door.A, door.B);
        var t = Vector2.Normalize(door.B - door.A);
        var n = new Vector2(-t.Y, t.X);
        if (PlanGeometry.Inside(house.Shell, mid + n * 0.6f)) n = -n; // n points out of the house
        float halfX = size[0] / 2, halfZ = size[2] / 2;
        foreach (var side in new[] { 1f, -1f })
        {
            var ax = t * side;                      // the pallet's own +x: the buyer stands that way, away from the door
            var az = new Vector2(ax.Y, -ax.X);      // its own +z on the plan (a node turned by the yaw below: +z is a quarter turn clockwise of +x on the plan, as Godot's z is -y)
            foreach (var depth in inside ? Depths : new[] { 1.3f })
            {
                var c = mid + (inside ? -n : n) * depth + ax * (width / 2 + DoorGapM + halfX);
                var buyer = c + ax * man[0] + az * man[2];
                var corners = new[] { c - ax * halfX - az * halfZ, c + ax * halfX - az * halfZ, c + ax * halfX + az * halfZ, c - ax * halfX + az * halfZ, buyer };
                var ok = inside
                    ? corners.All(p => PlanGeometry.Inside(room.Polygon, p) && PlanGeometry.ToOutline(room.Polygon, p) >= WallGapM)
                    : corners.All(p => !PlanGeometry.Inside(house.Shell, p) && PlanGeometry.ToOutline(house.Shell, p) >= WallGapM);
                if (!ok) continue;
                var g = new Vector2(c.X, -c.Y);
                var y = inside ? house.FloorZ : ground(g);
                return new StashSpot(new Vector3(g.X, y, g.Y), MathF.Atan2(ax.Y, ax.X), inside, door.Id);
            }
        }
        return null;
    }
}

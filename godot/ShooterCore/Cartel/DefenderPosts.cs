using System.Numerics;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>A placed prop's footprint on the floor plan (map local), with the radius round its centre that holds it, for the cheap test first.</summary>
public readonly record struct PropFootprint(Vector2 Centre, float Radius, Vector2[] Foot);

/// <summary>
/// The posted defenders' spots of a dressed Trap house (ADR 0047 §3; trap_house.json <c>posts</c>), pure: up to <c>per_room</c> in every room big enough that a way reaches, off its walls,
/// its doorways and the props, apart from each other, the farthest from the room's way in first and each facing it. Listed round-robin over the rooms, so the first N spread over the house.
/// </summary>
public static class DefenderPosts
{
    /// <summary>
    /// Each room's depth (links from the room the front door opens into) and its way in: the middle of the link it is reached by, or, for the front room, of its front door (else
    /// its garage door). A room no way reaches has neither.
    /// </summary>
    public static (IReadOnlyDictionary<string, int> Depth, IReadOnlyDictionary<string, Vector2> Entrance) Ways(HouseFloorPlan house)
    {
        var depth = new Dictionary<string, int>();
        var entrance = new Dictionary<string, Vector2>();
        var doors = house.Doors.ToDictionary(d => d.Id);
        var queue = new Queue<PlanRoom>();
        foreach (var r in house.Rooms)
        {
            var door = r.Doors.Concat(r.GarageDoors).Select(id => doors.GetValueOrDefault(id)).FirstOrDefault(d => d != null);
            if (door == null) continue;
            depth[r.Id] = 0; entrance[r.Id] = (door.A + door.B) / 2;
            queue.Enqueue(r);
        }
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            foreach (var l in r.Links)
            {
                if (depth.ContainsKey(l.To) || house.Room(l.To) is not { } next) continue;
                depth[l.To] = depth[r.Id] + 1; entrance[l.To] = (l.P + l.Q) / 2;
                queue.Enqueue(next);
            }
        }
        return (depth, entrance);
    }

    /// <summary>The posts of <paramref name="house"/> among the <paramref name="placed"/> props (the floor ones; map local), in Godot axes. <paramref name="rng"/> breaks the ties.</summary>
    public static List<DefenderPost> Plan(HouseFloorPlan house, IReadOnlyList<PropFootprint> placed, TrapHouseFile data, Random rng)
    {
        var q = data.Posts;
        var c = data.Clearance;
        var entrance = Ways(house).Entrance;
        var openings = TrapHousePlanner.Openings(house);
        var perRoom = new List<List<DefenderPost>>();
        foreach (var room in house.Rooms)
        {
            if (q.PerRoom == 0 || room.AreaM2 < q.MinRoomM2 || q.SkipTypes.Contains(room.Type) || !entrance.TryGetValue(room.Id, out var way)) continue;
            var near = TrapHousePlanner.NearOpenings(openings, room.Polygon, c.DoorClearM + c.TagDoorGapM);
            var spots = new List<(Vector2 P, float Score)>();
            foreach (var p in TrapHousePlanner.GridSpots(room.Polygon, c.StepM, q.WallGapM))
            {
                if (near.Any(o => PlanGeometry.PointSegment(p, o.A, o.B) < c.DoorClearM)) continue;
                if (placed.Any(f => Vector2.Distance(p, f.Centre) < f.Radius + q.PropGapM && (PlanGeometry.Inside(f.Foot, p) || PlanGeometry.ToOutline(f.Foot, p) < q.PropGapM))) continue;
                spots.Add((p, Vector2.Distance(p, way) + rng.NextSingle() * c.StepM));
            }
            var mine = new List<DefenderPost>();
            var taken = new List<Vector2>();
            foreach (var (p, _) in spots.OrderByDescending(s => s.Score))
            {
                if (mine.Count >= q.PerRoom) break;
                if (taken.Any(t => Vector2.Distance(t, p) < q.ApartM)) continue;
                taken.Add(p);
                var look = way - p;
                mine.Add(new DefenderPost(house.ToGodot(p, 0), look.LengthSquared() > 1e-6f ? TrapHousePlanner.Yaw(Vector2.Normalize(look)) : 0, room.Id));
            }
            if (mine.Count > 0) perRoom.Add(mine);
        }
        var all = new List<DefenderPost>();
        for (var i = 0; perRoom.Any(r => r.Count > i); i++) all.AddRange(perRoom.Where(r => r.Count > i).Select(r => r[i]));
        return all;
    }
}

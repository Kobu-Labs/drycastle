using System.Numerics;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>A hollow house a Trap house Job may take: the house, the road spot nearest its front door (where the Cartel van can park), and how far that is.</summary>
public sealed record TrapHouseCandidate(HouseFloorPlan House, RoadPlace Road, Vector2 RoadGround, float RoadDistanceM);

/// <summary>
/// Which hollow house a Trap house Job takes (docs/cartel.md "Trap house", ADR 0046 §1), pure: one with a floor plan and a front door, the door within <c>choice.road_max_m</c> of a
/// road wide enough for the van (a <see cref="RoadGraph"/> parsed with <c>choice.min_road_width_m</c>), that no other Job uses and that has no player inside.
/// </summary>
public static class TrapHouseChoice
{
    /// <summary>Every house that qualifies, in file order.</summary>
    public static List<TrapHouseCandidate> Candidates(IEnumerable<HouseFloorPlan> houses, RoadGraph roads, float roadMaxM, ICollection<string> inUse, IReadOnlyCollection<Vector3> playerFeet)
    {
        var list = new List<TrapHouseCandidate>();
        foreach (var h in houses)
        {
            if (h.Rooms.Length == 0 || inUse.Contains(h.Id) || playerFeet.Any(h.Contains)) continue;
            var fronts = h.Rooms.SelectMany(r => r.Doors).Select(id => h.Doors.FirstOrDefault(d => d.Id == id)).Where(d => d != null).ToList();
            (RoadPlace Place, float Distance, Vector2 Ground)? best = null;
            foreach (var d in fronts)
            {
                var mid = (d!.A + d.B) / 2;
                var ground = new Vector2(mid.X, -mid.Y); // the road graph's axes: Godot x, z
                if (roads.Nearest(ground, roadMaxM) is { } near && (best == null || near.Distance < best.Value.Distance)) best = (near.Place, near.Distance, roads.PointAt(near.Place));
            }
            if (best is { } b) list.Add(new TrapHouseCandidate(h, b.Place, b.Ground, b.Distance));
        }
        return list;
    }
}

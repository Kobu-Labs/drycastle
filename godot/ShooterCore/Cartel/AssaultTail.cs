using System.Numerics;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// An Assault van's last stretch over open ground (docs/cartel.md "Assaults"), pure: the road plan of a Raid van ends on the road stop nearest the objective; the Rival crew's van drives on from there across the
/// field to its target when that brings it closer (<see cref="OffRoadRoute"/>), and stops short of it (<see cref="OffRoadData.ParkShortM"/>), where no other Assault van of the Heist stopped and where
/// the crew can step out on the navigation mesh (the Raid planner's own check).
/// </summary>
public static class AssaultTail
{
    /// <summary>
    /// <paramref name="plan"/> with the way over open ground to <paramref name="target"/> added to its way in and its stop moved to the end of it; the plan itself when the open way is shorter than
    /// <see cref="OffRoadData.MinTailM"/>, there is none, or no stop on it works. <paramref name="used"/>: the stops of the Heist's earlier vans (and the Cartel van): the new stop is
    /// <see cref="VanConfig.UsedApartM"/> from them. <paramref name="open"/>: the van's width as the server answers it.
    /// </summary>
    public static RaidPlan Extend(RoadGraph roads, RaidPlan plan, Vector2 target, IReadOnlyList<Vector2> used, VanConfig vc, OffRoadData d, OpenLine open, RaidPlanner.Accept accept)
    {
        var way = OffRoadRoute.Plan(roads, plan.StopAt, target, open, d, plan.Stop, plan.Forward, vc.UTurnPenaltyM);
        if (way == null || way.OpenM < d.MinTailM) return plan;
        // stops back from the target, the nearest first: it parks ParkShortM short, further back when a van of the Heist stands there already
        for (var short_ = d.ParkShortM; short_ < way.Path.Length; short_ += Math.Max(vc.UsedApartM, 1f))
        {
            var cut = way.Path.Length - short_;
            if (cut <= 0.5f) break;
            var at = way.Path.At(cut);
            if (Taken(used, at, vc.UsedApartM)) continue;
            var yaw = way.Path.YawAt(cut);
            if (accept(at, yaw) is not { } side) continue;
            var tail = way.Path.Slice(0, cut);
            return plan with { In = plan.In.IsEmpty ? tail : plan.In.Then(tail), StopAt = at, Yaw = yaw, DoorSide = side };
        }
        return plan;
    }

    static bool Taken(IReadOnlyList<Vector2> used, Vector2 p, float apartM)
    {
        foreach (var u in used) if (Vector2.Distance(u, p) < apartM) return true;
        return false;
    }
}

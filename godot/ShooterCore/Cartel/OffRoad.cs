using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// How the Rival crew's vehicles leave the roads (cartel.json <c>off_road</c>; docs/cartel.md "Assaults", "Chase cars"): an Assault van and a chase car (the same driver) use the roads while they bring them closer and cross open
/// ground (fields, meadows) when that is shorter, never over a building, a tree trunk, a fence, water or a slope too steep for a car. Lengths metres.
/// </summary>
public sealed class OffRoadData
{
    /// <summary>A vehicle that stands off the road joins the nearest road within this, when the line to it is open.</summary>
    public float EnterM { get; set; } = 80;
    /// <summary>The places to leave the road for the goal are the road points within this of it, one every <see cref="StepM"/>.</summary>
    public float LeaveM { get; set; } = 300;
    public float StepM { get; set; } = 15;
    /// <summary>Metres of cost for each metre over open ground (a metre of road costs 1): the roads win unless the field is clearly shorter.</summary>
    public float OpenCost { get; set; } = 1.3f;
    /// <summary>Within this of its end a leg over open ground is not looked at (the goal itself stands on something).</summary>
    public float CloseM { get; set; } = 6;
    /// <summary>A road edge narrower than this (roads.json width: tracks 3.0, service 3.5) is a narrow way: a metre of it costs <see cref="TrackCost"/> (wide roads win when they are not much longer), and a car on it keeps to the chase's track_speed_mps.</summary>
    public float TrackWidthM { get; set; } = 4;
    public float TrackCost { get; set; } = 1.5f;
    /// <summary>The most road points whose way is worked out for one plan (the nearest by a lower bound first).</summary>
    public int MaxChecks { get; set; } = 10;
    /// <summary>The open check (the server's): a line is looked at every <see cref="SampleM"/> along it, in three lanes (the vehicle's width), with rays <see cref="RayUpM"/> over the ground, and refused where the ground climbs more than <see cref="MaxGrade"/> between two samples.</summary>
    public float SampleM { get; set; } = 4;
    public float RayUpM { get; set; } = 0.9f;
    public float MaxGrade { get; set; } = 0.45f;
    /// <summary>The strip a car needs: its half width plus <see cref="MarginM"/>; a spot for a chase car to stand on is checked for <see cref="CarHalfWidthM"/> (the car type's half width and a margin).</summary>
    public float MarginM { get; set; } = 0.3f;
    public float CarHalfWidthM { get; set; } = 1.1f;

    public void Validate(string what)
    {
        foreach (var (v, name) in new[] { (EnterM, "enter_m"), (LeaveM, "leave_m"), (StepM, "step_m"), (OpenCost, "open_cost"), (CloseM, "close_m"), (TrackWidthM, "track_width_m"), (SampleM, "sample_m"), (RayUpM, "ray_up_m"), (MaxGrade, "max_grade"), (MarginM, "margin_m"), (CarHalfWidthM, "car_half_width_m") })
            if (!float.IsFinite(v) || v <= 0) throw new DataFileException($"{what}: {name} must be more than 0");
        if (TrackCost < 1) throw new DataFileException($"{what}: track_cost must be at least 1");
        if (OpenCost < 1) throw new DataFileException($"{what}: open_cost must be at least 1 (open ground is never cheaper than road)");
        if (MaxChecks < 1) throw new DataFileException($"{what}: max_checks must be at least 1");
    }
}

/// <summary>Whether a car-wide strip of ground from one point to another (ground axes x, z) is open: nothing solid on it, no water, no slope too steep. The server's world answers.</summary>
public delegate bool OpenLine(Vector2 from, Vector2 to);

/// <summary>A way for a vehicle to a goal: the path, and how much of it is road and how much open ground.</summary>
public sealed record OffRoadPlan(RoadPath Path, float RoadM, float OpenM)
{
    /// <summary>Part of the way lies over open ground (not all of it is road).</summary>
    public bool CrossesOpenGround => OpenM > 0.5f;
}

/// <summary>
/// The way of a Rival crew's vehicle to a goal, pure (docs/cartel.md "Assaults", "Chase cars"): roads while they bring it closer, open ground where that is shorter. It tries going straight; joining the nearest
/// road (when the line to it is open) and leaving it at the road point near the goal that makes the whole way cheapest (a metre of open ground costs <see cref="OffRoadData.OpenCost"/>, a metre of road 1; the
/// line from the road point to the goal must be open). The cheapest of them is the answer; none: no way.
/// </summary>
public static class OffRoadRoute
{
    /// <summary>
    /// The way from <paramref name="from"/> to <paramref name="goal"/>. <paramref name="fromPlace"/> says the vehicle stands on that road place (heading <paramref name="forward"/>, a turn round costing
    /// <paramref name="uTurnM"/>: an Assault van that never turns back over its road); null: it joins the nearest road within <see cref="OffRoadData.EnterM"/> when it must.
    /// </summary>
    public static OffRoadPlan? Plan(RoadGraph roads, Vector2 from, Vector2 goal, OpenLine open, OffRoadData d, RoadPlace? fromPlace = null, bool? forward = null, float uTurnM = 0)
    {
        var oc = d.OpenCost;
        OffRoadPlan? best = null;
        var bestCost = float.PositiveInfinity;

        // straight over open ground (the whole way, or none at all when a road is not worth it)
        var direct = Vector2.Distance(from, goal);
        if (fromPlace == null && (direct <= d.CloseM || open(from, goal)))
        {
            bestCost = direct * oc;
            best = new OffRoadPlan(new RoadPath(new[] { from, goal }), 0, direct);
        }
        if (roads.IsEmpty) return best;

        // join a road
        RoadPlace place; var join = from; var enterM = 0f;
        if (fromPlace is { } fp) place = fp;
        else
        {
            if (roads.Nearest(from, d.EnterM) is not { } near) return best;
            place = near.Place; join = roads.PointAt(place); enterM = Vector2.Distance(from, join);
            if (enterM > d.CloseM && !open(from, join)) return best;
        }
        Func<float, float>? narrow = d.TrackCost > 1f ? w => w < d.TrackWidthM ? d.TrackCost : 1f : null; // a metre of a narrow edge costs track_cost
        var dist = roads.DijkstraFrom(place, forward, uTurnM, narrow);

        // leave it near the goal: the road points within leave_m, the goal's nearest road point too (the road may reach it)
        var min = goal - new Vector2(d.LeaveM, d.LeaveM); var max = goal + new Vector2(d.LeaveM, d.LeaveM);
        var cands = new List<(RoadPlace Place, Vector2 At, float Lower)>();
        foreach (var rp in roads.PointsAlong(d.StepM, min, max, 0))
            cands.Add((rp.Place, rp.Ground, Vector2.Distance(join, rp.Ground) + Vector2.Distance(rp.Ground, goal) * oc));
        if (roads.Nearest(goal, d.LeaveM) is { } onGoal)
        {
            var at = roads.PointAt(onGoal.Place);
            cands.Add((onGoal.Place, at, Vector2.Distance(join, at) + Vector2.Distance(at, goal) * oc));
        }
        cands.Sort((a, b) => a.Lower.CompareTo(b.Lower));
        var checks = 0;
        foreach (var (cp, at, lower) in cands)
        {
            if (checks >= d.MaxChecks || enterM * oc + lower >= bestCost) break;
            if (roads.RouteBetweenCosted(place, forward, uTurnM, cp, dist) is not { } route) continue;
            checks++;
            var leave = Vector2.Distance(at, goal);
            var cost = enterM * oc + route.Cost + leave * oc;
            if (cost >= bestCost) continue;
            if (leave > d.CloseM && !open(at, goal)) continue;
            var pts = new List<Vector2> { from };
            if (enterM > 0.5f) pts.Add(join);
            pts.AddRange(route.Path.Points);
            if (leave > 0.5f) pts.Add(goal);
            bestCost = cost;
            best = new OffRoadPlan(new RoadPath(pts), route.Path.Length, enterM + leave);
        }
        return best;
    }

    /// <summary>The path without its last <paramref name="shortM"/> metres (it stops short of the goal); null when nothing would be left.</summary>
    public static RoadPath? StopShort(RoadPath path, float shortM)
    {
        var cut = path.Length - Math.Max(0, shortM);
        return cut > 0.5f ? path.Slice(0, cut) : null;
    }
}

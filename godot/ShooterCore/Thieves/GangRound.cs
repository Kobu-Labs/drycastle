using System.Numerics;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>A living vehicle's hull as a Gunman's round meets it: whose it is and where it stands for the round.</summary>
public readonly record struct RoundHull(ushort Id, VehiclePose Pose, VehicleDef Def);

/// <summary>Where a Gunman's round stops: the distance (infinite when nothing is in reach) and the hull it stopped on (null: a wall or nothing).</summary>
public readonly record struct RoundStop(float Distance, ushort? Hull);

/// <summary>
/// What a Gunman's round stops on, pure (GLOSSARY "Gunman"; docs/cartel.md "Chase cars"): the nearer of the first wall and the first vehicle hull on its ray. A round shot from inside a vehicle that carries a crew
/// (a rider's) passes that vehicle and every other crewed one (a chase car's rounds do not hurt its sister car); a round shot on foot passes none.
/// </summary>
public static class GangRound
{
    /// <summary>The round passes this hull: it is the shooter's own vehicle, or (for a rider's round) one that carries a crew.</summary>
    public static bool Passes(ushort hull, ushort? own, IReadOnlyList<ushort> crewed) => own != null && (hull == own || crewed.Contains(hull));

    /// <summary>
    /// The stop of the round from <paramref name="origin"/> along <paramref name="dir"/> (unit) within <paramref name="max"/>: the hull nearest on the ray when it is nearer than <paramref name="wall"/> (the first wall,
    /// infinite when none), else the wall. <paramref name="own"/> is the vehicle the shooter rides (null on foot), <paramref name="crewed"/> every vehicle that carries a crew.
    /// </summary>
    public static RoundStop Stop(Vector3 origin, Vector3 dir, float max, float wall, IReadOnlyList<RoundHull> hulls, ushort? own, IReadOnlyList<ushort> crewed)
    {
        ushort? best = null;
        var distance = wall;
        foreach (var h in hulls)
        {
            if (Passes(h.Id, own, crewed)) continue;
            if (VehicleBox.Ray(origin, dir, max, h.Pose.Feet, h.Pose.Yaw, h.Pose.Pitch, h.Pose.Roll, h.Def) is not { } hit || hit.Distance >= distance) continue;
            best = h.Id; distance = hit.Distance;
        }
        return new RoundStop(distance, best);
    }
}

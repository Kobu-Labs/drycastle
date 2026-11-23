using System.Numerics;
using Drycastle.ShooterCore.Minimap;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// Which way a vehicle parked at a point should face: along the nearest road (<see cref="VehicleSpotDef"/> without a yaw). Godot-free. Roads are the
/// polylines of roads.json (<see cref="RoadLine"/>: local metres, x east, y north), the point is in the world's ground axes (x east, z south, so z = -y).
/// </summary>
public static class RoadHeading
{
    /// <summary>The road further than this (metres, to its centre line) from the spot does not set its heading.</summary>
    public const float DefaultMaxDistance = 15f;

    /// <summary>Roads a car does not park along: footpaths and the like.</summary>
    static readonly HashSet<string> NotForCars = new() { "footway", "path", "steps", "cycleway", "pedestrian", "bridleway", "corridor" };

    /// <summary>Yaw (game convention: 0 faces -Z, positive turns left) of a vehicle that moves along the ground direction (<paramref name="dx"/>, <paramref name="dz"/>).</summary>
    public static float YawOf(float dx, float dz) => MathF.Atan2(-dx, -dz);

    /// <summary>
    /// A road has two ways; this picks one the same way for every spot, whatever order the polyline was written in: the one that heads east, and
    /// for a road running exactly north-south the one that heads north (-z). The direction is in roads.json axes (x east, y north).
    /// </summary>
    public static Vector2 Canonical(Vector2 direction)
    {
        var flip = direction.X < -1e-4f || MathF.Abs(direction.X) <= 1e-4f && direction.Y < 0;
        return flip ? -direction : direction;
    }

    /// <summary>
    /// The yaw along the road nearest to the ground point (<paramref name="x"/>, <paramref name="z"/>) within <paramref name="maxDistance"/> metres
    /// (<see cref="Canonical"/> way), or null when no road for cars is that close.
    /// </summary>
    public static float? YawAt(IReadOnlyList<RoadLine> roads, float x, float z, float maxDistance = DefaultMaxDistance)
    {
        var p = new Vector2(x, -z); // roads.json: y is north
        var best = maxDistance * maxDistance;
        Vector2? tangent = null;
        foreach (var road in roads)
        {
            if (NotForCars.Contains(road.Highway)) continue;
            for (var i = 0; i + 1 < road.Points.Length; i++)
            {
                var a = road.Points[i]; var ab = road.Points[i + 1] - a;
                var len2 = ab.LengthSquared();
                if (len2 < 1e-6f) continue;
                var t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
                var d2 = (p - (a + ab * t)).LengthSquared();
                if (d2 < best) { best = d2; tangent = ab; }
            }
        }
        if (tangent is not { } dir) return null;
        var c = Canonical(Vector2.Normalize(dir));
        return YawOf(c.X, -c.Y); // north (+y) is -z on the ground
    }

    /// <summary>The yaw a fixed spot's vehicle has: the spot's own, else along the nearest road, else 0.</summary>
    public static float YawFor(VehicleSpotDef spot, IReadOnlyList<RoadLine> roads) =>
        spot.YawDeg is { } deg ? deg * MathF.PI / 180f : YawAt(roads, spot.X, spot.Z) ?? 0f;
}

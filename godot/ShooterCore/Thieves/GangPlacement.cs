using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a Raid's crew appears when there is no van yet (the dev fallback of <c>/event thieves</c>, ticket 02), and where the map edge is for a crew that flees.</summary>
public static class GangPlacement
{
    /// <summary>
    /// Candidate points on the roads of roads.json between <paramref name="minM"/> and <paramref name="maxM"/> (ground distance) of <paramref name="around"/>, one every
    /// <paramref name="stepM"/> along each polyline, in random order. Points are in world axes (x east, y up with the height of <paramref name="around"/>, z south: the map's
    /// north is -z); the caller snaps them onto the navigation mesh and keeps the first that fits.
    /// </summary>
    public static List<Vector3> RoadPoints(IReadOnlyList<RoadLine> roads, Vector3 around, float minM, float maxM, float stepM, Random rng)
    {
        var points = new List<Vector3>();
        foreach (var road in roads)
        {
            var pts = road.Points;
            for (var i = 0; i + 1 < pts.Length; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                var len = Vector2.Distance(a, b);
                var n = Math.Max(1, (int)MathF.Ceiling(len / stepM));
                for (var s = 0; s < n; s++)
                {
                    var p = Vector2.Lerp(a, b, (float)s / n);
                    var world = new Vector3(p.X, around.Y, -p.Y);
                    var dx = world.X - around.X; var dz = world.Z - around.Z;
                    var d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d >= minM && d <= maxM) points.Add(world);
                }
            }
        }
        for (var i = points.Count - 1; i > 0; i--) // Fisher-Yates
        {
            var j = rng.Next(i + 1);
            (points[i], points[j]) = (points[j], points[i]);
        }
        return points;
    }

    /// <summary>
    /// The point on the border of the box (<paramref name="min"/> and <paramref name="max"/> corners, x and z) nearest to <paramref name="from"/>, at the height of
    /// <paramref name="from"/>: where a crew that flees heads for. A point outside the box is pulled to it, one inside is pushed to the nearest side.
    /// </summary>
    public static Vector3 NearestEdge(Vector3 from, Vector2 min, Vector2 max)
    {
        var x = Math.Clamp(from.X, min.X, max.X);
        var z = Math.Clamp(from.Z, min.Y, max.Y);
        var inside = from.X >= min.X && from.X <= max.X && from.Z >= min.Y && from.Z <= max.Y;
        if (!inside) return new Vector3(x, from.Y, z);
        var toLeft = from.X - min.X; var toRight = max.X - from.X; var toTop = from.Z - min.Y; var toBottom = max.Y - from.Z;
        var m = MathF.Min(MathF.Min(toLeft, toRight), MathF.Min(toTop, toBottom));
        if (m == toLeft) return new Vector3(min.X, from.Y, from.Z);
        if (m == toRight) return new Vector3(max.X, from.Y, from.Z);
        return m == toTop ? new Vector3(from.X, from.Y, min.Y) : new Vector3(from.X, from.Y, max.Y);
    }
}

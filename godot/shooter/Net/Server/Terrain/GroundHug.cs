using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Terrain;

/// <summary>A ray that follows the ground, for what walks (citizens, the Gang, animals): one physics ray that a rise or a hollow does not turn into a wall.</summary>
internal static class GroundHug
{
    /// <summary>
    /// The ends of one ray at <paramref name="height"/> above the ground, along the ground: it runs from the ground height under the start to the ground height under the end, raised where the heightmap
    /// between bulges above that line, so a rise or a hollow is never mistaken for a wall. False without a map (nothing to follow: no ray).
    /// </summary>
    public static bool Ray(Drycastle.MapLoader.MapLoader? map, NVec3 from, NVec3 dir, float maxM, float height, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        if (map == null) return false;
        float G(float t) { var y = map.GroundHeight(from.X + dir.X * t, from.Z + dir.Z * t); return float.IsNaN(y) ? from.Y : y; }
        var (y0, y1) = (from.Y, G(maxM));
        var raise = 0f;
        for (var k = 1; k < 4; k++) raise = MathF.Max(raise, G(maxM * k / 4) - (y0 + (y1 - y0) * k / 4));
        a = new Vector3(from.X, y0 + height + raise, from.Z);
        b = new Vector3(from.X + dir.X * maxM, y1 + height + raise, from.Z + dir.Z * maxM);
        return true;
    }
}

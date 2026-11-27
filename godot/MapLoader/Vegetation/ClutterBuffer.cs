namespace Drycastle.MapLoader.Vegetation;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link, so the raw MultiMesh layout can be tested.

/// <summary>The raw float buffer Godot's MultiMesh takes, made from clutter instances.</summary>
public static class ClutterBuffer
{
    /// <summary>
    /// The MultiMesh buffer of a cell's instances, relative to the cell's origin (Godot axes: x, z, -y of the map's): per instance the three basis rows
    /// each followed by the origin's coordinate (12 floats) and, with <paramref name="colours"/>, the tint r, g, b, a = 1 (4 floats). The basis is a turn about
    /// Godot's +Y axis by Rot, scaled by Scale. One call sets them all, where one call per instance and value took seconds for the village's 1.5 million
    /// instances. <paramref name="min"/> and <paramref name="max"/> are the culling box (the engine would otherwise walk every instance): each instance's origin
    /// plus and minus <paramref name="reach"/> times its scale.
    /// </summary>
    public static float[] Build(IReadOnlyList<ClutterInstance> cell, (float X, float Y, float Z) origin, bool colours, float reach,
        out (float X, float Y, float Z) min, out (float X, float Y, float Z) max)
    {
        var stride = colours ? 16 : 12;
        var buf = new float[cell.Count * stride];
        min = (float.MaxValue, float.MaxValue, float.MaxValue);
        max = (float.MinValue, float.MinValue, float.MinValue);
        for (var k = 0; k < cell.Count; k++)
        {
            var i = cell[k];
            var lx = i.X - origin.X; var ly = i.Z - origin.Y; var lz = -i.Y - origin.Z; // map (x, y, z) -> Godot (x, z, -y)
            var s = i.Scale;
            var sin = MathF.Sin(i.Rot); var cos = MathF.Cos(i.Rot);
            var o = k * stride;
            buf[o] = s * cos; buf[o + 1] = 0f; buf[o + 2] = s * sin; buf[o + 3] = lx;
            buf[o + 4] = 0f; buf[o + 5] = s; buf[o + 6] = 0f; buf[o + 7] = ly;
            buf[o + 8] = -s * sin; buf[o + 9] = 0f; buf[o + 10] = s * cos; buf[o + 11] = lz;
            if (colours)
            {
                var (r, g, b) = ClutterTint.Rgb(i.Tint);
                buf[o + 12] = r; buf[o + 13] = g; buf[o + 14] = b; buf[o + 15] = 1f;
            }
            var reachHere = reach * MathF.Max(s, 0.01f);
            min = (MathF.Min(min.X, lx - reachHere), MathF.Min(min.Y, ly - reachHere), MathF.Min(min.Z, lz - reachHere));
            max = (MathF.Max(max.X, lx + reachHere), MathF.Max(max.Y, ly + reachHere), MathF.Max(max.Z, lz + reachHere));
        }
        return buf;
    }
}

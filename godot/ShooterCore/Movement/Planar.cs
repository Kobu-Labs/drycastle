using System.Numerics;

namespace Drycastle.ShooterCore.Movement;

/// <summary>Measures on the ground plane (x, z): the height is ignored.</summary>
internal static class Planar
{
    /// <summary>The distance between two points seen from above.</summary>
    public static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

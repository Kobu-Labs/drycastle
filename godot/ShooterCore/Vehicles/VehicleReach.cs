using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>How near an on-foot player is to a vehicle for getting in (#217): measured to the footprint rectangle, not to the centre, so a 12 m bus can be boarded anywhere along its side.</summary>
public static class VehicleReach
{
    /// <summary>Horizontal metres from <paramref name="from"/> to the nearest point of the vehicle's oriented footprint; 0 when inside it.</summary>
    public static float DistanceToFootprint(Vector3 carPos, float yaw, float halfWidth, float halfLength, Vector3 from)
    {
        var d = from - carPos;
        var lx = d.X * MathF.Cos(yaw) - d.Z * MathF.Sin(yaw);
        var lz = -d.X * MathF.Sin(yaw) - d.Z * MathF.Cos(yaw);
        var dx = MathF.Max(MathF.Abs(lx) - halfWidth, 0);
        var dz = MathF.Max(MathF.Abs(lz) - halfLength, 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>The player is within <paramref name="radius"/> of the footprint and not more than 2.5 m above or below the vehicle's origin (not one hovering overhead).</summary>
    public static bool CanEnter(Vector3 carPos, float yaw, float halfWidth, float halfLength, Vector3 from, float radius) =>
        MathF.Abs(carPos.Y - from.Y) <= 2.5f && DistanceToFootprint(carPos, yaw, halfWidth, halfLength, from) <= radius;
}

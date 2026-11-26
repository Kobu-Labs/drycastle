using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>One carve: a sphere, centre and radius in quarter-cells, in the house's grid frame (Contract 3 and 4). WP-A.</summary>
public readonly record struct CarveOp(short Cx, short Cy, short Cz, ushort RadiusQ)
{
    /// <summary>The op of a blast at <paramref name="map"/> (map local, z up) of <paramref name="radiusM"/> metres; null when the sphere misses the grid.</summary>
    public static CarveOp? From(HouseGrid g, Vector3 map, float radiusM) => throw new NotImplementedException("WP-A: CarveOp.From");
}

using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Faces and box covers of a house's cells. WP-C.</summary>
public static class HouseMesher
{
    /// <summary>Greedy faces of the live cells, no allocation beyond <paramref name="into"/>.</summary>
    public static void Surface(HouseCells c, MeshMode mode, List<Quad> into) => throw new NotImplementedException("WP-C: HouseMesher.Surface");
    /// <summary>A debris piece: every face of <paramref name="cells"/>.</summary>
    public static void Surface(HouseGrid g, ReadOnlySpan<int> cells, List<Quad> into) => throw new NotImplementedException("WP-C: HouseMesher.Surface(piece)");
    /// <summary>Greedy box cover of the live cells, for colliders (grid units).</summary>
    public static void Boxes(HouseCells c, List<(Vector3 Min, Vector3 Max)> into) => throw new NotImplementedException("WP-C: HouseMesher.Boxes");
}

using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Voronoi split of a fallen component into pieces (the last group is dust). WP-C.</summary>
public static class Fracture
{
    public static IReadOnlyList<int[]> Split(HouseGrid g, int[] cells, Vector3 impactGrid, uint seed, FractureConfig cfg) => throw new NotImplementedException("WP-C: Fracture.Split");
}

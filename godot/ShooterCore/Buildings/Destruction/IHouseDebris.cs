using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Client-side cosmetic debris (D7). Called by the client world (WP-F), implemented by WP-G.</summary>
public interface IHouseDebris
{
    /// <summary>Fallen -> bodies, Crumbled and Carved -> dust. <paramref name="blastMap"/> is map local (z up).</summary>
    void Fall(HouseGrid g, CarveResult r, Vector3 blastMap, uint seed);
    void SetRubble(bool stay);
    /// <summary>Round reset.</summary>
    void ResetAll();
}

using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Who a ray reaches on a vehicle whose gunner sits in the open (vehicles.json <c>exposed_gunner</c>, the flak truck's turret). The hull's
/// hit box stops a bullet at its surface, and the gunner stands inside that box, so the hull would always shadow him. Here the gunner is a
/// target of his own: a ray that reaches his body before the world beyond the hull stops it hits him, wherever the hull's surface was, and the hull
/// takes nothing from that ray. Every other target stays blocked by the hull as before, and a ray that misses the gunner goes on to hit the hull.
/// Pure, so the tests run the server's own arithmetic.
/// </summary>
public static class GunnerCover
{
    /// <summary>
    /// The nearest target the ray hits. <paramref name="wall"/> is where the world (the hull included) blocks it; <paramref name="exposed"/> is the
    /// exposed gunner of the vehicle the ray stopped on (null: none, then this is <see cref="HitMath.Raycast"/>), and <paramref name="wallPastHull"/>
    /// is where the world blocks the ray with that hull left out. The gunner is tested against the latter, the others against <paramref name="wall"/>,
    /// and the nearer hit wins (a player in front of the truck still takes the shot meant for the gunner behind him).
    /// </summary>
    public static RayHit? Raycast(Vector3 origin, Vector3 dir, float maxDistance, float wall, IEnumerable<HitTarget> targets, ushort shooter, HitTarget? exposed, float wallPastHull)
    {
        var normal = HitMath.Raycast(origin, dir, maxDistance, wall, targets, shooter);
        if (exposed is not { } g || g.Id == shooter) return normal;
        var gunner = HitMath.Raycast(origin, dir, maxDistance, wallPastHull, new[] { g }, shooter);
        if (gunner is not { } gh) return normal;
        return normal is { } n && n.Distance <= gh.Distance ? normal : gunner;
    }
}

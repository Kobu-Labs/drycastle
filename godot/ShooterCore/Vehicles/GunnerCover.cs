using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The riders of one vehicle its hull does not cover, as hit targets: the flak truck's or the utility helicopter's gunner (one), or everyone
/// aboard an amphibious transport (up to three, the driver included). A single target converts to it, so one exposed gunner is passed as before.
/// </summary>
public sealed class ExposedRiders
{
    public IReadOnlyList<HitTarget> Riders { get; }

    public ExposedRiders(IReadOnlyList<HitTarget> riders) => Riders = riders;

    public static implicit operator ExposedRiders?(HitTarget? one) => one is { } t ? new ExposedRiders(new[] { t }) : null;
}

/// <summary>
/// Who a ray reaches on a vehicle with riders in the open (vehicles.json <c>exposed_gunner</c>, the flak truck's turret and the helicopter's
/// door; a seat's <c>exposed</c>, every seat of the amphibious transport, ADR 0009). The hull's hit box stops a bullet at its surface, and a rider
/// sits inside or behind that box, so the hull would always shadow them. Here each exposed rider is a target of their own: a ray that reaches a
/// rider's body before the world beyond the hull stops it hits that rider, wherever the hull's surface was, and the hull takes nothing from that
/// ray. Every other target stays blocked by the hull as before, and a ray that misses the riders goes on to hit the hull.
/// Pure, so the tests run the server's own arithmetic.
/// </summary>
public static class GunnerCover
{
    /// <summary>
    /// The nearest target the ray hits. <paramref name="wall"/> is where the world (the hull included) blocks it; <paramref name="exposed"/> are the
    /// exposed riders of the vehicle the ray stopped on (null: none, then this is <see cref="HitMath.Raycast"/>), and <paramref name="wallPastHull"/>
    /// is where the world blocks the ray with that hull left out. The riders are tested against the latter, the others against <paramref name="wall"/>,
    /// and the nearest hit wins (a player in front of the vehicle still takes the shot meant for the rider behind him, and of two riders in line
    /// the nearer one). The shooter is never their own target.
    /// </summary>
    public static RayHit? Raycast(Vector3 origin, Vector3 dir, float maxDistance, float wall, IEnumerable<HitTarget> targets, ushort shooter, ExposedRiders? exposed, float wallPastHull)
    {
        var normal = HitMath.Raycast(origin, dir, maxDistance, wall, targets, shooter);
        if (exposed == null) return normal;
        var rider = HitMath.Raycast(origin, dir, maxDistance, wallPastHull, exposed.Riders, shooter);
        if (rider is not { } rh) return normal;
        return normal is { } n && n.Distance <= rh.Distance ? normal : rider;
    }
}

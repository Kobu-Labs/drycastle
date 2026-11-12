using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>A vehicle's hit volume as the gunship routing sees it: its index in the server's car list, its footprint and its height.</summary>
public readonly record struct GunshipVehicle(int Index, Vector3 Feet, float Yaw, float HalfWidth, float HalfLength, float Height);

/// <summary>What a gunship round's ray stopped on.</summary>
public enum GunshipImpact : byte { None, World, Body, Vehicle }

/// <summary>
/// Where a round's ray ends. <see cref="Damage"/> is the round's damage to that victim before the vehicle and perk multipliers
/// the server applies (for a body: the round's damage; for a vehicle: the damage times the bullet multiplier, since a cannon
/// round with no blast is a bullet).
/// </summary>
public readonly record struct GunshipRoute(GunshipImpact Impact, float Distance, ushort TargetId, int VehicleIndex, float Damage);

/// <summary>One victim of a blast: a body (player or dummy) or a vehicle, with its damage. <see cref="Direct"/> is the hit point.</summary>
public readonly record struct GunshipHit(bool IsVehicle, ushort TargetId, int VehicleIndex, float Damage, float Fraction, bool Direct);

/// <summary>
/// The damage routing of a gunship turret, pure so it runs in the tests. <see cref="Route"/> finds what a ray stops on (a body,
/// a vehicle's hit volume, the world, or nothing); <see cref="Burst"/> is a blast at a point, with the hit point at full damage
/// and everything in the radius on the splash falloff. Both use the same numbers the server applies, so a test that passes
/// here is the damage the server does.
/// </summary>
public static class GunshipRouting
{
    /// <summary>
    /// The nearest thing the ray stops on within the weapon's range: a live body (<see cref="HitMath.Raycast"/>, with the world and
    /// the vehicles blocking it), else a vehicle's hit volume (<see cref="VehicleBox"/>), else the world.
    /// </summary>
    /// <param name="wallDistance">Where the world blocks the ray (infinity if nothing). Vehicles are not part of it: they are <paramref name="vehicles"/>.</param>
    /// <param name="bulletMultiplier">vehicles.json bullet_damage_multiplier: the damage a bare cannon round does to a vehicle.</param>
    public static GunshipRoute Route(GunshipWeaponDef def, Vector3 origin, Vector3 dir, float wallDistance, IEnumerable<HitTarget> targets, ushort owner, IReadOnlyList<GunshipVehicle> vehicles, float bulletMultiplier)
    {
        var range = def.RangeM;
        var car = -1;
        var carDist = float.PositiveInfinity;
        foreach (var v in vehicles)
        {
            if (VehicleBox.Ray(origin, dir, range, v.Feet, v.Yaw, v.HalfWidth, v.HalfLength, v.Height) is not { } h || h.Distance >= carDist) continue;
            carDist = h.Distance;
            car = v.Index;
        }
        var blocker = MathF.Min(wallDistance, carDist);
        if (HitMath.Raycast(origin, dir, range, blocker, targets, owner) is { } body)
            return new GunshipRoute(GunshipImpact.Body, body.Distance, body.Id, -1, def.Damage);
        if (car >= 0 && carDist <= range)
            return new GunshipRoute(GunshipImpact.Vehicle, carDist, 0, car, def.Damage * bulletMultiplier);
        if (wallDistance <= range)
            return new GunshipRoute(GunshipImpact.World, wallDistance, 0, -1, 0);
        return new GunshipRoute(GunshipImpact.None, range, 0, -1, 0);
    }

    /// <summary>
    /// A blast at <paramref name="at"/> from a round of <paramref name="def"/> (its <see cref="GunshipWeaponDef.Splash"/> must be set).
    /// The direct hit (a body id, or a vehicle index) takes the full round damage and no splash on top. Every other live body in
    /// the radius takes the falloff, except the owner, who is never hurt by its own blast; every other vehicle in the radius takes the falloff times <paramref name="explosiveMultiplier"/>
    /// (vehicles.json rocket_damage_multiplier). <paramref name="takenMultiplier"/> is a body's perk on what it takes (Flak Jacket).
    /// </summary>
    public static List<GunshipHit> Burst(GunshipWeaponDef def, Vector3 at, ushort owner, ushort? directBody, int directVehicle,
        IEnumerable<HitTarget> targets, IReadOnlyList<GunshipVehicle> vehicles, float explosiveMultiplier, Func<ushort, float>? takenMultiplier = null)
    {
        var splash = def.Splash ?? throw new InvalidOperationException($"gunship weapon \"{def.Id}\" has no blast");
        var w = new WeaponDef { Damage = def.Damage, Splash = splash };
        var hits = new List<GunshipHit>();
        foreach (var t in targets)
        {
            var isDirect = directBody == t.Id;
            if (!isDirect && t.Id == owner) continue; // the gunner's own blast never hurts the gunner
            var dist = HitMath.DistanceToBody(at, t);
            var dmg = isDirect ? def.Damage : ProjectileMath.SplashDamage(w, dist, t.Id == owner);
            if (dmg <= 0) continue;
            var taken = takenMultiplier?.Invoke(t.Id) ?? 1f;
            if (taken != 1f) dmg = MathF.Max(1f, MathF.Round(dmg * taken));
            hits.Add(new GunshipHit(false, t.Id, -1, dmg, isDirect ? 1f : ProjectileMath.SplashFraction(w, dist), isDirect));
        }
        foreach (var v in vehicles)
        {
            var isDirect = directVehicle == v.Index;
            var dist = VehicleDamage.DistanceToBox(at, v.Feet, v.Yaw, v.HalfWidth, v.HalfLength, v.Height);
            var dmg = (isDirect ? def.Damage : ProjectileMath.SplashDamage(w, dist, false)) * explosiveMultiplier;
            if (dmg <= 0) continue;
            hits.Add(new GunshipHit(true, 0, v.Index, dmg, isDirect ? 1f : ProjectileMath.SplashFraction(w, dist), isDirect));
        }
        return hits;
    }
}

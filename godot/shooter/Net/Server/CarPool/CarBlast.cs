using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>A vehicle blowing up: its explosion, what it throws and what it hurts, and the chain to the vehicles beside it.</summary>
internal sealed class CarBlast
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly CarDamage _damage;
    readonly CarSeats _seats;
    readonly RidingCorpses _riding;
    readonly WorldBlasts _blasts;
    readonly ImpactFx _impacts;
    readonly Splash _splash;
    readonly DeathPush _deathPush;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly Action<NVec3, ushort> _cartBlast;
    readonly SmokeStats _smoke;
    readonly VehiclesFile _vehicles;

    /// <param name="cartBlast">The Scud blast where a loaded rocket cart stood, credited to the attacker.</param>
    public CarBlast(ServerContext ctx, Fleet fleet, CarDamage damage, CarSeats seats, RidingCorpses riding, WorldBlasts blasts, ImpactFx impacts, Splash splash, DeathPush deathPush, TargetBook targets,
        DealDamage deal, Action<NVec3, ushort> cartBlast, SmokeStats smoke, VehiclesFile vehicles)
    {
        _ctx = ctx; _fleet = fleet; _damage = damage; _seats = seats; _riding = riding; _blasts = blasts; _impacts = impacts; _splash = splash; _deathPush = deathPush; _targets = targets;
        _deal = deal; _cartBlast = cartBlast; _smoke = smoke; _vehicles = vehicles;
    }

    /// <summary>
    /// The vehicle blows up: broadcast the event, kill the driver, splash everyone alive and every other vehicle in range (those that run out of HP
    /// go off after the chain delay), then the vehicle is gone until it returns. The last attacker gets the credit.
    /// </summary>
    public void Detonate(ServerCar car)
    {
        var at = car.GlobalPosition + Vector3.Up * 0.8f;
        var driver = _fleet.OccupantAt(car, 0);
        var attacker = car.LastAttacker != 0 ? car.LastAttacker : driver?.Id ?? (ushort)0;
        var weapon = car.LastWeapon != 0 ? car.LastWeapon : SpecialWeapons.Vehicle;
        var carVelocity = CarPose.Velocity(car); // before Destroy zeroes it: the riders' bodies fly on with it
        var loadedCart = car.Cargo?.Detonates(car.Def) ?? false; // before the destruction: a rocket cart that still carries its missile
        car.Destroy(car.Def.Returns ? _ctx.Now + _vehicles.DestroyedReturnTime : double.MaxValue); // a type that never returns (the Scud truck) is gone for the match
        _smoke.VehicleBlasts++;
        if (loadedCart) CartDetonated(car, car.GlobalPosition.ToNumerics(), attacker); // it does not blow up like a vehicle: the Scud's blast is the hook's
        else Explode(car, at, attacker, weapon, carVelocity);
    }

    /// <summary>
    /// THE hook: a rocket cart that was carrying its missile is destroyed (bullets, a blast, a crash, a chain reaction; <paramref name="cart"/> is already destroyed, its return timer set and its tractor
    /// let go) at <paramref name="position"/>, credited to <paramref name="attackerId"/> (0: nobody). It is the Scud blast where the cart stood, credited to whoever shot it. Other loaded
    /// carts in the blast's damage are doomed and go up in their turn (<see cref="ScudBlastRules.ChainDelay"/>), each once: a doomed or destroyed vehicle takes no second date. An empty cart never comes here: it blows
    /// up as a small ordinary vehicle.
    /// </summary>
    void CartDetonated(ServerCar cart, NVec3 position, ushort attackerId)
    {
        GD.Print($"server: rocket cart {cart.Id} detonated at ({position.X:F1},{position.Y:F1},{position.Z:F1}), credited to #{attackerId}");
        _cartBlast(position, attackerId);
    }

    /// <summary>
    /// The ordinary vehicle explosion of <paramref name="car"/> (already destroyed, its life and its return set): the event for every client, the ground thrown up, the panes, then everyone aboard
    /// and everyone and every vehicle within the blast's reach. Credited to <paramref name="attacker"/>.
    /// </summary>
    void Explode(ServerCar car, Vector3 at, ushort attacker, byte weapon, NVec3 carVelocity)
    {
        var e = _vehicles.Explosion;
        _ctx.Broadcast(EventKind.VehicleExplosion, WeaponCodec.Encode(new VehicleExplosionMsg(car.Id, attacker, at.ToNumerics(), e.Splash.Radius)));
        _impacts.Broadcast(car.GlobalPosition.ToNumerics(), ImpactSize.Plume); // the blast throws up dirt or road where the car sits (ticket #154)
        _blasts.Blast(at, e.Splash.Radius, BlastSource.Car); // the panes and the houses
        GD.Print($"server: {car.Def.Id} {car.Id} exploded at ({at.X:F1},{at.Y:F1},{at.Z:F1}), credited to #{attacker}");

        _riding.Throw(car, carVelocity, at.ToNumerics(), e.Splash, 1f); // the dead pilots and drivers still in their seats are flung out (ADR 0026)
        HurtRiders(car, at, attacker, weapon, carVelocity);
        HurtBystanders(car, at, attacker, weapon);
        foreach (var other in _fleet.Cars)
        {
            if (other == car || other.Destroyed) continue;
            var dist = CarPose.HullDistanceNow(other, at.ToNumerics());
            var dmg = VehicleDamage.BlastDamage(e, dist) * e.VehicleDamageMultiplier;
            if (dmg <= 0) continue;
            _smoke.ChainHits++;
            _damage.Damage(other, dmg, attacker, weapon, e.ChainDelay);
        }
    }

    /// <summary>Everyone still aboard (a gunner too): a rider who dies is thrown with the vehicle's velocity plus the blast's knockback from its centre (no corpse stays seated in a wreck); one who survives (spawn protection) is thrown out, the seat is gone.</summary>
    void HurtRiders(ServerCar car, Vector3 at, ushort attacker, byte weapon, NVec3 carVelocity)
    {
        var e = _vehicles.Explosion;
        foreach (var o in _fleet.Occupants(car))
        {
            var k = ProjectileMath.Knockback(e.Splash.KnockbackSpeed, at.ToNumerics(), o.Body.GlobalPosition.ToNumerics() + NVec3.UnitY * 0.9f, 1f);
            _deathPush.Record(o.Id, (k == NVec3.Zero ? NVec3.Zero : CorpseRules.ThrowFromBlast(k)) + carVelocity);
            var dealt = _deal(attacker, weapon, o.Id, e.DriverDamage, false, false);
            _deathPush.Clear(o.Id, dealt);
            if (o.Car == car) _seats.Leave(o, true);
        }
    }

    /// <summary>Everyone else in the blast's reach, by its falloff, behind walls and covered seats as for any blast.</summary>
    void HurtBystanders(ServerCar car, Vector3 at, ushort attacker, byte weapon)
    {
        var e = _vehicles.Explosion;
        var centre = at.ToNumerics();
        foreach (var t in _targets.Live())
        {
            if (car.Seat.SeatOf(t.Id) >= 0) continue;
            var dist = HitMath.DistanceToBody(centre, t);
            var dmg = VehicleDamage.BlastDamage(e, dist);
            if (dmg <= 0) continue;
            var share = _splash.Share(centre, t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            dmg *= share;
            var self = t.Id == attacker;
            if (self) dmg = MathF.Max(1f, MathF.Round(dmg * e.Splash.SelfDamageMultiplier));
            if (_ctx.Players.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(e.Splash.KnockbackSpeed, centre, HitMath.Centre(t), dmg / e.Damage);
            _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = _deal(attacker, weapon, t.Id, dmg, false, false);
            _deathPush.Clear(t.Id, res);
            if (res == DamageResult.Ignored) continue;
            _smoke.BlastHits++;
            if (res != DamageResult.Killed && _ctx.Players.TryGetValue(t.Id, out var v) && k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
    }
}

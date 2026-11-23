using Godot;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>What hurts a vehicle: bullets, rockets, blasts and wall impacts (<see cref="Damage"/>, <see cref="Rockets"/>) and the one crash rule (<see cref="Crash"/>).</summary>
internal sealed class CarDamage
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly GlassServer _glass;
    readonly SmokeStats _smoke;
    readonly VehiclesFile _vehicles;

    public CarDamage(ServerContext ctx, Fleet fleet, GlassServer glass, SmokeStats smoke, VehiclesFile vehicles)
    {
        _ctx = ctx; _fleet = fleet; _glass = glass; _smoke = smoke; _vehicles = vehicles;
    }

    /// <summary>
    /// Hurts a vehicle (bullet, rocket, blast or wall impact). At 0 HP it is scheduled to blow up <paramref name="delay"/> seconds from now
    /// (0 = in this tick's vehicle step). <paramref name="attacker"/> 0 means nobody: the blast is then credited to nobody (or the driver's own crash).
    /// </summary>
    public void Damage(ServerCar car, float amount, ushort attacker, byte weapon, float delay)
    {
        car.Wake();
        if (car.Destroyed || amount <= 0) return;
        if (_ctx.Match.VehicleHitBlocked(attacker, Fleet.SeatOccupants(car))) return; // a teammate aboard: no damage, no hit credit (#338)
        if (attacker != 0 && _ctx.Players.TryGetValue(attacker, out var attackerClient)) amount *= attackerClient.Perks.VehicleDamageDealt; // Tank Hunter
        if (attacker != 0) { car.LastAttacker = attacker; car.LastWeapon = weapon; }
        var before = VehicleDamage.Stage(car.Hp, car.Life.MaxHp, _vehicles.DamageStages);
        car.Life.Damage(amount, _ctx.Now, delay); // at 0 HP the blast is scheduled `delay` seconds from now (0 = this tick's vehicle step)
        _smoke.VehicleHits++;
        var stage = VehicleDamage.Stage(car.Hp, car.Life.MaxHp, _vehicles.DamageStages);
        if (stage != before) GD.Print($"server: {car.Def.Id} {car.Id} damage stage {before} -> {stage} (hp {car.Hp:F0}/{car.Life.MaxHp:F0})");
    }

    /// <summary>
    /// The one crash rule (#417, #431): a collision that took <paramref name="speedLoss"/> m/s from <paramref name="car"/> in one tick, whatever it
    /// met (a wall, a tree, a ledge, the ground under a helicopter, another vehicle, a Raid van). Up to crash_min_speed_loss the hull takes
    /// nothing; the panes crack and shatter by their own two speeds (<see cref="GlassServer.Crash"/>). A type with crash_damage off takes neither.
    /// From crash_sound_min_speed_loss it is heard (#410).
    /// </summary>
    public void Crash(ServerCar car, float speedLoss, ushort attacker)
    {
        // heard by everyone, whatever the type takes from it (#410); one per vehicle in crash_sound_gap, so grinding along a wall is not a crash every tick
        if (_vehicles.CrashSoundOf(speedLoss) is var sound and not CrashSound.None && car.CrashSoundGap.StartIfReady(_vehicles.CrashSoundGap))
            _ctx.Broadcast(EventKind.VehicleCrash, VehicleCrashCodec.Encode(car.Id, sound));
        if (!car.Def.CrashDamage) return;
        _glass.Crash(car, speedLoss); // before the hull: a crash that dooms the vehicle still breaks its glass
        var damage = _vehicles.CrashDamage(speedLoss);
        if (damage > 0) Damage(car, damage, attacker, SpecialWeapons.Vehicle, 0);
    }

    /// <summary>Vehicle damage of a weapon (rocket direct hit or splash) by the vehicles.json multiplier.</summary>
    public void Rockets(Vector3 pos, WeaponDef w, ushort owner, byte weapon, ServerCar? direct)
    {
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed) continue;
            float dmg;
            if (car == direct) dmg = w.Damage;
            else dmg = ProjectileMath.SplashDamage(w, CarPose.HullDistanceNow(car, pos.ToNumerics()), false);
            Damage(car, dmg * _vehicles.RocketDamageMultiplier, owner, weapon, 0);
        }
    }
}

using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>A probe along a ray: the distance to the first thing it stops on, and the vehicle that was (the one to skip aside).</summary>
internal delegate float WorldHitProbe(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip = null);

/// <summary>
/// The attack missile on the server (killstreak "attack_missile"): the adapter the streak flies in (world sweeps, live
/// players, the owner's facing) and the blast. The blast is the rpg's splash code with the missile's own tuning, and
/// its kills are credited to the missile (<see cref="SpecialWeapons.Missile"/>).
/// </summary>
internal sealed class MissileServer : IMissileWorld
{
    readonly ServerContext _ctx;
    readonly KillstreakFile _killstreaks;
    readonly TargetBook _targets;
    readonly WorldHitProbe _worldHit;
    readonly Func<bool> _hasVehicles;
    readonly Action<Vector3, WeaponDef, ushort, byte, ServerCar?> _rocketVsVehicles;
    readonly Action<Vector3, float> _blastGlass;
    readonly DealDamage _deal;
    readonly ImpactFx _impacts;
    readonly Splash _splash;
    readonly DeathPush _deathPush;
    MissileTuning? _tuning;

    /// <param name="hasVehicles">Whether vehicles.json is loaded (a missile blast hurts vehicles only then).</param>
    /// <param name="rocketVsVehicles">The rpg's blast on the vehicles around a point (position, weapon, owner, credit weapon, the vehicle it hit).</param>
    /// <param name="blastGlass">The panes a blast of this radius breaks.</param>
    public MissileServer(ServerContext ctx, KillstreakFile killstreaks, TargetBook targets, WorldHitProbe worldHit, Func<bool> hasVehicles, Action<Vector3, WeaponDef, ushort, byte, ServerCar?> rocketVsVehicles,
        Action<Vector3, float> blastGlass, DealDamage deal, ImpactFx impacts, Splash splash, DeathPush deathPush)
    {
        _ctx = ctx; _killstreaks = killstreaks; _targets = targets; _worldHit = worldHit; _hasVehicles = hasVehicles; _rocketVsVehicles = rocketVsVehicles; _blastGlass = blastGlass;
        _deal = deal; _impacts = impacts; _splash = splash; _deathPush = deathPush;
    }

    /// <summary>The streak's implementation, wired to this server.</summary>
    public IKillstreak Streak()
    {
        _tuning = _killstreaks.Find(AttackMissileStreak.StreakId)?.Missile
            ?? throw new InvalidOperationException("killstreaks.json: attack_missile has no missile tuning");
        AttackMissileRules.Register(WorldEntityRegistry.Shared);
        return new AttackMissileStreak(this, _tuning);
    }

    // ---- IMissileWorld: the match's entities, the sweeps and the blast ----

    public WorldEntityManager Entities => _ctx.Match.Entities;

    public float WorldDistance(NVec3 from, NVec3 dir, float len, out object? hitCar)
    {
        var d = _worldHit(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, out var car);
        hitCar = car;
        return d;
    }

    public IEnumerable<HitTarget> Targets() => _targets.Live();

    public float OwnerYaw(ushort owner) => _ctx.Players.TryGetValue(owner, out var c) ? c.Body.Yaw : 0f;

    /// <summary>Resolves the missile's blast: splash to every player in reach, the vehicles, and the event for clients.</summary>
    public void Detonate(MissileImpact i)
    {
        if (_tuning == null) return;
        var w = _tuning.AsWeapon();
        var pos = new Vector3(i.Position.X, i.Position.Y, i.Position.Z);
        var at = pos.ToNumerics();
        var radius = _tuning.SplashRadius;
        if (_hasVehicles()) _rocketVsVehicles(pos, w, i.Owner, SpecialWeapons.Missile, i.Car as ServerCar);
        _blastGlass(pos, radius);
        _ctx.Broadcast(EventKind.MissileBlast, MissileCodec.EncodeBlast(i.Id, i.Owner, i.Position, radius));
        _impacts.Broadcast(at, ImpactSize.Plume, vehicle: i.Car != null); // ticket #154: the missile's plume
        var targets = _targets.Live();
        foreach (var t in targets)
        {
            var isDirect = i.Victim == t.Id;
            var dist = HitMath.DistanceToBody(at, t);
            var self = t.Id == i.Owner;
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
            if (dmg <= 0) continue;
            var share = isDirect ? 1f : _splash.Share(at, t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            dmg *= share;
            if (_ctx.Players.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(w, at, HitMath.Centre(t), isDirect ? 1f : share * ProjectileMath.SplashFraction(w, dist));
            _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = _deal(i.Owner, SpecialWeapons.Missile, t.Id, dmg, false, false);
            _deathPush.Clear(t.Id, res);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (res != DamageResult.Killed && _ctx.Players.TryGetValue(t.Id, out var v) && k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
        _targets.Return(targets);
    }
}

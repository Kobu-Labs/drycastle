using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The attack missile on the server (killstreak "attack_missile"): the adapter the streak flies in (world sweeps, live
/// players, the owner's facing) and the blast. The blast is the bazooka's splash code with the missile's own tuning, and
/// its kills are credited to the missile (<see cref="SpecialWeapons.Missile"/>).
/// </summary>
public partial class NetServer
{
    MissileTuning? _missileTuning;

    /// <summary>The streak's implementation, wired to this server. Built once per server by <see cref="BuildStreaks"/>.</summary>
    IKillstreak MakeMissileStreak()
    {
        _missileTuning = Killstreaks.Find(AttackMissileStreak.StreakId)?.Missile
            ?? throw new InvalidOperationException("killstreaks.json: attack_missile has no missile tuning");
        AttackMissileRules.Register(WorldEntityRegistry.Shared);
        return new AttackMissileStreak(new MissileWorld(this), _missileTuning);
    }

    /// <summary>Resolves the missile's blast: splash to every player in reach, the vehicles, and the event for clients.</summary>
    void MissileDetonate(MissileImpact i)
    {
        if (_missileTuning == null) return;
        var w = _missileTuning.AsWeapon();
        var pos = new Vector3(i.Position.X, i.Position.Y, i.Position.Z);
        var radius = _missileTuning.SplashRadius;
        if (Vehicles != null) RocketVsVehicles(pos, w, i.Owner, SpecialWeapons.Missile, i.Car as ServerCar);
        BlastGlass(pos, radius);
        BroadcastEvent(EventKind.MissileBlast, MissileCodec.EncodeBlast(i.Id, i.Owner, i.Position, radius));
        BroadcastImpact(N(pos), ImpactSize.Plume, vehicle: i.Car != null); // ticket #154: the missile's plume
        foreach (var t in LiveTargets())
        {
            var isDirect = i.Victim == t.Id;
            if (!isDirect && !BlastReaches(N(pos), t)) continue; // a bunker room's walls are between them
            var dist = HitMath.DistanceToBody(N(pos), t);
            var self = t.Id == i.Owner;
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
            if (dmg <= 0) continue;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(w, N(pos), HitMath.Centre(t), isDirect ? 1f : ProjectileMath.SplashFraction(w, dist));
            PushOnDeath(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(i.Owner, SpecialWeapons.Missile, t.Id, dmg, false, false);
            ClearPush(t.Id, res);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v) && k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
    }

    /// <summary>The streak's view of the server: the match's entities, the sweeps and the blast.</summary>
    sealed class MissileWorld : IMissileWorld
    {
        readonly NetServer _s;
        public MissileWorld(NetServer server) => _s = server;

        public WorldEntityManager Entities => _s._match.Entities;

        public float WorldDistance(NVec3 from, NVec3 dir, float len, out object? hitCar)
        {
            var d = _s.WorldHit(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, out var car);
            hitCar = car;
            return d;
        }

        public IEnumerable<HitTarget> Targets() => _s.LiveTargets();

        public float OwnerYaw(ushort owner) => _s._byId.TryGetValue(owner, out var c) ? c.Body.Yaw : 0f;

        public void Detonate(MissileImpact impact) => _s.MissileDetonate(impact);
    }
}

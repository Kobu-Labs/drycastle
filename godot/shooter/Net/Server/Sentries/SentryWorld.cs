using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Sentries;

/// <summary>
/// Sentry turrets on the server (killstreak ticket "turret"): the bridge between the pure turret rules in ShooterCore and
/// the match. It answers the turret's questions (where the owner is, what the aim ray hits, who is alive and visible) and
/// applies its shots through the normal damage path, so the kill goes to the owner. It also lets knives hit a turret.
/// </summary>
internal sealed class SentryWorld : ITurretWorld
{
    /// <summary>Chest height of an enemy as the turret aims at it, metres above the feet.</summary>
    public const float AimHeightM = 1.0f;

    readonly ServerContext _ctx;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly Func<WorldQuery> _query;

    /// <summary>One buffer for every turret: a turret reads its enemies inside its own tick and keeps none (TurretEntity).</summary>
    readonly List<TurretCandidate> _enemies = new();

    public SentryWorld(ServerContext ctx, TargetBook targets, DealDamage deal, Func<WorldQuery> query)
    {
        _ctx = ctx; _targets = targets; _deal = deal; _query = query;
    }

    /// <summary>The owner's feet and eye, or null when the owner is not on foot (dead, driving, gone).</summary>
    public (NVec3 Feet, NVec3 Eye)? Pose(ushort owner)
    {
        if (!_ctx.Players.TryGetValue(owner, out var c) || c.Car != null || !_ctx.IsAlive(owner)) return null;
        var feet = c.Body.GlobalPosition.ToNumerics();
        return (feet, feet + NVec3.UnitY * MovementStep.EyeHeight(c.Body.State.Stance));
    }

    /// <summary>The first thing the sight line stops on, as for a bullet (<see cref="WorldQuery.Shot"/>: walls, cover, a vehicle's hull), with its point and normal.</summary>
    public GroundHit? WorldRay(NVec3 from, NVec3 dir, float maxDistance)
    {
        var o = new Vector3(from.X, from.Y, from.Z);
        var d = new Vector3(dir.X, dir.Y, dir.Z);
        var stop = _query().Shot(o, d, maxDistance);
        return stop.Hit ? new GroundHit((o + d * stop.Distance).ToNumerics(), stop.Normal.ToNumerics()) : null;
    }

    /// <summary>Every live player except the owner and the owner's teammates, aimed at chest height.</summary>
    public IReadOnlyList<TurretCandidate> Enemies(ushort owner)
    {
        _enemies.Clear();
        var targets = _targets.Live();
        foreach (var t in targets)
            if (BotEnemies.IsEnemy(_ctx.Match, owner, t.Id) && !NonEnemyTargets.IsNonEnemy(t.Id)) _enemies.Add( // citizens are no enemy: automatic guns leave them alone
                new TurretCandidate(t.Id, t.Feet + NVec3.UnitY * AimHeightM));
        _targets.Return(targets);
        return _enemies;
    }

    public void Fire(ushort owner, ushort target, float damage) => _deal(owner, SpecialWeapons.Turret, target, damage, false, false);

    /// <summary>The armed turret a knife swing reaches (the nearest in the swing's cone), or null.</summary>
    public ushort? Melee(NVec3 feet, float yaw, float reach)
    {
        var targets = new List<HitTarget>();
        foreach (var e in _ctx.Match.Entities.Snapshot())
            if (e.Type == TurretRules.TypeId && TurretRules.IsArmed(e.State.State))
                targets.Add(new HitTarget(e.Id, e.State.Position, Stance.Stand, e.State.YawRad));
        return HitMath.Melee(feet, yaw, reach, targets, ushort.MaxValue)?.Id;
    }
}

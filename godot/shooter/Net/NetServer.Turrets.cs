using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Sentry turrets on the server (killstreak ticket "turret"): the bridge between the pure turret rules in ShooterCore and
/// the match. It answers the turret's questions (where the owner is, what the aim ray hits, who is alive and visible) and
/// applies its shots through the normal damage path, so the kill goes to the owner. It also lets knives hit a turret.
/// </summary>
public partial class NetServer
{
    /// <summary>turret.json: set by the host before the server starts.</summary>
    public TurretFile Turret = null!;

    /// <summary>Chest height of an enemy as the turret aims at it, metres above the feet.</summary>
    const float TurretAimHeightM = 1.0f;

    sealed class TurretWorld : ITurretWorld
    {
        readonly NetServer _s;
        public TurretWorld(NetServer server) => _s = server;

        public (NVec3 Feet, NVec3 Eye)? Pose(ushort owner) => _s.TurretPose(owner);
        public GroundHit? WorldRay(NVec3 from, NVec3 dir, float maxDistance) => _s.TurretRay(from, dir, maxDistance);
        public IReadOnlyList<TurretCandidate> Enemies(ushort owner) => _s.TurretEnemies(owner);
        public void Fire(ushort owner, ushort target, float damage) => _s.Deal(owner, SpecialWeapons.Turret, target, damage, false, false);
    }

    /// <summary>The owner's feet and eye, or null when the owner is not on foot (dead, driving, gone).</summary>
    (NVec3 Feet, NVec3 Eye)? TurretPose(ushort owner)
    {
        if (!_byId.TryGetValue(owner, out var c) || c.Car != null || _match.Get(owner)?.Life != PlayerLife.Alive) return null;
        var feet = N(c.Body.GlobalPosition);
        return (feet, feet + NVec3.UnitY * MovementStep.EyeHeight(c.Body.State.Stance));
    }

    /// <summary>The first world surface along a ray (players excluded, vehicles included), with its point and normal.</summary>
    GroundHit? TurretRay(NVec3 from, NVec3 dir, float maxDistance)
    {
        var to = from + dir * maxDistance;
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, to.Y, to.Z));
        q.CollisionMask = CollisionLayers.Bullets; // as for bullets
        q.Exclude = PlayerRids();
        var r = World.GetWorld3D().DirectSpaceState.PerfRay(q);
        if (r.Count == 0) return null;
        return new GroundHit(N(r["position"].AsVector3()), N(r["normal"].AsVector3()));
    }

    /// <summary>Every live player except the owner, aimed at chest height.</summary>
    IReadOnlyList<TurretCandidate> TurretEnemies(ushort owner)
    {
        // one buffer for every turret: a turret reads its enemies inside its own tick and keeps none (TurretEntity)
        _turretEnemies.Clear();
        var targets = LiveTargets();
        foreach (var t in targets)
            if (t.Id != owner && !CitizenRules.IsHitId(t.Id) && !DemonRules.IsHitId(t.Id)) _turretEnemies.Add( // citizens are no enemy: automatic guns leave them alone
                new TurretCandidate(t.Id, t.Feet + NVec3.UnitY * TurretAimHeightM));
        ReturnTargets(targets);
        return _turretEnemies;
    }

    readonly List<TurretCandidate> _turretEnemies = new();

    /// <summary>The armed turret a knife swing reaches (the nearest in the swing's cone), or null.</summary>
    ushort? TurretMelee(NVec3 feet, float yaw, float reach)
    {
        var targets = new List<HitTarget>();
        foreach (var e in _match.Entities.Snapshot())
            if (e.Type == TurretRules.TypeId && TurretRules.IsArmed(e.State.State))
                targets.Add(new HitTarget(e.Id, e.State.Position, Stance.Stand, e.State.YawRad));
        return HitMath.Melee(feet, yaw, reach, targets, ushort.MaxValue)?.Id;
    }
}

using System.Numerics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Scud;

/// <summary>Wire type and registration of the Scud missile's world entity (GLOSSARY "Scud missile").</summary>
public static class ScudMissileRules
{
    /// <summary>World entity type id of the Scud missile (10 is the attack missile, 20 the turret, 30 the care package).</summary>
    public const byte TypeId = 11;

    /// <summary>Registers the type once (idempotent) so the snapshot codec knows it. Not an Interest type: every client sees it at every range.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "scud_missile", HasHp: false));
    }
}

/// <summary>
/// One Scud missile in the air: a world entity that only follows its <see cref="ScudFlightPlan"/>. It is not an Aircraft and not a rocket: it has no hit points, cannot be shot
/// down, locked on to or hit, and sweeps nothing. When its time is up it ends (<see cref="WorldDespawnReason.Destroyed"/>) and calls <c>onImpact</c> once, which is where the
/// server works out the blast (<c>ScudBlastServer.Blast</c>). The state byte is its <see cref="ScudPhase"/>; position, heading and the placer ride the generic record.
/// </summary>
public sealed class ScudMissileEntity : IWorldEntity
{
    readonly ScudFlightPlan _plan;
    readonly Action<ScudMissileEntity> _onImpact;
    float _age;
    bool _impacted;

    public ScudMissileEntity(ushort owner, ScudFlightPlan plan, Action<ScudMissileEntity> onImpact)
    {
        Owner = owner; _plan = plan; _onImpact = onImpact;
    }

    public ushort Id { get; set; }
    public byte TypeId => ScudMissileRules.TypeId;
    /// <summary>The player who placed the lock: the kills are theirs.</summary>
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }
    public ScudFlightPlan Plan => _plan;
    public float Age => _age;
    public Vector3 Position => _plan.At(_age);

    public void Tick(float dt)
    {
        if (Ended != null) return;
        _age += dt;
        if (_age < _plan.Seconds) return;
        _age = _plan.Seconds;
        Ended = WorldDespawnReason.Destroyed;
        if (_impacted) return;
        _impacted = true;
        _onImpact(this);
    }

    /// <summary>Nothing can hurt it.</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize()
    {
        var (yaw, pitch) = _plan.HeadingAt(_age);
        return WorldState.Make(_plan.At(_age), yaw, pitch, Owner, (byte)_plan.PhaseAt(_age), 0);
    }
}

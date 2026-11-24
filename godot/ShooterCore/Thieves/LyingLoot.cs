using System.Numerics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>Rules of the lying Loot entity that need no world: the wire type and the state layout.</summary>
public static class LootRules
{
    /// <summary>Wire id of the lying Loot world entity type (55 is the Raid van).</summary>
    public const byte TypeId = 56;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "loot", HasHp: false));
    }

    /// <summary>A state byte as a kind; a value this build does not know is read as null (the client draws nothing).</summary>
    public static LootKind? KindOf(byte state) => state < LootKinds.Count ? (LootKind)state : null;
}

/// <summary>
/// Loot that lies on the ground (GLOSSARY "Loot"): its carrier was killed. It lies until a Thief takes it up, a player sends it back to its place (or the cap of lying Loot does) or the round resets.
/// A world entity of its own type (<see cref="LootRules.TypeId"/>); the server owns it, the client draws the kind's prop where it lies.
/// <para>
/// Layout of the entity state: state byte = the <see cref="LootKind"/>, owner field = the low 16 bits of the origin id (for tools; the client needs only the kind), position = where it lies
/// (on the ground), yaw = how it lies, hp byte absent.
/// </para>
/// </summary>
public sealed class LyingLoot : IWorldEntity
{
    public LyingLoot(LootOrigin origin, Vector3 at, float yaw) { Origin = origin; Position = at; Yaw = yaw; }

    public LootOrigin Origin { get; }
    public LootKind Kind => Origin.Kind;
    public Vector3 Position { get; }
    public float Yaw { get; }

    public byte TypeId => LootRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>It is gone (taken up, sent back, or the round reset); the manager despawns it on the next tick, or at once through <see cref="WorldEntityManager.Despawn"/>.</summary>
    public void End(WorldDespawnReason reason) => Ended ??= reason;

    public void Tick(float dt) { }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, (ushort)(Origin.Id & 0xFFFF), (byte)Kind, 0);
}

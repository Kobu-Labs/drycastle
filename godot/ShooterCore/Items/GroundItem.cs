using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Ground item rules (issue #267, ADR 0018): an <see cref="ItemStack"/> a player dropped, lying where it landed until someone takes it.</summary>
public static class GroundItemRules
{
    /// <summary>Wire id of the ground item world entity type.</summary>
    public const byte TypeId = 2;
    /// <summary>Seconds a dropped item stays on the map: the corpse timer.</summary>
    public const float LifetimeSeconds = CorpseRules.LifetimeSeconds;
    /// <summary>Reach of the pickup, metres from the item, and the extra reach the server allows for latency: the corpse's.</summary>
    public const float ReachMetres = CorpseRules.ReachMetres, LatencySlackMetres = CorpseRules.LatencySlackMetres;

    /// <summary>Registers the type once (idempotent). The hp byte carries the pickup count, the state byte the item.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "ground item", HasHp: true));
    }

    public static bool InReach(Vector3 player, Vector3 item, float slack = 0) =>
        Vector3.Distance(player, item) <= ReachMetres + slack;

    /// <summary>The item a ground item's world state names (the state byte: the items.json position).</summary>
    public static int ItemOf(WorldState s) => s.State;

    /// <summary>How many times the item has been taken, as the world state shows it (the hp byte, at most 255 on the wire).</summary>
    public static int PickupsOf(WorldState s) => s.Hp;
}

/// <summary>
/// An item on the ground: dropped by a player (<see cref="Owner"/>, who may take it again like anyone). It does not move: the server puts it
/// at the terrain height under the dropper. It carries the item's pickup count, replicated as <see cref="WorldState"/> with the item in
/// <see cref="WorldState.State"/> and the count in <see cref="WorldState.Hp"/>. It takes no damage and expires after
/// <see cref="GroundItemRules.LifetimeSeconds"/>, with the corpses it shares the cap with (<see cref="CorpseBook"/>).
/// </summary>
public sealed class GroundItem : IWorldEntity
{
    float _age;

    public GroundItem(ushort dropper, Vector3 position, ItemStack stack)
    {
        Owner = dropper; Position = position; Stack = stack;
    }

    public byte TypeId => GroundItemRules.TypeId;
    /// <summary>Who dropped it (0 when nobody).</summary>
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }
    public Vector3 Position { get; }
    public ItemStack Stack { get; }

    public void Tick(float dt)
    {
        _age += dt;
        if (Ended == null && _age >= GroundItemRules.LifetimeSeconds) Ended = WorldDespawnReason.Expired;
    }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, 0, 0, Owner, (byte)Stack.Item, Stack.Pickups);
}

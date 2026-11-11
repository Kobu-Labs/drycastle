using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// A world entity type (turret, missile, aircraft, corpse, care package, ...) registered under its wire id.
/// All types share one state codec (<see cref="WorldState"/>); <see cref="HasHp"/> only decides whether the
/// optional hp byte is sent for that type.
/// </summary>
public sealed record WorldEntityDef(byte Id, string Name, bool HasHp);

/// <summary>Registered world entity types by wire id. <see cref="Shared"/> is the table the server and client use.</summary>
public sealed class WorldEntityRegistry
{
    public static readonly WorldEntityRegistry Shared = new();
    readonly Dictionary<byte, WorldEntityDef> _defs = new();

    public void Register(WorldEntityDef def)
    {
        if (!_defs.TryAdd(def.Id, def)) throw new InvalidOperationException($"world entity type {def.Id} is already registered as {_defs[def.Id].Name}");
    }

    public WorldEntityDef? Find(byte id) => _defs.GetValueOrDefault(id);
}

/// <summary>
/// Quantised state of one world entity (the generic codec every type shares). Position uses the same 1/128 m
/// units as <see cref="EntityRecord"/>. <see cref="Owner"/> is the owning player id (0 when none); <see cref="State"/>
/// is a type-defined state byte; <see cref="Hp"/> is only on the wire for types with <see cref="WorldEntityDef.HasHp"/>.
/// </summary>
public readonly record struct WorldState(int X, int Y, int Z, ushort Yaw, short Pitch, ushort Owner, byte State, byte Hp)
{
    public static WorldState Make(Vector3 pos, float yaw, float pitch, ushort owner, byte state, float hp) => new(
        Quant.Pos(pos.X), Quant.Pos(pos.Y), Quant.Pos(pos.Z), Quant.Yaw(yaw), Quant.Pitch(pitch), owner, state,
        (byte)Math.Clamp((int)MathF.Round(hp), 0, 255));

    public Vector3 Position => new(Quant.Pos(X), Quant.Pos(Y), Quant.Pos(Z));
    public float YawRad => Quant.Yaw(Yaw);
    public float PitchRad => Quant.Pitch(Pitch);
}

public enum WorldDespawnReason : byte
{
    /// <summary>Hit points ran out.</summary>
    Destroyed = 0,
    /// <summary>Lifetime ended (a missile that ran out of fuel, a corpse that timed out).</summary>
    Expired = 1,
    /// <summary>Removed by the server (round end, clear).</summary>
    Removed = 2,
    /// <summary>Opened by a player (a care package).</summary>
    Opened = 3,
}

/// <summary>
/// Server-side world entity. The manager owns the id and the lifetime; the entity owns its own behaviour.
/// <see cref="Serialize"/> must be cheap: it is called once per tick for every live entity.
/// </summary>
public interface IWorldEntity
{
    /// <summary>The registered <see cref="WorldEntityDef.Id"/> of this entity's type.</summary>
    byte TypeId { get; }
    /// <summary>Player id that owns the entity (its controller, shooter or killstreak user). 0 when none.</summary>
    ushort Owner { get; }
    /// <summary>Null while alive. When set, the manager despawns the entity with this reason.</summary>
    WorldDespawnReason? Ended { get; }
    void Tick(float dt);
    /// <summary>Called when the server applies damage to this entity. <paramref name="attacker"/> is the player id.</summary>
    void OnDamage(float amount, ushort attacker);
    WorldState Serialize();
}

public readonly record struct WorldEntry(ushort Id, byte Type, WorldState State);
public readonly record struct WorldSpawned(ushort Id, byte Type, WorldState State);
public readonly record struct WorldDespawned(ushort Id, WorldDespawnReason Reason);

/// <summary>
/// Server-side entity manager, owned by the match (<see cref="MatchSim.Entities"/>). Spawn, despawn and damage raise
/// <see cref="Spawned"/> and <see cref="Despawned"/> synchronously in call order; the server turns them into reliable
/// events, so the reliable channel delivers spawn and despawn in the order the match made them.
/// Ids start at 1 and are never handed out again while the entity is live. Capped at <see cref="MaxEntities"/>.
/// </summary>
public sealed class WorldEntityManager
{
    public const int MaxEntities = 256;

    readonly WorldEntityRegistry _types;
    readonly Dictionary<ushort, IWorldEntity> _live = new();
    ushort _lastId;

    public WorldEntityManager(WorldEntityRegistry? types = null) => _types = types ?? WorldEntityRegistry.Shared;

    /// <summary>Error log for refused spawns (unregistered type, cap reached). Null means silent.</summary>
    public Action<string>? Log { get; set; }
    public event Action<WorldSpawned>? Spawned;
    public event Action<WorldDespawned>? Despawned;

    public int Count => _live.Count;

    /// <summary>Spawns the entity and returns its id, or null if the type is unregistered or the cap is reached.</summary>
    public ushort? Spawn(IWorldEntity e)
    {
        var def = _types.Find(e.TypeId);
        if (def == null) { Log?.Invoke($"world entity: type {e.TypeId} is not registered, not spawned"); return null; }
        if (_live.Count >= MaxEntities) { Log?.Invoke($"world entity: cap of {MaxEntities} entities reached, {def.Name} not spawned"); return null; }
        var id = NextId();
        _live[id] = e;
        Spawned?.Invoke(new WorldSpawned(id, e.TypeId, e.Serialize()));
        return id;
    }

    ushort NextId()
    {
        do { _lastId = unchecked((ushort)(_lastId + 1)); } while (_lastId == 0 || _live.ContainsKey(_lastId));
        return _lastId;
    }

    public bool Despawn(ushort id, WorldDespawnReason reason)
    {
        if (!_live.Remove(id)) return false;
        Despawned?.Invoke(new WorldDespawned(id, reason));
        return true;
    }

    /// <summary>Applies damage to a live entity and despawns it at once if it ended. Unknown ids are ignored.</summary>
    public void Damage(ushort id, float amount, ushort attacker)
    {
        if (!_live.TryGetValue(id, out var e)) return;
        e.OnDamage(amount, attacker);
        if (e.Ended is { } reason) Despawn(id, reason);
    }

    /// <summary>Advances every live entity by <paramref name="dt"/> seconds and despawns those that ended.</summary>
    public void Tick(float dt)
    {
        foreach (var id in _live.Keys.ToList())
        {
            if (!_live.TryGetValue(id, out var e)) continue;
            e.Tick(dt);
            if (e.Ended is { } reason) Despawn(id, reason);
        }
    }

    /// <summary>Despawns every live entity of one type owned by <paramref name="owner"/> (a turret when its owner dies or leaves).</summary>
    public void DespawnOwned(ushort owner, byte type, WorldDespawnReason reason)
    {
        foreach (var (id, e) in _live.Where(kv => kv.Value.Owner == owner && kv.Value.TypeId == type).ToList()) Despawn(id, reason);
    }

    /// <summary>Despawns everything (round end). Each despawn raises <see cref="Despawned"/> with <see cref="WorldDespawnReason.Removed"/>.</summary>
    public void Clear()
    {
        foreach (var id in _live.Keys.ToList()) Despawn(id, WorldDespawnReason.Removed);
    }

    /// <summary>The live entities of one type (their ids and the entities themselves), for server rules that look for them (the flak fuse looks for aircraft). A copy: safe to despawn while iterating.</summary>
    public List<(ushort Id, IWorldEntity Entity)> OfType(byte type)
    {
        var list = new List<(ushort, IWorldEntity)>();
        foreach (var (id, e) in _live) if (e.TypeId == type) list.Add((id, e));
        return list;
    }

    /// <summary>The current state of one live entity, or null when it is gone. Looks up one id without building the whole snapshot.</summary>
    public WorldState? StateOf(ushort id) => _live.TryGetValue(id, out var e) ? e.Serialize() : null;

    /// <summary>The live entities with their current state, for snapshots and join catch-up. Order is not significant.</summary>
    public List<WorldEntry> Snapshot()
    {
        var list = new List<WorldEntry>(_live.Count);
        foreach (var (id, e) in _live) list.Add(new WorldEntry(id, e.TypeId, e.Serialize()));
        return list;
    }
}

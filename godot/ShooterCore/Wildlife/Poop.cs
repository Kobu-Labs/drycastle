using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Poop world entity rules: the wire type id.</summary>
public static class PoopRules
{
    /// <summary>Wire id of the poop world entity type (50 animal, 51 citizen, 52 demon).</summary>
    public const byte TypeId = 53;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "poop", HasHp: false));
    }
}

/// <summary>
/// What a pet left on the ground. Static and cosmetic: no hit points, no collision, nobody touches it. It lies
/// <c>poop_lifetime_seconds</c> (pets.json) and expires, or the server removes it when its group keeps too many (<see cref="PoopBook"/>).
/// A world entity of type <see cref="PoopRules.TypeId"/>; its state is only its position.
/// </summary>
public sealed class Poop : IWorldEntity
{
    readonly float _lifetime;
    Lifetime _life;

    public Poop(Vector3 position, float yaw, float lifetimeSeconds)
    {
        Position = position;
        Yaw = yaw;
        _lifetime = lifetimeSeconds;
    }

    public Vector3 Position { get; }
    public float Yaw { get; }
    public byte TypeId => PoopRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended { get; private set; }

    public void Tick(float dt)
    {
        if (Ended == null && _life.Tick(dt, _lifetime)) Ended = WorldDespawnReason.Expired;
    }

    /// <summary>Nothing hurts it.</summary>
    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, 0, 0, 0);
}

/// <summary>
/// When one pet poops: a random wait between <c>poop_min_seconds</c> and <c>poop_max_seconds</c>, then the poop drops as soon as the pet is alive and
/// standing (Idle) or eating (Eat), and the wait starts over. A pet that is walking when its time comes drops it when it next stops. One countdown per pet.
/// </summary>
public sealed class PoopSchedule
{
    readonly PetsFile _cfg;
    readonly Random _rng;
    Countdown _next;

    public PoopSchedule(PetsFile cfg, Random rng)
    {
        _cfg = cfg;
        _rng = rng;
        Restart();
    }

    /// <summary>Seconds until the next poop is due (0 when it is due and waiting for the pet to stand).</summary>
    public float Left => _next.Left;

    /// <summary>Advances the clock; true on the tick a poop drops.</summary>
    public bool Tick(float dt, bool alive, AnimalState state)
    {
        if (!alive) return false;
        _next.Tick(dt);
        if (!_next.Ready || state is not (AnimalState.Idle or AnimalState.Eat)) return false;
        Restart();
        return true;
    }

    void Restart() => _next.Start(_cfg.PoopMinSeconds + (float)_rng.NextDouble() * (_cfg.PoopMaxSeconds - _cfg.PoopMinSeconds));
}

/// <summary>
/// The poops each property group keeps, oldest first. <see cref="Add"/> says which poops to remove so the group stays within
/// <c>poop_max_per_group</c>; <see cref="Forget"/> drops one that is gone by itself (expired).
/// </summary>
public sealed class PoopBook
{
    readonly int _max;
    readonly Dictionary<string, List<ushort>> _byGroup = new();
    readonly Dictionary<ushort, string> _groupOf = new();

    public PoopBook(int maxPerGroup) => _max = maxPerGroup;

    public int Count(string group) => _byGroup.TryGetValue(group, out var l) ? l.Count : 0;
    public int Total => _groupOf.Count;

    /// <summary>Records a new poop (an entity id) and returns the oldest ones of the group that must go now (empty while within the limit).</summary>
    public List<ushort> Add(string group, ushort id)
    {
        if (!_byGroup.TryGetValue(group, out var list)) _byGroup[group] = list = new();
        list.Add(id);
        _groupOf[id] = group;
        var evicted = new List<ushort>();
        while (list.Count > _max)
        {
            evicted.Add(list[0]);
            _groupOf.Remove(list[0]);
            list.RemoveAt(0);
        }
        return evicted;
    }

    /// <summary>A poop is gone (expired, removed): it no longer counts.</summary>
    public void Forget(ushort id)
    {
        if (!_groupOf.Remove(id, out var group)) return;
        _byGroup[group].Remove(id);
    }

    public void Clear() { _byGroup.Clear(); _groupOf.Clear(); }
}

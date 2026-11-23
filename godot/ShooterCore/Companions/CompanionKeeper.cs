using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Companions;

/// <summary>
/// The Companions of a match (GLOSSARY "Companion"): which Companion each player has out (<see cref="Assign"/>) and the world entity of it. A player has at most one. It is in
/// the world while its owner is "present" (the server says so each tick: alive and on foot, <see cref="Update"/>): it appears beside them, and goes in a puff the moment they are
/// not (dead, in a seat, gone). The keeper knows nothing of what a Companion does: that is the kind's (<see cref="Companion"/>). Godot-free.
/// </summary>
public sealed class CompanionKeeper
{
    sealed class Slot
    {
        public required int Def;
        public Companion? Live;
        public ushort Entity;
    }

    readonly CompanionsFile _cfg;
    readonly AnimalsFile _animals;
    readonly WorldEntityManager _entities;
    readonly Random _rng;
    readonly Dictionary<ushort, Slot> _slots = new();
    readonly List<ushort> _players = new(); // the walk of Update: no enumerator over a dictionary an entity's end may touch

    public CompanionKeeper(CompanionsFile cfg, AnimalsFile animals, WorldEntityManager entities, int seed)
    {
        _cfg = cfg; _animals = animals; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>Error log for a Companion that could not be spawned (the entity cap). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Companions in the world now.</summary>
    public int Count { get { var n = 0; foreach (var s in _slots.Values) if (s.Live != null) n++; return n; } }

    /// <summary>The id of the Companion a player has out, or null.</summary>
    public string? Assigned(ushort player) => _slots.TryGetValue(player, out var s) ? _cfg.Companions[s.Def].Id : null;

    /// <summary>The player's Companion in the world, or null (none assigned, or its owner is not present).</summary>
    public Companion? Of(ushort player) => _slots.GetValueOrDefault(player)?.Live;

    /// <summary>
    /// The player has this Companion out from now on (an id of companions.json), or none (null or empty). Another one than before: the old one goes at once and the new one appears at
    /// the next <see cref="Update"/>. False for an unknown id (nothing changes).
    /// </summary>
    public bool Assign(ushort player, string? id)
    {
        if (string.IsNullOrEmpty(id)) { Forget(player); return true; }
        var def = _cfg.IndexOf(id);
        if (def < 0) return false;
        if (_slots.TryGetValue(player, out var s) && s.Def == def) return true;
        Forget(player);
        _slots[player] = new Slot { Def = def };
        return true;
    }

    /// <summary>The player has no Companion any more (they left, or took it back): the one in the world goes.</summary>
    public void Forget(ushort player)
    {
        if (!_slots.Remove(player, out var s) || s.Live == null) return;
        s.Live = null;
        _entities.Despawn(s.Entity, WorldDespawnReason.Destroyed);
    }

    /// <summary>
    /// Once a tick, after the entities ticked. <paramref name="ownerOf"/> says where a player is when they are present (alive, on foot, in the round), null otherwise. A present
    /// owner's Companion is told where they are, or made beside them when there is none; an absent owner's goes.
    /// </summary>
    public void Update(IAnimalWorld world, Func<ushort, CompanionOwner?> ownerOf)
    {
        if (_slots.Count == 0) return;
        _players.Clear();
        foreach (var p in _slots.Keys) _players.Add(p);
        foreach (var player in _players)
        {
            if (!_slots.TryGetValue(player, out var s)) continue;
            var owner = ownerOf(player);
            if (owner is not { } at)
            {
                if (s.Live != null) _entities.Despawn(s.Entity, WorldDespawnReason.Destroyed); // OnDespawned clears the slot
                continue;
            }
            if (s.Live != null) { s.Live.Lead(at); continue; }
            Spawn(player, s, at, world);
        }
    }

    void Spawn(ushort player, Slot s, CompanionOwner at, IAnimalWorld world)
    {
        var def = _cfg.Companions[s.Def];
        var species = _animals.IndexOf(def.Species);
        if (species < 0 || CompanionKinds.Find(def.Kind) is not { } kind) return; // a file that was not checked against animals.json: no Companion, as with no file
        var animal = _animals.Animals[species];
        var live = kind.Create(new CompanionSpawn(def, s.Def, animal, species, Math.Max(0, def.VariantIn(animal)), player, at, world, _rng.Next()));
        if (_entities.Spawn(live) is not { } entity) { Log?.Invoke($"companion {def.Id} of #{player} not spawned: the world is full of entities"); return; } // tried again next tick
        s.Live = live;
        s.Entity = entity;
    }

    /// <summary>A round starts (the match cleared the entities): nothing is in the world, the players keep the Companion they had out.</summary>
    public void Clear()
    {
        foreach (var s in _slots.Values) s.Live = null;
    }

    void OnDespawned(WorldDespawned d)
    {
        foreach (var s in _slots.Values)
            if (s.Live != null && s.Entity == d.Id) { s.Live = null; return; }
    }
}

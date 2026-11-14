using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a weapon hit on a pet (the match calls it for a victim id in the pets' range).</summary>
public interface IAnimalHits
{
    DamageResult Hit(ushort hitId, ushort attacker, float amount);
}

/// <summary>Pet rules shared by the server and the match: the hit ids a pet has as a shot target.</summary>
public static class PetRules
{
    /// <summary>
    /// The id a pet has as a hit target (the weapon paths know bodies by id): in the gap between the turret records (0x7000 + a vehicle index) and the
    /// rockets (0x8000 up), clear of players, vehicles, citizens (40000 up), the demon, target dummies (50000 up) and the Bigfoot. Not the entity id.
    /// </summary>
    public const ushort FirstHitId = 0x7800, HitIdCount = 0x800;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;

    /// <summary>A pet's hit volume is the player capsule scaled to the height of its own hit box (its variant's, else the species'; a head hit counts as a body hit).</summary>
    public static HitTarget TargetOf(ushort hitId, Animal pet) =>
        new(hitId, pet.Position, Stance.Stand, pet.Yaw, HeadGuarded: true, Scale: pet.HitBox[1] / PlayerHitBox.StandHeight);
}

/// <summary>
/// The pets of a match: <see cref="Populate"/> picks property groups by pets.json's rules and puts pets into each group's <see cref="Pen"/>, as
/// <see cref="Animal"/> world entities that never leave it. A pet can be shot dead (nothing scores, nothing replaces it this round; one that dies of one hit leaves no corpse) and leaves poop
/// on a clock of its own (<see cref="PoopSchedule"/>, kept per group by <see cref="PoopBook"/>). Godot-free; the server supplies the ground and calls
/// <see cref="Update"/> each tick.
/// </summary>
public sealed class PetKeeper : IAnimalHits
{
    sealed class Pet
    {
        public required Animal Animal;
        public required ushort Entity;
        public required ushort HitId;
        public required string Group;
        public required PoopSchedule Poop;
        public bool Gone; // despawned or cleared: a tick that already took its snapshot skips it
    }

    /// <summary>Why <see cref="SpawnOne"/> did not put a pet in the world.</summary>
    enum Spawned { Yes, NoGround, EntitiesFull }

    readonly PetsFile _cfg;
    readonly AnimalsFile _animals;
    readonly WorldEntityManager _entities;
    readonly Random _rng;
    readonly PoopBook _poops;
    readonly Dictionary<ushort, Pet> _byHit = new();
    readonly Dictionary<ushort, Pet> _byEntity = new();
    readonly Dictionary<Animal, Pet> _byAnimal = new();
    readonly List<Pet> _all = new(); // every pet, for the per-tick walks (no enumerator or copy per tick)
    readonly List<Pet> _tick = new(); // the snapshot Update walks: dropping poop can despawn entities, which changes the collections above
    int _nextHit;

    public PetKeeper(PetsFile cfg, AnimalsFile animals, WorldEntityManager entities, int seed)
    {
        _cfg = cfg; _animals = animals; _entities = entities;
        _rng = new Random(seed);
        _poops = new PoopBook(cfg.PoopMaxPerGroup);
        entities.Despawned += OnDespawned;
    }

    /// <summary>Error log for pets or poop that could not be spawned (the entity cap). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A pet was shot dead: the pet and the player who did it.</summary>
    public event Action<Animal, ushort>? Killed;

    /// <summary>Pets in the world, dead ones lying included.</summary>
    public int Count => _byHit.Count;
    public int AliveCount { get { var n = 0; foreach (var p in _all) if (p.Animal.Alive) n++; return n; } }
    public IEnumerable<Animal> Pets => _all.Select(p => p.Animal);
    /// <summary>The property group a pet belongs to, or null for an animal that is not a pet.</summary>
    public string? GroupOf(Animal pet) => _byAnimal.TryGetValue(pet, out var p) ? p.Group : null;
    public int PoopCount(string group) => _poops.Count(group);
    public int PoopTotal => _poops.Total;

    /// <summary>
    /// Gives the property groups of <paramref name="pens"/> their pets: each group in a random order keeps pets with the chance of pets.json, as many as it says
    /// (but no more than the map's cap), each of a species picked by weight and standing at a random point of the pen that has ground. A group whose pen has
    /// no such point gets none. Before that, the pinned pets of pets.json are rolled into their named groups (an unknown group is logged). Returns how many pets were spawned. Call when a round starts, after the match cleared its entities.
    /// </summary>
    public int Populate(IReadOnlyDictionary<string, Pen> pens, IAnimalWorld ground)
    {
        Clear();
        if (_cfg.MaxPets <= 0) return 0;
        var worlds = new Dictionary<string, PennedWorld>();
        PennedWorld World(string group) => worlds.TryGetValue(group, out var w) ? w : worlds[group] = new PennedWorld(ground, pens[group]);
        // pinned pets first: the cap must not starve them
        foreach (var pin in _cfg.Pinned)
        {
            if (!pens.TryGetValue(pin.Group, out var pen)) { Log?.Invoke($"pets: pinned {pin.Species}: no property group {pin.Group} in this map"); continue; }
            var species = _animals.IndexOf(pin.Species);
            var variant = pin.VariantIn(_animals.Animals[species]);
            for (var k = 0; k < pin.Count && _byHit.Count < _cfg.MaxPets; k++)
            {
                if (!(_rng.NextDouble() < pin.Chance)) continue;
                var r = SpawnOne(pin.Group, pen, World(pin.Group), species, variant);
                if (r == Spawned.EntitiesFull) return _byHit.Count; // the world refuses every spawn now: stop, logged once
                if (r == Spawned.NoGround) break; // no ground in this pen: the rest of this pin has none either
            }
        }
        if (!(_cfg.GroupChance > 0) || _cfg.Species.Count == 0) return _byHit.Count;
        var order = pens.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (var i = order.Count - 1; i > 0; i--) { var j = _rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        foreach (var group in order)
        {
            if (_byHit.Count >= _cfg.MaxPets) break;
            if (_rng.NextDouble() >= _cfg.GroupChance) continue;
            var n = Math.Min(_rng.Next(_cfg.PetsPerGroupMin, _cfg.PetsPerGroupMax + 1), _cfg.MaxPets - _byHit.Count);
            for (var k = 0; k < n; k++)
            {
                var r = SpawnOne(group, pens[group], World(group));
                if (r == Spawned.EntitiesFull) return _byHit.Count;
                if (r == Spawned.NoGround) break; // this group only
            }
        }
        return _byHit.Count;
    }

    Spawned SpawnOne(string group, Pen pen, IAnimalWorld world, int? pinned = null, int? pinnedVariant = null)
    {
        Vector2? home = null;
        for (var attempt = 0; attempt < 30 && home == null; attempt++)
            if (pen.RandomPoint(_rng) is { } p && world.GroundY(p.X, p.Y) != null) home = p;
        if (home is not { } h) return Spawned.NoGround;
        var species = pinned ?? _cfg.Pick(_animals, _rng.NextDouble());
        var animal = new Animal(_animals, species, new Vector3(h.X, 0, h.Y), (float)_rng.NextDouble() * MathF.Tau, world, _rng.Next(), pinnedVariant ?? _rng.Next(_animals.Animals[species].VariantCount));
        if (_entities.Spawn(animal) is not { } entity) { Log?.Invoke("pets: the world is full of entities; no more pets"); return Spawned.EntitiesFull; }
        var hit = NextHitId();
        var pet = new Pet { Animal = animal, Entity = entity, HitId = hit, Group = group, Poop = new PoopSchedule(_cfg, new Random(_rng.Next())) };
        animal.Died += (a, by) => Killed?.Invoke(a, by);
        _byHit[hit] = pet;
        _byEntity[entity] = pet;
        _byAnimal[animal] = pet;
        _all.Add(pet);
        return Spawned.Yes;
    }

    ushort NextHitId()
    {
        do { _nextHit = (_nextHit + 1) % PetRules.HitIdCount; } while (_byHit.ContainsKey((ushort)(PetRules.FirstHitId + _nextHit)));
        return (ushort)(PetRules.FirstHitId + _nextHit);
    }

    /// <summary>Once a tick, after the entities ticked: pets whose time has come drop their poop.</summary>
    public void Update(float dt)
    {
        _tick.Clear();
        _tick.AddRange(_all);
        foreach (var pet in _tick)
            if (!pet.Gone && pet.Poop.Tick(dt, pet.Animal.Alive, pet.Animal.State)) Drop(pet);
        _tick.Clear();
    }

    void Drop(Pet pet)
    {
        var poop = new Poop(pet.Animal.Position, pet.Animal.Yaw, _cfg.PoopLifetimeSeconds);
        if (_entities.Spawn(poop) is not { } id) { Log?.Invoke("pets: the world is full of entities; no poop"); return; }
        foreach (var old in _poops.Add(pet.Group, id)) _entities.Despawn(old, WorldDespawnReason.Removed);
    }

    void OnDespawned(WorldDespawned d)
    {
        _poops.Forget(d.Id);
        if (!_byEntity.Remove(d.Id, out var pet)) return;
        pet.Gone = true;
        _byHit.Remove(pet.HitId);
        _byAnimal.Remove(pet.Animal);
        _all.Remove(pet); // at most 100 pets, and only when one is despawned
    }

    /// <summary>Adds the living pets as hit targets.</summary>
    public void AddTargets(List<HitTarget> into)
    {
        foreach (var p in _all)
            if (p.Animal.Alive) into.Add(PetRules.TargetOf(p.HitId, p.Animal));
    }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        if (!_byHit.TryGetValue(hitId, out var pet) || !pet.Animal.Alive || !(amount > 0)) return DamageResult.Ignored;
        pet.Animal.OnDamage(amount, attacker);
        if (pet.Animal.Ended is { } reason) _entities.Despawn(pet.Entity, reason); // a one-hit pet leaves no corpse: it is gone before the next tick
        return pet.Animal.Alive ? DamageResult.Damaged : DamageResult.Killed;
    }

    /// <summary>Forgets every pet and poop (the match cleared its entities, or the round is over).</summary>
    public void Clear()
    {
        foreach (var p in _all) p.Gone = true;
        _byHit.Clear(); _byEntity.Clear(); _byAnimal.Clear(); _all.Clear(); _poops.Clear();
    }
}

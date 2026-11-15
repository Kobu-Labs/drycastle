using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Wildlife rules shared by the server and the match: the hit ids a wild animal has as a shot target. A species stands in the zones of its own kind (the zone kind is the animals.json id, ADR 0022).</summary>
public static class WildlifeRules
{
    /// <summary>
    /// The id a wild animal has as a hit target: the 0x400 ids between the turret records (0x7000 + a vehicle index) and the pets (<see cref="PetRules.FirstHitId"/>).
    /// Not the entity id.
    /// </summary>
    public const ushort FirstHitId = 0x7400, HitIdCount = 0x400;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;
}

/// <summary>
/// The wildlife of a match: <see cref="Populate"/> puts each species of wildlife.json at random candidate points of its habitat (the zones of its
/// kind, baked into points by the build), as <see cref="Animal"/> world entities that wander about their spawn point and run from
/// players and vehicles. A wild animal can be shot dead (it lies for corpse_seconds unless its species is one-hit; nothing scores and nothing replaces it
/// this round). Godot-free; the server supplies the ground and the threats and calls <see cref="AddTargets"/> and <see cref="Hit"/>.
/// </summary>
public sealed class WildlifeKeeper : IAnimalHits
{
    sealed class Wild
    {
        public required Animal Animal;
        public required ushort Entity;
        public required ushort HitId;
        public bool Gone;
    }

    readonly WildlifeFile _cfg;
    readonly AnimalsFile _animals;
    readonly WorldEntityManager _entities;
    readonly Random _rng;
    readonly Dictionary<ushort, Wild> _byHit = new();
    readonly Dictionary<ushort, Wild> _byEntity = new();
    readonly List<Wild> _all = new();
    readonly List<Herd> _herds = new();
    readonly List<Flock> _flocks = new();
    int _nextHit;

    public WildlifeKeeper(WildlifeFile cfg, AnimalsFile animals, WorldEntityManager entities, int seed)
    {
        _cfg = cfg; _animals = animals; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    /// <summary>Error log for animals that could not be spawned (the entity cap). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A wild animal was shot dead: the animal and the player who did it.</summary>
    public event Action<Animal, ushort>? Killed;

    /// <summary>Wild animals in the world, dead ones lying included.</summary>
    public int Count => _byHit.Count;
    public int AliveCount { get { var n = 0; foreach (var w in _all) if (w.Animal.Alive) n++; return n; } }
    public IEnumerable<Animal> Animals => _all.Select(w => w.Animal);
    public IReadOnlyList<Herd> Herds => _herds;
    public IReadOnlyList<Flock> Flocks => _flocks;

    /// <summary>
    /// Spawns the wildlife of wildlife.json: per species its count, each at a random point of <paramref name="habitat"/> (species id = zone kind to candidate points,
    /// Godot ground axes) that has ground, up to the map's cap. A species with no point in the map gets none (logged). Returns how many were spawned.
    /// Call when a round starts, after the match cleared its entities.
    /// </summary>
    public int Populate(Func<string, IReadOnlyList<Vector3>> habitat, IAnimalWorld ground)
    {
        Clear();
        var n = PopulateAnimals(habitat, ground);
        PopulateFlocks(habitat, ground);
        return n;
    }

    /// <summary>The bird flocks of wildlife.json: per kind its flocks, each at a random habitat point, a random number of birds. A kind with no point gets none (logged).</summary>
    void PopulateFlocks(Func<string, IReadOnlyList<Vector3>> habitat, IAnimalWorld ground)
    {
        for (var ki = 0; ki < _cfg.Flocks.Count; ki++)
        {
            var kind = _cfg.Flocks[ki];
            var points = habitat(kind.Id);
            if (points.Count == 0) { Log?.Invoke($"wildlife: no habitat point for the {kind.Id} flocks: none spawned"); continue; }
            for (var k = 0; k < kind.Flocks; k++)
            {
                if (PickPoint(points, ground) is not { } at) break;
                var flock = new Flock(kind, ki, _rng.Next(kind.BirdsMin, kind.BirdsMax + 1), new Vector3(at.X, ground.GroundY(at.X, at.Z) ?? at.Y, at.Z), ground, _rng.Next());
                if (_entities.Spawn(flock) is null) { Log?.Invoke("wildlife: the world is full of entities; no more flocks"); return; }
                _flocks.Add(flock);
            }
        }
    }

    int PopulateAnimals(Func<string, IReadOnlyList<Vector3>> habitat, IAnimalWorld ground)
    {
        foreach (var sp in _cfg.Species)
        {
            var species = _animals.IndexOf(sp.Id);
            if (species < 0) continue;
            var points = habitat(sp.Id);
            if (points.Count == 0) { Log?.Invoke($"wildlife: no habitat point for {sp.Id}: none spawned"); continue; }
            var left = sp.Count;
            while (left > 0 && _byHit.Count < _cfg.MaxAnimals)
            {
                var r = sp.Herd is { } rules ? SpawnHerd(species, Math.Min(left, _rng.Next(rules.Min, rules.Max + 1)), rules, points, ground, out var made) : SpawnSingle(species, points, ground, out made);
                if (r == Spawned.EntitiesFull) return _byHit.Count;
                if (r == Spawned.NoGround) break; // this species has no usable point: the rest has none either
                left -= made;
            }
        }
        return _byHit.Count;
    }

    Spawned SpawnSingle(int species, IReadOnlyList<Vector3> points, IAnimalWorld ground, out int made)
    {
        made = 0;
        if (PickPoint(points, ground) is not { } at) return Spawned.NoGround;
        var r = SpawnAt(species, at, ground, null);
        if (r == Spawned.Yes) made = 1;
        return r;
    }

    /// <summary>A herd of <paramref name="size"/> animals: one habitat point is its home, each member starts at a spot with ground within the spread of it.</summary>
    Spawned SpawnHerd(int species, int size, HerdRules rules, IReadOnlyList<Vector3> points, IAnimalWorld ground, out int made)
    {
        made = 0;
        if (PickPoint(points, ground) is not { } home) return Spawned.NoGround;
        var herd = new Herd(home, rules.SpreadM, rules.RangeM, rules.MoveMinSeconds, rules.MoveMaxSeconds, _rng.Next());
        for (var k = 0; k < size && _byHit.Count < _cfg.MaxAnimals; k++)
        {
            var at = home;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var angle = (float)_rng.NextDouble() * MathF.Tau;
                var reach = (float)Math.Sqrt(_rng.NextDouble()) * rules.SpreadM;
                var p = new Vector3(home.X + MathF.Cos(angle) * reach, 0, home.Z + MathF.Sin(angle) * reach);
                if (ground.GroundY(p.X, p.Z) != null) { at = p; break; }
            }
            var r = SpawnAt(species, at, ground, herd);
            if (r == Spawned.EntitiesFull) { if (herd.Members.Count > 0) _herds.Add(herd); return r; }
            made++;
        }
        if (herd.Members.Count > 0) _herds.Add(herd);
        return Spawned.Yes;
    }

    /// <summary>A random point of the habitat that has ground, or null.</summary>
    Vector3? PickPoint(IReadOnlyList<Vector3> points, IAnimalWorld ground)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var p = points[_rng.Next(points.Count)];
            if (ground.GroundY(p.X, p.Z) != null) return p;
        }
        return null;
    }

    enum Spawned { Yes, NoGround, EntitiesFull }

    Spawned SpawnAt(int species, Vector3 h, IAnimalWorld ground, Herd? herd)
    {
        var def = _animals.Animals[species];
        var animal = new Animal(_animals, species, new Vector3(h.X, 0, h.Z), (float)_rng.NextDouble() * MathF.Tau, ground, _rng.Next(), _rng.Next(def.VariantCount));
        if (_entities.Spawn(animal) is not { } entity) { Log?.Invoke("wildlife: the world is full of entities; no more wildlife"); return Spawned.EntitiesFull; }
        herd?.Add(animal);
        var hit = NextHitId();
        var wild = new Wild { Animal = animal, Entity = entity, HitId = hit };
        animal.Died += (a, by) => Killed?.Invoke(a, by);
        _byHit[hit] = wild;
        _byEntity[entity] = wild;
        _all.Add(wild);
        return Spawned.Yes;
    }

    ushort NextHitId()
    {
        do { _nextHit = (_nextHit + 1) % WildlifeRules.HitIdCount; } while (_byHit.ContainsKey((ushort)(WildlifeRules.FirstHitId + _nextHit)));
        return (ushort)(WildlifeRules.FirstHitId + _nextHit);
    }

    void OnDespawned(WorldDespawned d)
    {
        if (!_byEntity.Remove(d.Id, out var wild)) return;
        wild.Gone = true;
        _byHit.Remove(wild.HitId);
        _all.Remove(wild); // at most 100 animals, and only when one is despawned
    }

    /// <summary>Once a tick, after the entities ticked: the herds drift their anchors.</summary>
    public void Update(float dt, IAnimalWorld ground)
    {
        foreach (var h in _herds) h.Update(dt, ground);
    }

    /// <summary>
    /// A wild animal's hit volume. A bullet meets its body box (its variant's, else the species': the length along its facing direction, so a deer is hit
    /// from its nose to its tail, and nowhere is a head hit); a blast or a knife measures to the player capsule scaled to the box's height, as before.
    /// </summary>
    public static HitTarget TargetOf(ushort hitId, Animal animal)
    {
        var box = animal.HitBox;
        return new(hitId, animal.Position, Stance.Stand, animal.Yaw, HeadGuarded: true, Scale: box[1] / PlayerHitBox.StandHeight,
            Box: new Vector3(box[0], box[1], box[2]), BoxAhead: animal.Def.HitBoxAhead);
    }

    /// <summary>The animal with a hit id, null when it is gone.</summary>
    public Animal? AnimalOf(ushort hitId) => _byHit.TryGetValue(hitId, out var w) ? w.Animal : null;

    /// <summary>Adds the living wild animals as hit targets.</summary>
    public void AddTargets(List<HitTarget> into)
    {
        foreach (var w in _all)
            if (w.Animal.Alive) into.Add(TargetOf(w.HitId, w.Animal));
    }

    /// <summary>The living wild animals with their hit ids, into <paramref name="into"/> (a copy: a hit may remove one).</summary>
    public void Living(List<(ushort HitId, Animal Animal)> into)
    {
        foreach (var w in _all)
            if (w.Animal.Alive) into.Add((w.HitId, w.Animal));
    }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        if (!_byHit.TryGetValue(hitId, out var wild) || !wild.Animal.Alive || !(amount > 0)) return DamageResult.Ignored;
        wild.Animal.OnDamage(amount, attacker);
        if (wild.Animal.Ended is { } reason) _entities.Despawn(wild.Entity, reason); // a one-hit species leaves no corpse: it is gone before the next tick
        return wild.Animal.Alive ? DamageResult.Damaged : DamageResult.Killed;
    }

    /// <summary>Forgets every wild animal (the match cleared its entities, or the round is over).</summary>
    public void Clear()
    {
        foreach (var w in _all) w.Gone = true;
        _byHit.Clear(); _byEntity.Clear(); _all.Clear(); _herds.Clear(); _flocks.Clear();
    }
}

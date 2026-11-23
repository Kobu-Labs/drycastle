using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Wildlife;

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
/// this round), except a <see cref="RareSpecies"/> (a Unicorn): its slot waits <see cref="RareSpecies.RespawnSeconds"/> after the kill (a <see cref="Countdown"/>, ticked by <see cref="Update"/>) and then
/// puts a new one at a new random point of the map with ground that is far enough from every building. Godot-free; the server supplies the ground and the threats and calls <see cref="AddTargets"/> and <see cref="Hit"/>.
/// </summary>
public sealed class WildlifeKeeper : IAnimalHits
{
    sealed class Wild
    {
        public required Animal Animal;
        public required ushort Entity;
        public required ushort HitId;
        public bool Gone;
        /// <summary>The rare-species slot this animal fills while alive; null for an ordinary animal, or once it died.</summary>
        public RareSlot? Slot;
    }

    /// <summary>One of the <see cref="RareSpecies.Count"/> places of a rare species: an animal, or (none) a clock to the next try at a spawn point.</summary>
    sealed class RareSlot
    {
        public required RareSpecies Rule;
        public required int Species;
        public Wild? Animal;
        public Countdown Respawn;
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
    readonly List<RareSlot> _rare = new();
    readonly PhoenixSchedule? _schedule;
    readonly List<MineralDrop> _drops = new();
    int _nextHit;
    AnimalCrowd? _crowd;

    public WildlifeKeeper(WildlifeFile cfg, AnimalsFile animals, WorldEntityManager entities, int seed)
    {
        _cfg = cfg; _animals = animals; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
        if (cfg.Phoenix is { } phoenix) _schedule = new PhoenixSchedule(phoenix);
    }

    /// <summary>The night counter of the Phoenix (null: wildlife.json has none). Counts the keeper's whole life, not one round (<see cref="PhoenixSchedule"/>).</summary>
    public PhoenixSchedule? Nights => _schedule;

    /// <summary>True from <see cref="Populate"/> to <see cref="Clear"/>: a round is running with wildlife on the map. A Phoenix spawns only then (the nights are counted either way).</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// A Phoenix was hit and starts to fall: the world entity id, where it was hit, the crash site and the fall's seconds. The server broadcasts it (<see cref="EventKind.PhoenixFall"/>, <see cref="PhoenixFallCodec"/>).
    /// </summary>
    public event Action<ushort, Vector3, Vector3, float>? PhoenixFell;

    /// <summary>A dead Phoenix left its mineral where it burned (it is also in <see cref="Drops"/>, until the round ends): the drop. Whoever runs the pickups (the Mineral Log's world nodes) takes it from here.</summary>
    public event Action<MineralDrop>? MineralDropped;

    /// <summary>The minerals creatures left in the world this round, none taken (the keeper has no pickup of its own: see <see cref="MineralDropped"/>).</summary>
    public IReadOnlyList<MineralDrop> Drops => _drops;

    /// <summary>Phoenixes in the world that are not dead (flying, falling, crashed): at most one is due at a time.</summary>
    public int PhoenixAlive { get { var n = 0; foreach (var w in _all) if (w.Animal.IsPhoenix && w.Animal.Alive) n++; return n; } }

    /// <summary>Every animal of the match, pets included: the wild ones are put into it when they spawn and out when they end, so an animal runs into one of another herd or a pet (ADR 0028). Null: none do.</summary>
    public AnimalCrowd? Crowd { get => _crowd; set => _crowd = value; }

    /// <summary>Error log for animals that could not be spawned (the entity cap). Null means silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A wild animal was shot dead: the animal and the player who did it.</summary>
    public event Action<Animal, ushort>? Killed;

    /// <summary>Wild animals in the world, dead ones lying included.</summary>
    public int Count => _byHit.Count;
    public int AliveCount { get { var n = 0; foreach (var w in _all) if (w.Animal.Alive) n++; return n; } }
    public IEnumerable<Animal> Animals => _all.Select(w => w.Animal);
    /// <summary>Rare-species slots with no animal alive: each is counting down to a new one.</summary>
    public int RareWaiting { get { var n = 0; foreach (var r in _rare) if (r.Animal == null) n++; return n; } }
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
        PopulateRare(ground);
        var n = PopulateAnimals(habitat, ground);
        PopulateFlocks(habitat, ground);
        Active = true;
        return n;
    }

    /// <summary>The first animal of every rare slot; one that finds no point waits a respawn period and tries again (<see cref="Update"/>).</summary>
    void PopulateRare(IAnimalWorld ground)
    {
        foreach (var rule in _cfg.Rare)
        {
            var species = _animals.IndexOf(rule.Id);
            if (species < 0) continue;
            for (var k = 0; k < rule.Count; k++)
            {
                var slot = new RareSlot { Rule = rule, Species = species };
                _rare.Add(slot);
                if (!TrySpawnRare(slot, ground)) slot.Respawn.Start(rule.RespawnSeconds);
            }
        }
    }

    /// <summary>
    /// A random point of the map with ground, room for the body and at least <paramref name="minBuildingM"/> from every building (<see cref="RareSpecies.MinBuildingM"/>); with <paramref name="awayFrom"/> also at least
    /// <paramref name="awayM"/> (flat) from it (where a vanishing animal reappears). Null when <see cref="MaxTries"/> tries find none.
    /// </summary>
    Vector3? PickRarePoint(int species, float minBuildingM, IAnimalWorld ground, Vector3? awayFrom = null, float awayM = 0)
    {
        if (ground.MapBounds is not { } b || !float.IsFinite(b.MinX + b.MaxX + b.MinZ + b.MaxZ) || !(b.MaxX > b.MinX) || !(b.MaxZ > b.MinZ)) return null;
        var radius = _animals.Animals[species].BodyRadiusOf(0);
        var min = minBuildingM;
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var x = b.MinX + (float)_rng.NextDouble() * (b.MaxX - b.MinX);
            var z = b.MinZ + (float)_rng.NextDouble() * (b.MaxZ - b.MinZ);
            if (awayFrom is { } from && Flat(x, z, from) < awayM) continue;
            if (ground.GroundY(x, z) == null || !(ground.NearestBuildingM(x, z, min) >= min) || !ground.Fits(x, z, radius)) continue; // cheapest test first
            return new Vector3(x, 0, z);
        }
        return null;
    }

    static float Flat(float x, float z, Vector3 p) => MathF.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z));

    /// <summary>Tries a rare slot's animal at a new point. False (logged) when there is no point, no room in <see cref="WildlifeFile.MaxAnimals"/> or the world is full.</summary>
    bool TrySpawnRare(RareSlot slot, IAnimalWorld ground)
    {
        var id = slot.Rule.Id;
        if (_byHit.Count >= _cfg.MaxAnimals) { Log?.Invoke($"wildlife: no room for the rare {id} (max_animals {_cfg.MaxAnimals}): trying again in {slot.Rule.RespawnSeconds:0} s"); return false; }
        if (PickRarePoint(slot.Species, slot.Rule.MinBuildingM, ground) is not { } at) { Log?.Invoke($"wildlife: no point for the rare {id} ({MaxTries} tries): trying again in {slot.Rule.RespawnSeconds:0} s"); return false; }
        if (SpawnAt(slot.Species, at, ground, null) != Spawned.Yes) return false;
        var wild = _all[^1];
        wild.Slot = slot;
        slot.Animal = wild;
        return true;
    }

    /// <summary>
    /// An extra animal of a rare species at <paramref name="at"/> (the admin's "/unicorn"): no slot, so it does not come back when killed and does not change the slot's countdown.
    /// False when the species is unknown, the point has no ground or room, or the map is full.
    /// </summary>
    public bool SpawnExtra(string id, Vector3 at, IAnimalWorld ground)
    {
        var species = _animals.IndexOf(id);
        if (species < 0 || _byHit.Count >= _cfg.MaxAnimals) return false;
        var radius = _animals.Animals[species].BodyRadiusOf(0);
        if (ground.GroundY(at.X, at.Z) == null || !ground.Fits(at.X, at.Z, radius)) return false;
        return SpawnAt(species, at, ground, null) == Spawned.Yes;
    }

    /// <summary>The animal of a rare slot died or went: the slot counts down to a new one.</summary>
    static void FreeSlot(Wild wild)
    {
        if (wild.Slot is not { } slot) return;
        wild.Slot = null;
        if (slot.Animal != wild) return;
        slot.Animal = null;
        slot.Respawn.Start(slot.Rule.RespawnSeconds);
    }

    /// <summary>Tries at a random point before a rare spawn gives up until the next countdown.</summary>
    public const int MaxTries = 200;

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
                if (PickPoint(points, ground, 0) is not { } at) break; // birds fly: only the ground
                var flock = new Flock(kind, ki, _rng.Next(kind.BirdsMin, kind.BirdsMax + 1), new Vector3(at.X, ground.GroundY(at.X, at.Z) ?? at.Y, at.Z), ground, _rng.Next());
                if (_entities.Spawn(flock) is null) { Log?.Invoke("wildlife: the world is full of entities; no more flocks"); return; }
                _flocks.Add(flock);
            }
        }
    }

    int PopulateAnimals(Func<string, IReadOnlyList<Vector3>> habitat, IAnimalWorld ground)
    {
        foreach (var mix in _cfg.Herds)
        {
            var points = habitat(mix.HabitatKind);
            if (points.Count == 0) { Log?.Invoke($"wildlife: no habitat point for the {mix.Id} herds ({mix.HabitatKind}): none spawned"); continue; }
            var left = mix.Count;
            while (left > 0 && _byHit.Count < _cfg.MaxAnimals)
            {
                var members = mix.Roll(Math.Min(left, _rng.Next(mix.Size.Min, mix.Size.Max + 1)), _rng).Select(id => _animals.IndexOf(id)).Where(i => i >= 0).ToList();
                if (members.Count == 0) break;
                var r = SpawnHerd(members, mix.SpreadM, mix.RangeM, mix.MoveMinSeconds, mix.MoveMaxSeconds, points, ground, out var made);
                if (r == Spawned.EntitiesFull) return _byHit.Count;
                if (r == Spawned.NoGround) break;
                left -= made;
            }
        }
        foreach (var sp in _cfg.Species)
        {
            var species = _animals.IndexOf(sp.Id);
            if (species < 0) continue;
            var points = habitat(sp.Id);
            if (points.Count == 0) { Log?.Invoke($"wildlife: no habitat point for {sp.Id}: none spawned"); continue; }
            var left = sp.Count;
            while (left > 0 && _byHit.Count < _cfg.MaxAnimals)
            {
                var r = sp.Herd is { } rules
                    ? SpawnHerd(Enumerable.Repeat(species, Math.Min(left, _rng.Next(rules.Min, rules.Max + 1))).ToList(), rules.SpreadM, rules.RangeM, rules.MoveMinSeconds, rules.MoveMaxSeconds, points, ground, out var made)
                    : SpawnSingle(species, points, ground, out made);
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
        if (PickPoint(points, ground, _animals.Animals[species].BodyRadiusOf(0)) is not { } at) return Spawned.NoGround;
        var r = SpawnAt(species, at, ground, null);
        if (r == Spawned.Yes) made = 1;
        return r;
    }

    /// <summary>A herd of <paramref name="members"/> (animals.json indices, one per animal, any mix of species): one habitat point is its home, each member starts at a spot with ground within the spread of it.</summary>
    Spawned SpawnHerd(IReadOnlyList<int> members, float spreadM, float rangeM, float moveMin, float moveMax, IReadOnlyList<Vector3> points, IAnimalWorld ground, out int made)
    {
        made = 0;
        var radius = members.Max(i => _animals.Animals[i].BodyRadiusOf(0)); // the biggest body of the herd needs room at home and at each drift target
        if (PickPoint(points, ground, radius) is not { } home) return Spawned.NoGround;
        var herd = new Herd(home, spreadM, rangeM, moveMin, moveMax, _rng.Next(), radius);
        for (var k = 0; k < members.Count && _byHit.Count < _cfg.MaxAnimals; k++)
        {
            var species = members[k];
            var at = home;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var angle = (float)_rng.NextDouble() * MathF.Tau;
                var reach = (float)Math.Sqrt(_rng.NextDouble()) * spreadM;
                var p = new Vector3(home.X + MathF.Cos(angle) * reach, 0, home.Z + MathF.Sin(angle) * reach);
                if (ground.GroundY(p.X, p.Z) != null && ground.Fits(p.X, p.Z, radius)) { at = p; break; } // not inside a building
            }
            var r = SpawnAt(species, at, ground, herd);
            if (r == Spawned.EntitiesFull) { if (herd.Members.Count > 0) _herds.Add(herd); return r; }
            made++;
        }
        if (herd.Members.Count > 0) _herds.Add(herd);
        return Spawned.Yes;
    }

    /// <summary>A random point of the habitat that has ground and room for a body of <paramref name="radius"/> (not inside a building, not on a parked vehicle); 0: the ground alone. Null when 30 tries find none.</summary>
    Vector3? PickPoint(IReadOnlyList<Vector3> points, IAnimalWorld ground, float radius)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var p = points[_rng.Next(points.Count)];
            if (ground.GroundY(p.X, p.Z) != null && (radius <= 0 || ground.Fits(p.X, p.Z, radius))) return p;
        }
        return null;
    }

    enum Spawned { Yes, NoGround, EntitiesFull }

    Spawned SpawnAt(int species, Vector3 h, IAnimalWorld ground, Herd? herd, PhoenixSpawn? sky = null)
    {
        var def = _animals.Animals[species];
        var yaw = (float)_rng.NextDouble() * MathF.Tau;
        var seed = _rng.Next();
        var variant = _rng.Next(def.VariantCount);
        var animal = new Animal(_animals, species, new Vector3(h.X, 0, h.Z), yaw, ground, seed, variant, AnimalRules.RollSize(_rng), sky);
        if (_entities.Spawn(animal) is not { } entity) { Log?.Invoke("wildlife: the world is full of entities; no more wildlife"); return Spawned.EntitiesFull; }
        herd?.Add(animal);
        _crowd?.Add(animal);
        var hit = NextHitId();
        var wild = new Wild { Animal = animal, Entity = entity, HitId = hit };
        animal.Died += (a, by) => { FreeSlot(wild); Killed?.Invoke(a, by); };
        if (animal.Vanishes) animal.Vanished += a => OnVanished(wild, ground);
        if (animal.IsPhoenix)
        {
            animal.Fell += (_, from, site, seconds) => PhoenixFell?.Invoke(wild.Entity, from, site, seconds);
            animal.Dropped += (a, mineral) => { var drop = new MineralDrop(mineral, a.Position, wild.Entity); _drops.Add(drop); MineralDropped?.Invoke(drop); };
        }
        _byHit[hit] = wild;
        _byEntity[entity] = wild;
        _all.Add(wild);
        return Spawned.Yes;
    }

    /// <summary>A vanishing animal's clock ran out: it reappears at a point picked as a rare one is (ground, room, far from buildings: its slot's rule, or no building distance for a species with no slot) at least vanish_min_m from where it was; none found, it stays.</summary>
    void OnVanished(Wild wild, IAnimalWorld ground)
    {
        var a = wild.Animal;
        if (wild.Gone || PickRarePoint(a.Species, wild.Slot?.Rule.MinBuildingM ?? 0, ground, a.Position, a.Def.VanishMinM) is not { } at)
        {
            if (!wild.Gone) Log?.Invoke($"wildlife: no point for the vanished {a.Def.Id} ({MaxTries} tries): it stays");
            return;
        }
        a.Teleport(at, (float)_rng.NextDouble() * MathF.Tau);
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
        FreeSlot(wild);
        _crowd?.Remove(wild.Animal);
        _byHit.Remove(wild.HitId);
        _all.Remove(wild); // at most 100 animals, and only when one is despawned
    }

    /// <summary>Once a tick, after the entities ticked: the herds drift their anchors, and the rare slots count down to their next animal.</summary>
    public void Update(float dt, IAnimalWorld ground)
    {
        foreach (var h in _herds) h.Update(dt, ground);
        foreach (var slot in _rare)
            if (slot.Animal == null && slot.Respawn.Tick(dt) && !TrySpawnRare(slot, ground)) slot.Respawn.Start(slot.Rule.RespawnSeconds);
    }

    // ---- the Phoenix ----

    /// <summary>
    /// Once a tick: the hour of the server's day clock goes to the night counter. A due Phoenix (every <see cref="PhoenixRules.NightsBetween"/>th night, at <see cref="PhoenixRules.SpawnHour"/>) is spawned while a round is
    /// <see cref="Active"/> and none is alive (<see cref="PhoenixAlive"/>: a Crashed one from last time is still there: this night has none); at <see cref="PhoenixRules.DawnHour"/> a Phoenix still Flying flies off.
    /// </summary>
    public void UpdateSky(double hour, IAnimalWorld ground)
    {
        if (_schedule == null) return;
        var tick = _schedule.Advance(hour);
        if (!Active) return;
        if (tick.Dawn)
            foreach (var w in _all.ToArray())
                if (w.Animal.IsPhoenix && w.Animal.FlyOff()) _entities.Despawn(w.Entity, WorldDespawnReason.Expired);
        if (tick.Due && PhoenixAlive == 0) SpawnPhoenix(ground);
    }

    /// <summary>
    /// A Phoenix now, flying a circle that fits the map (the admin's "/phoenix" and <see cref="UpdateSky"/>); the schedule is not touched. False (logged) when wildlife.json has none, the world has no map box or ground
    /// for the circle's centre, or the map is full.
    /// </summary>
    public bool SpawnPhoenix(IAnimalWorld ground)
    {
        if (_cfg.Phoenix is not { } rules) return false;
        var species = _animals.IndexOf(rules.Id);
        if (species < 0) return false;
        if (_byHit.Count >= _cfg.MaxAnimals) { Log?.Invoke($"wildlife: no room for the {rules.Id} (max_animals {_cfg.MaxAnimals})"); return false; }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (ground.MapBounds is not { } bounds || PhoenixFlight.Circle(bounds, rules.PatrolRadiusM, _rng) is not { } c) { Log?.Invoke($"wildlife: no map box for the {rules.Id}"); return false; }
            if (ground.GroundY(c.X, c.Z) == null) continue; // its height is the ground's at the centre
            return SpawnAt(species, new Vector3(c.X, 0, c.Z), ground, null, new PhoenixSpawn(rules, new Vector3(c.X, 0, c.Z), c.Radius)) == Spawned.Yes;
        }
        Log?.Invoke($"wildlife: no ground at the centre of a patrol for the {rules.Id} (30 tries)");
        return false;
    }

    /// <summary>The hit shapes of the species and their looks (animal_shapes.json); a look with none, and an animal knocked down or thrown, is tested against its body box.</summary>
    public AnimalShapes? Shapes { get; set; }

    /// <summary>The pose a client draws an animal in for its state; null when it lies or flies (no shape for that).</summary>
    public static AnimalPose? PoseOf(AnimalState state) => state switch
    {
        AnimalState.Eat => AnimalPose.Eat,
        AnimalState.Walk => AnimalPose.Walk,
        AnimalState.Flee => AnimalPose.Run,
        AnimalState.Thrown or AnimalState.Down or AnimalState.Dead or AnimalState.Carried or AnimalState.Flying or AnimalState.Falling or AnimalState.Crashed or AnimalState.Burning => null, // a Phoenix has no shape: its body box
        _ => AnimalPose.Idle, // Idle, Sit, Bark and Vanish (it stands still; it is no hit target anyway)
    };

    /// <summary>
    /// A wild animal's hit volume. A bullet meets its hit shape (the animal's outline in the pose it is in, <paramref name="backSeconds"/> ago: what
    /// the shooter's screen showed, so a grazing deer's head is where it was seen), or without one its body box (the length along its facing
    /// direction); nowhere is a headshot. A blast or a knife measures to the player capsule scaled to the box's height, as before.
    /// </summary>
    public HitTarget TargetOf(ushort hitId, Animal animal, float backSeconds = 0)
    {
        var box = animal.HitBox;
        var target = new HitTarget(hitId, animal.Feet, Stance.Stand, animal.Yaw, HeadGuarded: true, Scale: box[1] / PlayerHitBox.StandHeight,
            Box: new Vector3(box[0], box[1], box[2]), BoxAhead: animal.Def.HitBoxAhead * animal.Size, ShapeScale: animal.Size);
        // each look of a species (a variant) has its own shapes, baked from its own model
        if (Shapes == null || PoseOf(animal.State) is not { } pose) return target;
        return Shapes.At(animal.Def.ShapeKey(animal.Variant), pose, MathF.Max(0, animal.StateSeconds - backSeconds)) is { } s
            ? target with { Shape = s.Shape, ShapePose = pose, ShapeFrame = s.Frame }
            : target;
    }

    /// <summary>The animal with a hit id, null when it is gone.</summary>
    public Animal? AnimalOf(ushort hitId) => _byHit.TryGetValue(hitId, out var w) ? w.Animal : null;

    /// <summary>Adds the living wild animals as hit targets, each in the pose it had <paramref name="backSeconds"/> ago (what the shooter's screen showed).</summary>
    public void AddTargets(List<HitTarget> into, float backSeconds = 0)
    {
        foreach (var w in _all)
            if (w.Animal.Targetable) into.Add(TargetOf(w.HitId, w.Animal, backSeconds)); // a vanishing one is a ghost: shots and vehicles pass through
    }

    /// <summary>The living wild animals with their hit ids, into <paramref name="into"/> (a copy: a hit may remove one). A vanishing one is not in it.</summary>
    public void Living(List<(ushort HitId, Animal Animal)> into)
    {
        foreach (var w in _all)
            if (w.Animal.Targetable) into.Add((w.HitId, w.Animal));
    }

    public DamageResult Hit(ushort hitId, ushort attacker, float amount)
    {
        if (!_byHit.TryGetValue(hitId, out var wild) || !wild.Animal.Targetable || !(amount > 0)) return DamageResult.Ignored;
        wild.Animal.OnDamage(amount, attacker);
        if (wild.Animal.Ended is { } reason) _entities.Despawn(wild.Entity, reason); // a one-hit species leaves no corpse: it is gone before the next tick
        return wild.Animal.Alive ? DamageResult.Damaged : DamageResult.Killed;
    }

    /// <summary>Forgets every wild animal (the match cleared its entities, or the round is over).</summary>
    public void Clear()
    {
        foreach (var w in _all) { w.Gone = true; _crowd?.Remove(w.Animal); }
        _byHit.Clear(); _byEntity.Clear(); _all.Clear(); _herds.Clear(); _flocks.Clear(); _rare.Clear(); _drops.Clear();
        Active = false;
    }
}

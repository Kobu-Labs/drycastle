using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The <c>loot.pet</c> sub-section of thieves.json (docs/events/thieves/05). A pet in a fenced garden is reached by a hop: the Thief walks to a point <see cref="HopOutM"/> outside the nearest fence section, jumps
/// over it in <see cref="HopSeconds"/> (an arc <see cref="HopClearM"/> above the fence's top) to a point <see cref="HopInM"/> inside, walks to the pet at <see cref="InWalkSpeedMps"/>, kneels
/// (<c>steal_seconds</c>), and hops back out with it. A pet carried is held at the Thief's right side, <see cref="CarryRightM"/> out, <see cref="CarryForwardM"/> ahead and <see cref="CarryUpM"/> above its feet.
/// </summary>
public sealed class PetLootConfig : LootKindConfig
{
    public PetLootConfig() { StealSeconds = 4f; }

    public float HopSeconds { get; set; } = 0.7f;
    public float HopOutM { get; set; } = 0.9f;
    public float HopInM { get; set; } = 0.9f;
    public float HopClearM { get; set; } = 0.5f;
    public float InWalkSpeedMps { get; set; } = 3f;
    /// <summary>Within this of the pet the Thief grabs it (the steal clock starts).</summary>
    public float GrabM { get; set; } = 0.8f;
    public float CarryRightM { get; set; } = 0.4f;
    public float CarryForwardM { get; set; } = 0.25f;
    public float CarryUpM { get; set; } = 0.8f;
    /// <summary>How many of the nearest fence sections are tried for a hop point on the navigation mesh.</summary>
    public int FenceCandidates { get; set; } = 6;
    /// <summary>A point counts as on the navigation mesh when its nearest mesh point is within this.</summary>
    public float NavSnapM { get; set; } = 1.5f;
    /// <summary>The plan for a pet (where to hop, where to walk) is kept until the pet has moved this far.</summary>
    public float PlanRecheckM { get; set; } = 2f;

    public override void Validate(string file, string kind)
    {
        base.Validate(file, kind);
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: loot.pet.{what} must be positive"); }
        Positive(HopSeconds, "hop_seconds"); Positive(HopOutM, "hop_out_m"); Positive(HopInM, "hop_in_m"); Positive(InWalkSpeedMps, "in_walk_speed_mps");
        Positive(GrabM, "grab_m"); Positive(NavSnapM, "nav_snap_m"); Positive(PlanRecheckM, "plan_recheck_m");
        if (!(HopClearM >= 0) || !float.IsFinite(HopClearM)) throw new DataFileException($"{file}: loot.pet.hop_clear_m must not be negative");
        if (FenceCandidates < 1) throw new DataFileException($"{file}: loot.pet.fence_candidates is at least 1");
    }
}

/// <summary>A hop over a fence section: where the Thief stands outside (<see cref="Out"/>, on the navigation mesh), where it lands inside (<see cref="In"/>), and the fence's height (the arc clears it).</summary>
public readonly record struct FenceHop(Vector3 Out, Vector3 In, float HeightM);

/// <summary>What the pet target needs of the map: the ground, and whether a point is on the navigation mesh (a Thief can stand there and walk on from it).</summary>
public interface IPetWorld
{
    float? GroundY(float x, float z);
    bool Walkable(Vector3 at);
}

/// <summary>
/// The pet target (docs/events/thieves/05): a target per living pet that no Thief holds. Id = the pet's entity id. The work spot is outside the fence of its garden (with a <see cref="FenceHop"/> over it) or, with no
/// fence there, the pet itself (the Thief walks the mesh in through the gate gap). A pet whose garden cannot be reached (no hop point on the mesh among the nearest sections, or no walkable point for a plain walk) is not
/// a target. <see cref="Take"/> picks it up (<see cref="Animal.Carry"/>); the Thief moves it every tick (<see cref="Carried"/>, held at its side). A killed carrier lets go (<see cref="LiesWhenDropped"/> false: the pet
/// walks home); a piece that stops being carried without a return (the Thief boarded the van, or fled) has gone into the van: its entity ends, and a destroyed van (<see cref="Return"/>) puts it back at the van,
/// walking home. A pet killed while carried raises <see cref="CarriedKilled"/> (the Thief's hands are empty).
/// </summary>
public sealed class PetSource : IStealSource
{
    const int MissedTicks = 3; // ticks without a word from the carrier before the piece counts as loaded

    sealed class Held
    {
        public PetSeed Seed;
        public int Round;
        public Vector3 Last;
        public bool Seen = true;
        public int Missed;
        public bool Loaded;   // its entity ended: it is in a van
    }

    readonly record struct Plan(Vector3 WorkSpot, FenceHop? Hop);

    sealed class Cached { public Vector3 At; public Plan? Plan; }

    readonly PetLootConfig _cfg;
    readonly PetKeeper _pets;
    readonly Func<FenceBook> _fences;
    readonly IPetWorld _world;
    readonly Dictionary<ushort, Held> _held = new();
    readonly Dictionary<ushort, Cached> _plans = new();
    readonly List<LivePet> _scratch = new();
    readonly List<(float D, int I)> _order = new();

    /// <param name="fences">The map's fences, asked for each time (the server reads the file the first time it is asked, once the map is up).</param>
    public PetSource(PetLootConfig cfg, PetKeeper pets, Func<FenceBook> fences, IPetWorld world)
    {
        _cfg = cfg; _pets = pets; _fences = fences; _world = world;
        pets.Updated += Sweep;
        pets.Killed += OnKilled;
    }

    public LootKind Kind => LootKind.Pet;
    public bool LiesWhenDropped => false;

    /// <summary>A pet died while a Thief carried it (its entity id): the Thief has nothing in its hands any more.</summary>
    public event Action<ushort>? CarriedKilled;

    /// <summary>Pieces carried or in a van now.</summary>
    public int HeldCount => _held.Count;

    // ---- targets ----

    StealTarget TargetOf(ushort entity, Animal pet, Plan plan) => new(LootKind.Pet, entity, pet.Position, plan.WorkSpot, null, false, plan.Hop);

    Plan? PlanOf(LivePet p)
    {
        var at = p.Pet.Position;
        if (_plans.TryGetValue(p.Entity, out var c) && Flat(c.At, at) <= _cfg.PlanRecheckM) return c.Plan;
        var plan = Compute(p);
        if (c == null) _plans[p.Entity] = c = new Cached();
        c.At = at; c.Plan = plan;
        return plan;
    }

    Plan? Compute(LivePet p)
    {
        var at = p.Pet.Position;
        var pen = _pets.PenOf(p.Group);
        var sections = pen != null && pen.Contains(at.X, at.Z) ? _fences().Of(p.Group) : Array.Empty<FenceSection>();
        if (sections.Count == 0) return _world.Walkable(at) ? new Plan(at, null) : null; // no fence there, or the pet is out on the open ground: a plain walk
        var pet = new Vector2(at.X, at.Z);
        _order.Clear();
        for (var i = 0; i < sections.Count; i++) _order.Add((sections[i].Nearest(pet).Distance, i));
        _order.Sort((a, b) => a.D.CompareTo(b.D));
        for (var k = 0; k < _order.Count && k < _cfg.FenceCandidates; k++)
            if (HopAt(sections[_order[k].I], pet, pen!) is { } hop) return new Plan(hop.Out, hop);
        return null; // no hop point of this garden is on the mesh: not reachable
    }

    FenceHop? HopAt(FenceSection s, Vector2 pet, Pen pen)
    {
        var (point, _, t) = s.Nearest(pet);
        var dir = s.B - s.A;
        if (dir.LengthSquared() < 1e-9f) return null;
        dir = Vector2.Normalize(dir);
        var normal = new Vector2(-dir.Y, dir.X);
        if (Vector2.Dot(pet - point, normal) < 0) normal = -normal; // towards the pet's side: inside
        var outside = point - normal * _cfg.HopOutM;
        var inside = point + normal * _cfg.HopInM;
        if (pen.Contains(outside.X, outside.Y) || !pen.Contains(inside.X, inside.Y)) return null;
        var baseY = s.GroundAt(t);
        var outY = _world.GroundY(outside.X, outside.Y) ?? baseY;
        var inY = _world.GroundY(inside.X, inside.Y) ?? baseY;
        var o = new Vector3(outside.X, outY, outside.Y);
        if (!_world.Walkable(o)) return null;
        return new FenceHop(o, new Vector3(inside.X, inY, inside.Y), s.HeightM);
    }

    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        _scratch.Clear();
        _pets.LivePets(_scratch);
        foreach (var p in _scratch)
        {
            if (_held.ContainsKey(p.Entity) || Flat(p.Pet.Position, near) > radius) continue;
            if (PlanOf(p) is { } plan) into.Add(TargetOf(p.Entity, p.Pet, plan));
        }
    }

    public void Places(List<Vector2> into)
    {
        _scratch.Clear();
        _pets.LivePets(_scratch);
        foreach (var p in _scratch)
            if (!_held.ContainsKey(p.Entity) && PlanOf(p) != null) into.Add(new Vector2(p.Pet.Position.X, p.Pet.Position.Z));
    }

    public bool TryFind(int id, out StealTarget target)
    {
        target = default;
        if (id < 0 || id > ushort.MaxValue) return false;
        var e = (ushort)id;
        if (_held.ContainsKey(e) || !_pets.TryGet(e, out var pet, out var group) || !pet.Alive) return false;
        if (PlanOf(new LivePet(e, pet, group)) is not { } plan) return false;
        target = TargetOf(e, pet, plan);
        return true;
    }

    // ---- taken, carried, loaded, returned ----

    public void Take(int id)
    {
        var e = (ushort)id;
        if (!_pets.TryGet(e, out var pet, out _) || _pets.SeedOf(e) is not { } seed || !pet.Carry()) return;
        _held[e] = new Held { Seed = seed, Round = _pets.Round, Last = pet.Position };
        _plans.Remove(e);
    }

    public void Carried(int id, Vector3 carrierAt, float carrierYaw)
    {
        if (!_held.TryGetValue((ushort)id, out var h)) return;
        h.Seen = true; h.Last = carrierAt;
        if (h.Loaded || !_pets.TryGet((ushort)id, out var pet, out _)) return;
        var forward = new Vector3(-MathF.Sin(carrierYaw), 0, -MathF.Cos(carrierYaw));
        var right = new Vector3(MathF.Cos(carrierYaw), 0, -MathF.Sin(carrierYaw));
        pet.MoveCarried(carrierAt + right * _cfg.CarryRightM + forward * _cfg.CarryForwardM + new Vector3(0, _cfg.CarryUpM, 0), carrierYaw);
    }

    /// <summary>
    /// Once a tick after the entities ticked: a pet whose carrier said nothing for <see cref="MissedTicks"/> ticks is no longer in a Thief's hands (it was tossed into a van, or its Thief ran off the map with it): its
    /// entity ends, and it is kept as loaded until the van is destroyed (<see cref="Return"/>) or the round resets.
    /// </summary>
    void Sweep()
    {
        List<ushort>? gone = null;
        foreach (var (e, h) in _held)
        {
            if (h.Loaded) continue;
            if (h.Seen) { h.Seen = false; h.Missed = 0; continue; }
            if (++h.Missed < MissedTicks) continue;
            if (_pets.TryGet(e, out _, out _)) { h.Loaded = true; (gone ??= new()).Add(e); }
        }
        if (gone == null) return;
        foreach (var e in gone) _pets.End(e);
    }

    void OnKilled(Animal pet, ushort by)
    {
        if (_pets.EntityOf(pet) is not { } e || !_held.Remove(e)) return;
        CarriedKilled?.Invoke(e);
    }

    public void Return(int id)
    {
        var e = (ushort)id;
        if (!_held.Remove(e, out var h))
        {
            return;
        }
        _plans.Remove(e);
        if (!h.Loaded)
        {
            if (_pets.TryGet(e, out var pet, out _)) pet.Release(); // dropped where its carrier fell: it walks home from there
            return;
        }
        if (h.Round == _pets.Round) _pets.Respawn(h.Seed, h.Last); // the van was destroyed: it reappears at the van and walks home
    }

    public void Reset()
    {
        var held = _held.ToList();
        _held.Clear();
        _plans.Clear();
        foreach (var (e, h) in held)
        {
            if (h.Round != _pets.Round) continue; // a new round brought its own pets
            if (!h.Loaded) { if (_pets.TryGet(e, out var pet, out _)) pet.Release(); }
            else _pets.Respawn(h.Seed, h.Seed.Home);
        }
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

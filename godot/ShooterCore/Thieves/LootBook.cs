using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Where the event's Loot is (GLOSSARY "Loot"), pure: the kinds and their claims (<see cref="Registry"/>), the Loot that lies on the ground (<see cref="Lying"/>, world entities with a cap), and the
/// ways a piece goes back to its place (<see cref="Return"/>). Loot carried by a Thief belongs to the member; Loot loaded in a van to its Raid. A piece that is neither is at its place, or gone
/// (taken, not yet sent back): <see cref="Reset"/> puts everything back (a new round). The server keeps one; each Raid's Thieves work through a <see cref="RaidWork"/> over it.
/// </summary>
public sealed class LootBook
{
    readonly LootConfig _cfg;
    readonly WorldEntityManager _entities;
    readonly Dictionary<ushort, LyingLoot> _lying = new();
    readonly List<ushort> _order = new();   // entity ids, oldest first
    readonly List<StealTarget> _scratch = new();
    readonly Random _rng;

    public LootBook(LootConfig cfg, LootRegistry registry, WorldEntityManager entities, int seed = 0)
    {
        _cfg = cfg; Registry = registry; _entities = entities;
        _rng = new Random(seed);
        entities.Despawned += OnDespawned;
    }

    public LootRegistry Registry { get; }
    public LootConfig Config => _cfg;

    /// <summary>Where every piece away from its place is in its life (<see cref="LootPieceState"/>).</summary>
    public LootPieces Pieces { get; } = new();

    /// <summary>Loot lies on the ground now, oldest first.</summary>
    public IEnumerable<LyingLoot> Lying => _order.Select(id => _lying[id]);
    public int LyingCount => _lying.Count;

    /// <summary>A piece went back to its place (sent back by a player, by the cap, or because its van was destroyed).</summary>
    public event Action<LootOrigin>? Returned;

    // ---- targets ----

    /// <summary>Appends every free, unclaimed target within <paramref name="radius"/> of <paramref name="near"/>: things of the registered kinds and Loot that lies.</summary>
    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        Registry.FreeNear(near, radius, into);
        foreach (var (id, l) in _lying)
        {
            if (Flat(l.Position, near) > radius) continue;
            var t = LyingTarget(id, l);
            if (!Registry.IsClaimed(t)) into.Add(t);
        }
    }

    static StealTarget LyingTarget(ushort id, LyingLoot l) => new(l.Kind, id, l.Position, l.Position, null, true);

    /// <summary>The target as it is now, if it is still there to take: a thing still free at its source, Loot still lying.</summary>
    public bool TryFind(in StealTarget t, out StealTarget now)
    {
        if (!t.Lying) return Registry.TryFind(t, out now);
        if (_lying.TryGetValue((ushort)t.Id, out var l)) { now = LyingTarget((ushort)t.Id, l); return true; }
        now = t;
        return false;
    }

    /// <summary>
    /// A Thief's next target: among the kinds with something free near <paramref name="near"/> (<paramref name="allow"/> filters targets), a kind by weight (<see cref="LootKindConfig.Weight"/>),
    /// then that kind's target nearest to <paramref name="from"/>. The target is claimed. Null when nothing is free.
    /// </summary>
    public StealTarget? Pick(Vector3 near, float radius, Vector3 from, Func<StealTarget, bool>? allow = null)
    {
        _scratch.Clear();
        FreeNear(near, radius, _scratch);
        if (allow != null) _scratch.RemoveAll(t => !allow(t));
        var total = 0f;
        foreach (var kind in LootKinds.All) if (_scratch.Any(t => t.Kind == kind)) total += _cfg.Of(kind).Weight;
        if (!(total > 0)) return null;
        LootKind? chosen = null;
        if (_cfg.Tire.Weight > 0 && _scratch.Any(IsTireOnCar)) chosen = LootKind.Tire; // vehicles are the priority: a tire still on a car makes the tire kind beat every other (the nearest tire, a dropped one too)
        else
        {
            var roll = (float)_rng.NextDouble() * total;
            foreach (var kind in LootKinds.All)
            {
                if (!_scratch.Any(t => t.Kind == kind)) continue;
                var w = _cfg.Of(kind).Weight;
                if (!(w > 0)) continue;
                chosen = kind;
                if ((roll -= w) < 0) break;
            }
        }
        if (chosen is not { } k) return null;
        StealTarget? best = null;
        var bestD = float.PositiveInfinity;
        foreach (var t in _scratch)
        {
            if (t.Kind != k) continue;
            var d = Flat(t.WorkSpot, from);
            if (d < bestD) { bestD = d; best = t; }
        }
        if (best is not { } pick || !Claim(pick)) return null;
        return pick;
    }

    static bool IsTireOnCar(StealTarget t) => t.Kind == LootKind.Tire && !t.Lying;

    /// <summary>The claim on the target ends (the Thief gave up, was called off or died). A claim that is not there is ignored.</summary>
    public void Release(in StealTarget t)
    {
        if (!Registry.IsClaimed(t)) return;
        Registry.Release(t);
        if (OriginOf(t) is not { } piece) return; // Loot that no longer lies: its claim lapsed with it
        var now = Pieces.Of(piece);
        if (now == LootPieceState.Claimed) Pieces.Enter(piece, LootPieceState.AtPlace);
        else if (now == LootPieceState.LyingClaimed) Pieces.Enter(piece, LootPieceState.Lying);
    }

    /// <summary>Claims one target for a Thief (false when it is claimed already).</summary>
    public bool Claim(in StealTarget t)
    {
        if (!Registry.Claim(t)) return false;
        if (OriginOf(t) is not { } piece) return true;
        if (!t.Lying && Pieces.Of(piece) != LootPieceState.AtPlace) Pieces.Forget(piece); // its source offers it free: a kind that grows back (a pole) has a new piece under the old id, the old one's life is over
        Pieces.Enter(piece, t.Lying ? LootPieceState.LyingClaimed : LootPieceState.Claimed);
        return true;
    }

    /// <summary>The piece a target is: its kind and id, or for Loot that lies the origin of the entity (null when it no longer lies).</summary>
    LootOrigin? OriginOf(in StealTarget t) => !t.Lying ? new LootOrigin(t.Kind, t.Id) : _lying.TryGetValue((ushort)t.Id, out var l) ? l.Origin : null;

    /// <summary>Appends the wheels still on a car (at rest) that no Thief has claimed.</summary>
    public void FreeTires(int carId, List<StealTarget> into)
    {
        if (Registry.Source(LootKind.Tire) is not TireSource src) return;
        var from = into.Count;
        src.WheelsOf(carId, into);
        for (var i = into.Count - 1; i >= from; i--)
            if (Registry.IsClaimed(into[i])) into.RemoveAt(i);
    }

    /// <summary>The cars the tire kind sees (empty without it).</summary>
    public IEnumerable<TireCar> TireCars() => Registry.Source(LootKind.Tire) is TireSource src ? src.Cars() : Enumerable.Empty<TireCar>();

    /// <summary>A Thief was struck at the top of its climb (<see cref="IStealSource.AloftHazard"/>): who and what it climbs for. Raised as it happens, before the member's state changes.</summary>
    public event Action<GangMember, StealTarget>? Shocked;

    /// <summary>The Thief reached the top of its climb for <paramref name="t"/>: asks the kind's source (<see cref="IStealSource.AloftHazard"/>) and, when it strikes, raises <see cref="Shocked"/>.</summary>
    public AloftShock? AloftHazard(in StealTarget t, GangMember thief, Random rng)
    {
        if (t.Lying || Registry.Source(t.Kind)?.AloftHazard(t.Id, rng) is not { } shock) return null;
        Shocked?.Invoke(thief, t);
        return shock;
    }

    /// <summary>A piece is carried: its kind's source hears where the carrier stands (<see cref="IStealSource.Carried"/>).</summary>
    public void Carrying(LootOrigin piece, Vector3 at, float yaw) => Registry.Source(piece.Kind)?.Carried(piece.Id, at, yaw);

    /// <summary>
    /// The Thief has the thing in its hands: it is gone from its place (<see cref="IStealSource.Take"/>) or taken up from the ground; the claim ends. The origin to carry, or null when the thing was
    /// no longer there.
    /// </summary>
    public LootOrigin? Take(in StealTarget t)
    {
        if (t.Lying)
        {
            Registry.Release(t);
            if (!_lying.TryGetValue((ushort)t.Id, out var l)) return null;
            Forget((ushort)t.Id);
            _entities.Despawn((ushort)t.Id, WorldDespawnReason.Opened);
            Pieces.Enter(l.Origin, LootPieceState.Carried);
            return l.Origin;
        }
        if (!Registry.TryFind(t, out _)) { Release(t); return null; }
        Registry.Release(t);
        Registry.Source(t.Kind)!.Take(t.Id);
        var piece = new LootOrigin(t.Kind, t.Id);
        Pieces.Enter(piece, LootPieceState.Carried);
        return piece;
    }

    /// <summary>The Raid's van took the piece a Thief tossed in (<see cref="Raid.LoadIn"/>).</summary>
    public void Loaded(LootOrigin piece) => Pieces.Enter(piece, LootPieceState.Loaded);

    /// <summary>The piece a Thief carried is gone for good with nothing to send back (a pet killed in its hands): it is no longer away from anything.</summary>
    public void Lost(LootOrigin piece) => Pieces.Enter(piece, LootPieceState.AtPlace);

    // ---- lying and returning ----

    /// <summary>
    /// Loot lies where it fell (its carrier was killed; a kind that does not lie (<see cref="IStealSource.LiesWhenDropped"/>) goes back at once). Over <see cref="LootConfig.MaxLying"/> the oldest goes back to its place by itself. When the world has no room for the entity the piece goes
    /// back at once. Returns the entity id, or null.
    /// </summary>
    public ushort? Drop(LootOrigin origin, Vector3 at)
    {
        if (!(Registry.Source(origin.Kind)?.LiesWhenDropped ?? true)) { Return(origin); return null; } // the kind sees to it itself
        while (_order.Count >= _cfg.MaxLying) ReturnLying(_order[0]);
        var yaw = (float)_rng.NextDouble() * MathF.Tau - MathF.PI;
        var lying = new LyingLoot(origin, at, yaw);
        if (_entities.Spawn(lying) is not { } id) { Return(origin); return null; }
        _lying[id] = lying;
        _order.Add(id);
        Pieces.Enter(origin, LootPieceState.Lying);
        return id;
    }

    /// <summary>The piece goes back to its place (<see cref="IStealSource.Return"/>).</summary>
    public void Return(LootOrigin origin)
    {
        Registry.Source(origin.Kind)?.Return(origin.Id);
        Pieces.Enter(origin, LootPieceState.AtPlace);
        Returned?.Invoke(origin);
    }

    /// <summary>The Loot that lies as entity <paramref name="id"/> goes back to its place (a player sent it back, or the cap). False when no such Loot lies.</summary>
    public bool ReturnLying(ushort id)
    {
        if (!_lying.TryGetValue(id, out var l)) return false;
        Forget(id);
        _entities.Despawn(id, WorldDespawnReason.Opened);
        Return(l.Origin);
        return true;
    }

    /// <summary>Everything a destroyed van held goes back to its place.</summary>
    public void ReturnAll(IEnumerable<LootOrigin> pieces) { foreach (var o in pieces.ToList()) Return(o); }

    /// <summary>The Loot lying nearest to <paramref name="at"/> within <paramref name="reach"/> (flat, and not more than 2.2 m above or below), the entity id, or null.</summary>
    public ushort? NearestLying(Vector3 at, float reach)
    {
        ushort? best = null;
        var bestD = reach;
        foreach (var (id, l) in _lying)
        {
            if (MathF.Abs(l.Position.Y - at.Y) > 2.2f) continue;
            var d = Flat(l.Position, at);
            if (d <= bestD) { bestD = d; best = id; }
        }
        return best;
    }

    /// <summary>A new round (or a cancelled event): lying Loot is gone, claims are cleared and every kind puts everything back.</summary>
    public void Reset()
    {
        foreach (var id in _order.ToList()) { Forget(id); _entities.Despawn(id, WorldDespawnReason.Removed); }
        Registry.Reset();
        Pieces.Clear();
    }

    void Forget(ushort id) { _lying.Remove(id); _order.Remove(id); }

    void OnDespawned(WorldDespawned d)
    {
        if (!_lying.TryGetValue(d.Id, out var l)) return;
        Forget(d.Id); // the world cleared it (a round reset): nothing to return, the sources reset themselves
        Pieces.Forget(l.Origin);
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>Where one hold of the use key on lying Loot is (<see cref="LootReturnHold"/>): nothing counted, counting toward the return, or spent (done or interrupted) and waiting for the key to be released.</summary>
public enum LootHoldState : byte { Idle, Holding, Spent }

/// <summary>
/// A player's hold of the use key on lying Loot, to send it back (<see cref="LootConfig.ReturnSeconds"/>): counts while the key is held within reach of the same Loot; leaving its reach, switching to other
/// Loot or releasing the key starts it over; <see cref="Interrupt"/> (a hit taken) ends the hold and it needs a new press. Completes once per hold. The server counts it from the inputs; the client
/// runs the same for its ring.
/// </summary>
public struct LootReturnHold
{
    public static readonly StateMachineSpec<LootHoldState> Spec = StateMachineSpec<LootHoldState>
        .Define("Loot.ReturnHold", "One hold of the use key on lying Loot to send it back: idle, counting in reach, or spent (done, or interrupted by a hit) until the key is released.", LootHoldState.Idle)
        .Allow(LootHoldState.Idle, LootHoldState.Holding, LootHoldState.Spent)
        .Allow(LootHoldState.Holding, LootHoldState.Idle, LootHoldState.Spent)
        .Allow(LootHoldState.Spent, LootHoldState.Idle)
        .Note(LootHoldState.Idle, "Key up, or held with no Loot in reach (Held and Target are 0).")
        .Note(LootHoldState.Holding, "Key held within reach of Loot: Held counts up on Target; other Loot or leaving reach starts it over. Completes once Held reaches the return seconds.")
        .Note(LootHoldState.Spent, "Completed (the return fires once) or interrupted by a hit (also with the key up: the press of that same input counts for nothing): nothing more until the key is released.")
        .Build();

    LootHoldState _state;

    public double Held { get; private set; }
    /// <summary>The Loot (entity id) the hold counts on, or 0.</summary>
    public ushort Target { get; private set; }
    /// <summary>The state of the hold (a struct cannot own a <see cref="StateMachine{TState}"/> and stay a plain value, so it checks its own edges).</summary>
    public readonly LootHoldState State => _state;

    /// <summary>One input. <paramref name="inReach"/> is the Loot within reach now, or null. True on the tick the hold completes (act once).</summary>
    public bool Tick(bool use, ushort? inReach, double dt, double seconds)
    {
        if (!use) { Held = 0; Target = 0; Go(LootHoldState.Idle); return false; }
        if (_state == LootHoldState.Spent) return false;
        if (inReach is not { } id) { Held = 0; Target = 0; Go(LootHoldState.Idle); return false; }
        Go(LootHoldState.Holding);
        if (id != Target) { Target = id; Held = 0; }
        Held += dt;
        if (Held < seconds) return false;
        Held = 0; Target = 0;
        Go(LootHoldState.Spent);
        return true;
    }

    /// <summary>A hit was taken: the hold is over and the key has to be pressed again.</summary>
    public void Interrupt() { Held = 0; Target = 0; Go(LootHoldState.Spent); }

    public void Reset() { Held = 0; Target = 0; Go(LootHoldState.Idle); }

    void Go(LootHoldState next)
    {
        if (_state == next) return;
        if (!Spec.Allows(_state, next)) StateMachineDiagnostics.Illegal(Spec.Name, _state.ToString(), next.ToString());
        _state = next;
    }
}

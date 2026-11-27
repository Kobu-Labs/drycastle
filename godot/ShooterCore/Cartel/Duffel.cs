using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Duffel holds (GLOSSARY "Duffel"): weed is light, cash medium, product heavy (duffels.json). The wire carries the number.</summary>
public enum DuffelKind : byte { Weed = 0, Cash = 1, Product = 2 }

/// <summary>Where a Duffel is: lying on the ground, carried by a player, flying from a throw, or loaded in a cargo volume (the Cartel van's back).</summary>
public enum DuffelState : byte { Lying = 0, Carried = 1, Flying = 2, Loaded = 3 }

/// <summary>
/// A cargo volume that takes Duffels (the Cartel van's rear box; the van is another package's): a Duffel that flies into it, lands in it or is put down in it becomes Loaded and is counted by the
/// volume's owner. The volume is asked every flight step, so it answers for where it is now (a van moves).
/// </summary>
public interface ICargoVolume
{
    /// <summary>Whether the point (world) is inside the volume now.</summary>
    bool Contains(Vector3 point);
    /// <summary>Whether this Duffel may be loaded here (only the Job's own van takes its party's Duffels).</summary>
    bool Accepts(Duffel duffel);
    /// <summary>The Duffel is loaded in this volume now (once per Duffel); the owner counts it.</summary>
    void Load(Duffel duffel);
}

/// <summary>The world a flying or dropped Duffel meets (ADR 0028: the server's WorldQuery, mover row Drop): what stops a flight step, and the ground under a point.</summary>
public interface IDuffelWorld
{
    /// <summary>Whether the segment from a to b meets something a dropped thing stops on, and where, with the surface's normal.</summary>
    bool Blocked(Vector3 a, Vector3 b, out Vector3 at, out Vector3 normal);
    /// <summary>The height of the first surface under the point (searched from up above it to down below it), or null.</summary>
    float? GroundY(Vector3 at, float up, float down);
}

/// <summary>The rules of the Duffel entity that need no world: the wire type, the state layout, the kinds' names.</summary>
public static class DuffelRules
{
    /// <summary>Wire id of the Duffel world entity type (60 and 61 are the Bigfoot's).</summary>
    public const byte TypeId = 62;

    public static readonly DuffelKind[] AllKinds = { DuffelKind.Weed, DuffelKind.Cash, DuffelKind.Product };

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "duffel", HasHp: false));
    }

    /// <summary>The kind's name in duffels.json and in /duffel ("weed", "cash", "product").</summary>
    public static string NameOf(DuffelKind kind) => kind.ToString().ToLowerInvariant();

    public static DuffelKind? Parse(string name) => AllKinds.Where(k => NameOf(k) == name.Trim().ToLowerInvariant()).Select(k => (DuffelKind?)k).FirstOrDefault();

    /// <summary>The state byte: the state in bits 0-1, the kind in bits 2-3.</summary>
    public static byte Pack(DuffelState state, DuffelKind kind) => (byte)((byte)state | (byte)kind << 2);

    public static DuffelState StateOf(byte b) => (DuffelState)(b & 3);

    /// <summary>The kind of a state byte; a kind this build does not know is read as null (the client draws nothing).</summary>
    public static DuffelKind? KindOf(byte b) => (b >> 2 & 3) is var k && k < AllKinds.Length ? (DuffelKind)k : null;
}

/// <summary>
/// A Duffel (GLOSSARY "Duffel"; docs/cartel.md "Duffels"): a server-owned world entity of type <see cref="DuffelRules.TypeId"/>, made for a party (the player ids that may touch it) by a Job or by
/// the admin's <c>/duffel</c>. Machine <see cref="Spec"/>: it lies, is carried, flies from a throw and lands, or is loaded in a cargo volume. The book (<see cref="DuffelBook"/>) moves it between the
/// states; a flight is stepped here, every tick, against the world and the volumes.
/// <para>
/// Layout of the entity state: state byte = <see cref="DuffelRules.Pack"/> (state and kind), owner field = the carrier's player id (0 when nobody carries it), position = where it is (the carrier's
/// feet while carried), yaw = how it lies, hp byte absent.
/// </para>
/// </summary>
public sealed class Duffel : IWorldEntity
{
    public static readonly StateMachineSpec<DuffelState> Spec = StateMachineSpec<DuffelState>
        .Define("Duffel.Place", "A bag of goods of a Job's party: lying, carried by one of the party, flying from a throw, or loaded in a cargo volume (the Cartel van's back).", DuffelState.Lying)
        .Allow(DuffelState.Lying, DuffelState.Carried)
        .Allow(DuffelState.Carried, DuffelState.Flying, DuffelState.Lying, DuffelState.Loaded)
        .Allow(DuffelState.Flying, DuffelState.Lying, DuffelState.Loaded)
        .Note(DuffelState.Lying, "On the ground (the start state; a Duffel made in a player's hands goes on to Carried at once). A party player's interact key picks it up.")
        .Note(DuffelState.Carried, "On a party player's back: no sprint, slower by its weight. The throw key's tap puts it down (Lying, or Loaded in a cargo volume), a charged release throws it (Flying); the carrier's death drops it where they fell.")
        .Note(DuffelState.Flying, "The throw's arc, stepped against the world each tick: it lands (Lying), or passes into a cargo volume that accepts it (Loaded). A wall stops it and it falls straight down.")
        .Note(DuffelState.Loaded, "In a cargo volume, counted by its owner; it stays there until the Job removes it.")
        .Build();

    readonly StateMachine<DuffelState> _fsm = new(Spec);
    readonly DuffelBook _book;

    internal Duffel(DuffelBook book, DuffelKind kind, IReadOnlySet<ushort> party, Vector3 at, float yaw)
    {
        _book = book; Kind = kind; Party = party; Position = at; Yaw = yaw;
    }

    /// <summary>The world entity id (0 until it is spawned).</summary>
    public ushort Id { get; internal set; }
    public DuffelKind Kind { get; }
    /// <summary>The player ids that may pick it up: the Job's party, read live (a squad mate who joins later is in it when the Job's set has them).</summary>
    public IReadOnlySet<ushort> Party { get; }
    public DuffelState State => _fsm.Current;
    /// <summary>The player who carries it; 0 when nobody does.</summary>
    public ushort Carrier { get; private set; }
    /// <summary>The volume it is loaded in; null unless <see cref="DuffelState.Loaded"/>.</summary>
    public ICargoVolume? Volume { get; private set; }
    public Vector3 Position { get; internal set; }
    public Vector3 Velocity { get; private set; }
    public float Yaw { get; private set; }
    /// <summary>Seconds of the flight so far.</summary>
    public float FlightSeconds => _fsm.Is(DuffelState.Flying) ? _fsm.Elapsed : 0;

    public byte TypeId => DuffelRules.TypeId;
    public ushort Owner => Carrier;
    public WorldDespawnReason? Ended { get; private set; }

    public bool MayTouch(ushort player) => Party.Contains(player);

    /// <summary>It is gone (the Job ended, the round reset); the manager despawns it on the next tick.</summary>
    public void End(WorldDespawnReason reason) => Ended ??= reason;

    internal void TakeUp(ushort carrier, Vector3 feet)
    {
        Carrier = carrier; Position = feet; Velocity = Vector3.Zero;
        _fsm.Enter(DuffelState.Carried);
    }

    internal void Launch(Vector3 from, Vector3 velocity)
    {
        Carrier = 0; Position = from; Velocity = velocity;
        Yaw = MathF.Atan2(-velocity.X, -velocity.Z);
        _fsm.Enter(DuffelState.Flying);
    }

    internal void PutDown(Vector3 at)
    {
        Carrier = 0; Position = at; Velocity = Vector3.Zero;
        _fsm.Enter(DuffelState.Lying);
    }

    internal void LoadInto(ICargoVolume volume, Vector3 at)
    {
        Carrier = 0; Position = at; Velocity = Vector3.Zero; Volume = volume;
        _fsm.Enter(DuffelState.Loaded);
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        _fsm.Tick(dt);
        if (_fsm.Is(DuffelState.Flying)) _book.StepFlight(this, dt);
    }

    internal void Fly(Vector3 position, Vector3 velocity) { Position = position; Velocity = velocity; }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Carrier, DuffelRules.Pack(State, Kind), 0);
}

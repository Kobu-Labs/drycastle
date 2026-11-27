using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Duffel holds (GLOSSARY "Duffel"): weed is light, cash medium, product heavy (duffels.json). The wire carries the number.</summary>
public enum DuffelKind : byte { Weed = 0, Cash = 1, Product = 2 }

/// <summary>Where a Duffel is: lying on the ground (or on the Cartel pallet), carried by a player, flying from a throw, or loaded in a cargo volume (a vehicle's boot, bed or bay).</summary>
public enum DuffelState : byte { Lying = 0, Carried = 1, Flying = 2, Loaded = 3 }

/// <summary>
/// Where a flying Duffel met a cargo volume (<see cref="ICargoVolume.Meets"/>): the world point, the horizontal way out a refused Duffel is put aside by (it falls straight down beside the volume), and whether it
/// came down through the volume's top face.
/// </summary>
public readonly record struct CargoMeet(Vector3 At, Vector3 Normal, bool FromTop);

/// <summary>
/// A cargo volume that takes Duffels (a vehicle's: <see cref="VehicleCargo"/>): a Duffel that flies into it or lands in it, and that it accepts, becomes Loaded and takes a slot; one it does not accept stops
/// at it as at a wall. The volume is asked every flight step, so it answers for where it is now (a vehicle moves).
/// </summary>
public interface ICargoVolume
{
    /// <summary>The world id of the vehicle the volume belongs to (the wire's: a loaded Duffel's owner field).</summary>
    ushort Vehicle { get; }
    /// <summary>Whether the flight segment from a to b meets the volume (grown by its margin), and where and how.</summary>
    bool Meets(Vector3 from, Vector3 to, out CargoMeet meet);
    /// <summary>Whether the point (world) is inside the volume (grown by its margin) now: a Duffel that lands against it.</summary>
    bool Contains(Vector3 point);
    /// <summary>Whether this Duffel may be loaded here, coming the way it does (<paramref name="fromTop"/>): open, room, its party allowed, a top-loaded bed entered from above.</summary>
    bool Accepts(Duffel duffel, bool fromTop);
    /// <summary>The Duffel is loaded in this volume now (once per Duffel): the first free slot it takes.</summary>
    int Load(Duffel duffel);
    /// <summary>The Duffel leaves the volume (taken out, or removed from the world): its slot is free.</summary>
    void Unload(Duffel duffel);
    /// <summary>How far (flat metres) the player standing at <paramref name="feet"/> is from the volume's access point while the cargo area is open and they are within its radius; null otherwise.</summary>
    float? TakeOutDistance(Vector3 feet);
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
    /// <summary>The kinds' names by kind (read every frame by the client's hint: no string is built).</summary>
    static readonly string[] Names = { "weed", "cash", "product" };

    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "duffel", HasHp: false));
    }

    /// <summary>The kind's name in duffels.json and in /duffel ("weed", "cash", "product").</summary>
    public static string NameOf(DuffelKind kind) => Names[(int)kind];

    public static DuffelKind? Parse(string name) => AllKinds.Where(k => NameOf(k) == name.Trim().ToLowerInvariant()).Select(k => (DuffelKind?)k).FirstOrDefault();

    /// <summary>The state byte: the state in bits 0-1, the kind in bits 2-3, the slot a loaded Duffel takes in its vehicle in bits 4-7 (0 otherwise).</summary>
    public static byte Pack(DuffelState state, DuffelKind kind, int slot = 0) => (byte)((byte)state | (byte)kind << 2 | (slot & 15) << 4);

    /// <summary>The slot of a loaded Duffel's state byte (the vehicle's cargo slot, <see cref="Vehicles.VehicleCargoVolumeDef.Slots"/>).</summary>
    public static int SlotOf(byte b) => b >> 4;

    public static DuffelState StateOf(byte b) => (DuffelState)(b & 3);

    /// <summary>The kind of a state byte; a kind this build does not know is read as null (the client draws nothing).</summary>
    public static DuffelKind? KindOf(byte b) => (b >> 2 & 3) is var k && k < AllKinds.Length ? (DuffelKind)k : null;
}

/// <summary>
/// A Duffel (GLOSSARY "Duffel"; docs/cartel.md "Duffels"): a server-owned world entity of type <see cref="DuffelRules.TypeId"/>, made for a party (the player ids that may touch it) by a Job or by
/// the admin's <c>/duffel</c>. Machine <see cref="Spec"/>: it lies, is carried, flies from a throw and lands, or is loaded in a cargo volume. The book (<see cref="DuffelBook"/>) moves it between the
/// states; a flight is stepped here, every tick, against the world and the volumes.
/// <para>
/// Layout of the entity state: state byte = <see cref="DuffelRules.Pack"/> (state, kind and, loaded, the slot), owner field = the carrier's player id while carried or the vehicle's world id while loaded (0
/// otherwise), position = where it is (the carrier's feet while carried; where it went in while loaded: a loaded one is drawn from its vehicle and slot, not from this), yaw = how it lies, hp byte absent.
/// </para>
/// </summary>
public sealed class Duffel : IWorldEntity
{
    public static readonly StateMachineSpec<DuffelState> Spec = StateMachineSpec<DuffelState>
        .Define("Duffel.Place", "A bag of goods of a Job's party: lying, carried by one of the party, flying from a throw, or loaded in a vehicle's cargo volume.", DuffelState.Lying)
        .Allow(DuffelState.Lying, DuffelState.Carried)
        .Allow(DuffelState.Carried, DuffelState.Flying, DuffelState.Lying, DuffelState.Loaded)
        .Allow(DuffelState.Flying, DuffelState.Lying, DuffelState.Loaded)
        .Allow(DuffelState.Loaded, DuffelState.Carried)
        .Note(DuffelState.Lying, "On the ground, or on a Job's pallet spot (the start state; a Duffel made in a player's hands goes on to Carried at once). A party player's interact key picks it up.")
        .Note(DuffelState.Carried, "On a party player's back: no sprint, slower by its weight. One press of the throw key throws it at once (Flying); the carrier's death, a seat or leaving the party puts it down where they were (Lying).")
        .Note(DuffelState.Flying, "The throw's arc, stepped against the world each tick: it lands (Lying), or passes into a cargo volume that accepts it (Loaded). A wall, or a cargo volume that does not accept it (shut doors, full, not its party's, a bed met from the side), stops it and it falls straight down beside.")
        .Note(DuffelState.Loaded, "In a vehicle's cargo volume, on the first free slot, drawn there and fixed to the vehicle. A party player with empty hands at the open cargo area takes the last loaded one out (Carried); the vehicle's destruction, retirement or reset, or the Job's end, removes it.")
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
    /// <summary>The player ids that may pick it up: the Job's party set (<see cref="Jobs.Job.Party"/>: a squad mate who joins later is in it from the next tick).</summary>
    public IReadOnlySet<ushort> Party { get; }
    public DuffelState State => _fsm.Current;
    /// <summary>The player who carries it; 0 when nobody does.</summary>
    public ushort Carrier { get; private set; }
    /// <summary>The volume it is loaded in; null unless <see cref="DuffelState.Loaded"/>.</summary>
    public ICargoVolume? Volume { get; private set; }
    /// <summary>The slot it takes in its volume (the vehicle's cargo slot); 0 unless <see cref="DuffelState.Loaded"/>.</summary>
    public int Slot { get; private set; }
    /// <summary>Counts the loads of the book: the bigger, the later it was loaded (the last loaded comes out first).</summary>
    public uint LoadOrder { get; private set; }
    public Vector3 Position { get; internal set; }
    public Vector3 Velocity { get; private set; }
    public float Yaw { get; private set; }
    /// <summary>Seconds of the flight so far.</summary>
    public float FlightSeconds => _fsm.Is(DuffelState.Flying) ? _fsm.Elapsed : 0;

    public byte TypeId => DuffelRules.TypeId;
    /// <summary>The carrier's player id while carried, the vehicle's world id while loaded, else 0 (the wire's owner field).</summary>
    public ushort Owner => State == DuffelState.Loaded ? Volume?.Vehicle ?? 0 : Carrier;
    /// <summary>Never ends by itself: the book despawns it (<see cref="DuffelBook.Remove"/>, the round's reset).</summary>
    public WorldDespawnReason? Ended => null;

    public bool MayTouch(ushort player) => Party.Contains(player);

    internal void TakeUp(ushort carrier, Vector3 feet)
    {
        Carrier = carrier; Position = feet; Velocity = Vector3.Zero; Volume = null; Slot = 0;
        _fsm.Enter(DuffelState.Carried);
    }

    internal void Launch(Vector3 from, Vector3 velocity)
    {
        Carrier = 0; Position = from; Velocity = velocity;
        if (velocity.X != 0 || velocity.Z != 0) Yaw = MathF.Atan2(-velocity.X, -velocity.Z); // a straight fall (a drop from high up) keeps how it lay
        _fsm.Enter(DuffelState.Flying);
    }

    internal void PutDown(Vector3 at)
    {
        Carrier = 0; Position = at; Velocity = Vector3.Zero;
        _fsm.Enter(DuffelState.Lying);
    }

    internal void LoadInto(ICargoVolume volume, int slot, Vector3 at, uint order)
    {
        Carrier = 0; Position = at; Velocity = Vector3.Zero; Volume = volume; Slot = slot; LoadOrder = order;
        _fsm.Enter(DuffelState.Loaded);
    }

    public void Tick(float dt)
    {
        if (dt <= 0) return;
        _fsm.Tick(dt);
        if (_fsm.Is(DuffelState.Flying)) _book.StepFlight(this, dt);
    }

    internal void Fly(Vector3 position, Vector3 velocity) { Position = position; Velocity = velocity; }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, Yaw, 0, Owner, DuffelRules.Pack(State, Kind, State == DuffelState.Loaded ? Slot : 0), 0);
}

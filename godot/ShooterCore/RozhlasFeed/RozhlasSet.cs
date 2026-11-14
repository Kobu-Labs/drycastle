using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Whether somebody holds the rozhlas set (the microphone at the dictation table).</summary>
public enum RozhlasSetState : byte { Free = 0, Held = 1 }

/// <summary>What asking to take the set came to.</summary>
public enum RozhlasTake : byte
{
    /// <summary>The player holds it now.</summary>
    Taken,
    /// <summary>The player already held it (nothing changed).</summary>
    AlreadyYours,
    /// <summary>Only while the mic is hijacked (the announcer is dead).</summary>
    NotHijacked,
    /// <summary>Another player holds it; they must let go, die, leave or walk away first.</summary>
    Occupied,
    /// <summary>The dictation table is known and the player is not within <see cref="RozhlasSet.ReachMetres"/> of it.</summary>
    TooFar,
    /// <summary>Not from a vehicle seat.</summary>
    InVehicle,
}

/// <summary>
/// The rozhlas set (docs/events/README.md, "The set"): who is on the mic. Godot-free; the server owns one and asks it the questions below, it never looks at a player itself.
/// One holder at a time, and only while the mic is hijacked (<see cref="RozhlasPhase.Hijacked"/>). Released on the holder's death, disconnect, hijack end, an explicit release,
/// entering a vehicle (they cannot talk there: the V key is the vehicle view's), and, once the table is known, moving more than <see cref="ReachMetres"/> from it. The next player can
/// take it at once, no talk cap.
/// <para>
/// The dictation table does not exist yet (the Urad building waits for its interior model): <see cref="Table"/> is optional. With none, taking has no distance rule and the admin's
/// <c>/rozhlas take</c> and <c>/rozhlas release</c> stand in for "hold E at the table". When the building supplies the table's position, set <see cref="Table"/> and the real rule
/// switches on by itself: <see cref="Take"/> needs the player's position and <see cref="Check"/> releases a holder who walks off.
/// </para>
/// </summary>
public sealed class RozhlasSet
{
    /// <summary>Hold E at the table, and let go when more than this far from it.</summary>
    public const float ReachMetres = 2f;

    /// <summary>How long the interact key is held at the table to take the set (<see cref="InteractHold"/>; the server counts, the client shows the ring).</summary>
    public const float TakeHoldSeconds = 1f;

    public static bool InReach(Vector3 table, Vector3 at) => Vector3.Distance(table, at) <= ReachMetres;

    /// <summary>Whether a hold of the interact key counts towards taking the set: hijacked, free, on foot and at the table. The same question on the server (the rule) and the client (prompt and ring).</summary>
    public static bool CanTakeHere(bool hijacked, bool held, Vector3? table, Vector3 at, bool inVehicle) =>
        hijacked && !held && !inVehicle && table is { } t && InReach(t, at);

    /// <summary>Holder id of "nobody" on the wire: player ids start at 1.</summary>
    public const ushort None = 0;

    public static readonly StateMachineSpec<RozhlasSetState> Spec = StateMachineSpec<RozhlasSetState>.Define("Rozhlas.Set", "Who holds the rozhlas set (the mic at the dictation table): nobody, or one player while the mic is hijacked.", RozhlasSetState.Free)
        .Allow(RozhlasSetState.Free, RozhlasSetState.Held)
        .Allow(RozhlasSetState.Held, RozhlasSetState.Free)
        .Note(RozhlasSetState.Free, "Nobody is on the mic. A player may take it, only while the loudspeaker is Hijacked.")
        .Note(RozhlasSetState.Held, "One player holds it (Holder). Released by their death, disconnect, hijack end, entering a vehicle, walking more than 2 m from the table (once it exists) or an explicit release.")
        .Build();

    readonly StateMachine<RozhlasSetState> _fsm = new(Spec);

    /// <summary>The player on the mic, or <see cref="None"/>.</summary>
    public ushort Holder { get; private set; } = None;

    public bool IsHeld => _fsm.Is(RozhlasSetState.Held);
    public bool IsHolder(ushort player) => player != None && IsHeld && Holder == player;

    /// <summary>
    /// Where the dictation table is (world metres), or null until the Urad's interior exists. Set once the building supplies it; null keeps the distance rule off and
    /// leaves taking to the admin's stand-in.
    /// </summary>
    public Vector3? Table { get; set; }

    /// <summary>The holder changed: the new holder, or <see cref="None"/> when it was let go. Fired after the machine moved.</summary>
    public event Action<ushort>? HolderChanged;

    /// <summary>
    /// A player asks for the set. <paramref name="hijacked"/> is the loudspeaker's state, <paramref name="at"/> the player's position (needed only when <see cref="Table"/> is set),
    /// <paramref name="inVehicle"/> whether they sit in a vehicle.
    /// </summary>
    public RozhlasTake Take(ushort player, bool hijacked, Vector3? at = null, bool inVehicle = false)
    {
        if (!hijacked) return RozhlasTake.NotHijacked;
        if (IsHolder(player)) return RozhlasTake.AlreadyYours;
        if (IsHeld) return RozhlasTake.Occupied;
        if (inVehicle) return RozhlasTake.InVehicle;
        if (Table is { } t && (at is not { } p || !InReach(t, p))) return RozhlasTake.TooFar;
        Holder = player;
        _fsm.Enter(RozhlasSetState.Held);
        HolderChanged?.Invoke(player);
        return RozhlasTake.Taken;
    }

    /// <summary>Lets go of the set when <paramref name="player"/> holds it (their death, disconnect or an explicit release). False when they did not.</summary>
    public bool Release(ushort player)
    {
        if (!IsHolder(player)) return false;
        Clear();
        return true;
    }

    /// <summary>Lets go of the set whoever holds it (the hijack ended). False when it was free.</summary>
    public bool Clear()
    {
        if (!IsHeld) return false;
        Holder = None;
        _fsm.Enter(RozhlasSetState.Free);
        HolderChanged?.Invoke(None);
        return true;
    }

    /// <summary>
    /// Every tick: the rules that end a hold by themselves. The holder lets go when the mic is no longer hijacked, when they sit in a vehicle, or (with a <see cref="Table"/>)
    /// when they are more than <see cref="ReachMetres"/> from it. <paramref name="holderAt"/> is the holder's position (null when unknown, which with a table counts as too far).
    /// Returns true when it released.
    /// </summary>
    public bool Check(bool hijacked, Vector3? holderAt, bool holderInVehicle)
    {
        if (!IsHeld) return false;
        if (!hijacked || holderInVehicle || (Table is { } t && (holderAt is not { } p || !InReach(t, p)))) return Clear();
        return false;
    }
}

/// <summary>
/// <see cref="EventKind.RozhlasSet"/>: u16 holder (little endian), <see cref="RozhlasSet.None"/> (0) when nobody holds the set, then, when the dictation table is known, its position
/// (3 x f32, world metres). Server to everyone when a hijack begins and on every change of holder, and to a joiner during a hijack; the holder's own client starts its mic on it, the
/// others learn who is on air and where the set can be taken (the hold-E prompt).
/// </summary>
public static class RozhlasSetCodec
{
    public const int HolderSize = 2, Size = HolderSize + 12;

    public static byte[] Encode(ushort holder, Vector3? table = null)
    {
        var b = new byte[table == null ? HolderSize : Size];
        BinaryPrimitives.WriteUInt16LittleEndian(b, holder);
        if (table is { } t)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(2), t.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), t.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(10), t.Z);
        }
        return b;
    }

    public static (ushort Holder, Vector3? Table)? Decode(byte[] p)
    {
        if (p.Length != HolderSize && p.Length != Size) return null;
        var holder = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (p.Length == HolderSize) return (holder, null);
        return (holder, new Vector3(BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(2)), BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(6)), BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(10))));
    }
}

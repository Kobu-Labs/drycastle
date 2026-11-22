using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a road sign's plate is in its life (<see cref="BreakableSigns.Spec"/>). The values are the wire byte.</summary>
public enum SignPlateState : byte
{
    Whole = 0,
    Bent = 1,
}

/// <summary>One plate's new state: <see cref="Index"/> is its place in the ordered sign plate list (MapLoader.ObjectSignPlates()), the same on server and clients.</summary>
public readonly record struct SignPlateMsg(ushort Index, SignPlateState State);

/// <summary>
/// The shootable plates of the placed road signs (ADR 0038), server side and pure, the sibling of <see cref="BreakableLamps"/>: a round whose path passes
/// within <see cref="SignPlateLook.HitRadiusM"/> of a plate's centre bends it, after <see cref="SignPlateLook.RespawnSeconds"/> it is whole again. Only a bent
/// plate has a machine, so a map of hundreds of signs costs nothing until one is shot. A new round straightens every plate (<see cref="RestoreAll"/>).
/// Bending is visual only: nothing about a sign touches traffic, bots or the round itself (the plate stops no round; the pole is solid by its collider).
/// </summary>
public sealed class BreakableSigns
{
    public static readonly StateMachineSpec<SignPlateState> Spec = StateMachineSpec<SignPlateState>
        .Define("Sign.Plate", "The shootable plate of a road sign: whole until a round passes through it, bent (hanging askew) for a while, then whole again.", SignPlateState.Whole)
        .Allow(SignPlateState.Whole, SignPlateState.Bent)
        .Allow(SignPlateState.Bent, SignPlateState.Whole)
        .Note(SignPlateState.Whole, "Straight on its clamps. A whole plate has no clock and no machine; a new round straightens every plate without a transition.")
        .Note(SignPlateState.Bent, "Tilted and sagging on its upper clamp (clock: respawn_seconds). A second shot changes nothing and does not restart the clock.")
        .Build();

    readonly IReadOnlyList<Vector3> _plates;
    readonly SignPlateLook _look;
    readonly Dictionary<int, StateMachine<SignPlateState>> _live = new();
    readonly List<int> _done = new();
    readonly List<SignPlateMsg> _changes = new();

    public BreakableSigns(IReadOnlyList<Vector3> plates, SignPlateLook look) { _plates = plates; _look = look; }

    public int Count => _plates.Count;

    public SignPlateState StateOf(int plate) => _live.TryGetValue(plate, out var m) ? m.Current : SignPlateState.Whole;

    /// <summary>Bends plate <paramref name="plate"/> (a whole one). False for a plate already bent or out of range.</summary>
    public bool Shoot(int plate)
    {
        if ((uint)plate >= (uint)_plates.Count || plate > ushort.MaxValue) return false;
        if (!_live.TryGetValue(plate, out var m)) _live[plate] = m = new StateMachine<SignPlateState>(Spec);
        if (!m.Enter(SignPlateState.Bent, _look.RespawnSeconds)) return false;
        _changes.Add(new SignPlateMsg((ushort)plate, SignPlateState.Bent));
        return true;
    }

    /// <summary>A round that flew <paramref name="length"/> metres from <paramref name="from"/> along the unit vector <paramref name="dir"/> bends every plate it passed. Returns how many it bent.</summary>
    public int ShootAlong(Vector3 from, Vector3 dir, float length)
    {
        if (!(length > 0) || _plates.Count == 0) return 0;
        var bent = 0;
        for (var i = 0; i < _plates.Count; i++)
            if (LampGeometry.Hits(from, dir, length, _plates[i], (float)_look.HitRadiusM) && Shoot(i)) bent++;
        return bent;
    }

    /// <summary>Advances the clocks of the plates that are bent.</summary>
    public void Tick(float dt)
    {
        if (_live.Count == 0) return;
        _done.Clear();
        foreach (var (plate, m) in _live)
        {
            if (!m.Tick(dt)) continue;
            m.Enter(SignPlateState.Whole);
            _changes.Add(new SignPlateMsg((ushort)plate, SignPlateState.Whole));
            _done.Add(plate);
        }
        foreach (var plate in _done) _live.Remove(plate);
    }

    /// <summary>The changes since the last call, in the order they happened.</summary>
    public List<SignPlateMsg> TakeChanges()
    {
        if (_changes.Count == 0) return _changes;
        var taken = new List<SignPlateMsg>(_changes);
        _changes.Clear();
        return taken;
    }

    /// <summary>Every plate that is not whole: the joiner's catch-up.</summary>
    public List<SignPlateMsg> Bent() =>
        _live.Select(kv => new SignPlateMsg((ushort)kv.Key, kv.Value.Current)).OrderBy(s => s.Index).ToList();

    /// <summary>A new round: every plate is whole. True when any was not.</summary>
    public bool RestoreAll()
    {
        _changes.Clear();
        if (_live.Count == 0) return false;
        _live.Clear();
        return true;
    }
}

/// <summary>
/// How a bent plate hangs, the same on every client: tilted about its upper clamp (the plate node's origin) and tipped out at the bottom. The way it swings
/// (left or right) is a hash of its index, so a row of shot signs does not all lean one way. Angles in degrees.
/// </summary>
public static class SignBend
{
    public const float RollDeg = 35f, PitchDeg = 12f, SagM = 0.04f;

    /// <summary>+1 or -1: which side plate <paramref name="plate"/> swings to.</summary>
    public static int Side(int plate)
    {
        var h = unchecked((uint)plate * 2654435761u);
        h ^= h >> 15;
        return (h & 1) == 0 ? 1 : -1;
    }
}

/// <summary>
/// Wire side of the road sign plates (ADR 0038), over the reliable channel. <see cref="EventKind.SignPlateState"/>: u16 plate, u8 <see cref="SignPlateState"/>.
/// <see cref="EventKind.SignPlateSync"/>: u16 count, then count times a plate as above; every plate not listed is whole (the join catch-up, a new round).
/// </summary>
public static class SignPlateCodec
{
    const int Bytes = 3;

    static void Write(Span<byte> s, SignPlateMsg m) { BinaryPrimitives.WriteUInt16LittleEndian(s, m.Index); s[2] = (byte)m.State; }

    public static byte[] EncodeState(SignPlateMsg m) { var b = new byte[Bytes]; Write(b, m); return b; }

    public static SignPlateMsg? DecodeState(ReadOnlySpan<byte> p) =>
        p.Length == Bytes && p[2] <= (byte)SignPlateState.Bent ? new SignPlateMsg(BinaryPrimitives.ReadUInt16LittleEndian(p), (SignPlateState)p[2]) : null;

    public static byte[] EncodeSync(IReadOnlyList<SignPlateMsg> plates)
    {
        var b = new byte[2 + plates.Count * Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)plates.Count);
        for (var i = 0; i < plates.Count; i++) Write(b.AsSpan(2 + i * Bytes), plates[i]);
        return b;
    }

    public static List<SignPlateMsg>? DecodeSync(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        var n = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (p.Length != 2 + n * Bytes) return null;
        var list = new List<SignPlateMsg>(n);
        for (var i = 0; i < n; i++)
        {
            if (DecodeState(p.Slice(2 + i * Bytes, Bytes)) is not { } m) return null;
            list.Add(m);
        }
        return list;
    }
}

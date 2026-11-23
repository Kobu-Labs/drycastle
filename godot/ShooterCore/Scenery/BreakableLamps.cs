using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Scenery;

/// <summary>Where a street lamp is in its life (<see cref="BreakableLamps.Spec"/>). The values are the wire byte.</summary>
public enum LampState : byte
{
    Lit = 0,
    Broken = 1,
    Flickering = 2,
}

/// <summary>One lamp's new state: <see cref="Index"/> is its place in the ordered pole lamp list (MapLoader.ObjectLights()), the same on server and clients.</summary>
public readonly record struct LampStateMsg(ushort Index, LampState State);

/// <summary>
/// The shootable pole lamps (ADR 0037), server side and pure: a round whose path passes through a lamp's head breaks it, after <see cref="LampLook.RespawnSeconds"/>
/// it flickers for <see cref="LampLook.FlickerSeconds"/> and is lit again. Only a lamp that is not lit has a machine, so a map of hundreds of lamps costs nothing
/// until one is shot. A new round lights every lamp again (<see cref="RestoreAll"/>). Lamps of lamps.json (the base map's own poles) are not in the list.
/// </summary>
public sealed class BreakableLamps
{
    public static readonly StateMachineSpec<LampState> Spec = StateMachineSpec<LampState>
        .Define("Lamp.Light", "A shootable pole lamp: lit until a round passes through its head, dark for a while, then it flickers back on.", LampState.Lit)
        .Allow(LampState.Lit, LampState.Broken)
        .Allow(LampState.Broken, LampState.Flickering)
        .Allow(LampState.Flickering, LampState.Lit, LampState.Broken)
        .Note(LampState.Lit, "Shines at night. A lit lamp has no clock and no machine; a new round lights every lamp again without a transition.")
        .Note(LampState.Broken, "Dark (clock: respawn_seconds). A second shot changes nothing.")
        .Note(LampState.Flickering, "Coming back on (clock: flicker_seconds); a shot breaks it again.")
        .Build();

    readonly IReadOnlyList<Vector3> _heads;
    readonly LampLook _look;
    readonly Dictionary<int, StateMachine<LampState>> _live = new();
    readonly List<int> _done = new();
    readonly List<LampStateMsg> _changes = new();

    public BreakableLamps(IReadOnlyList<Vector3> heads, LampLook look) { _heads = heads; _look = look; }

    public int Count => _heads.Count;

    public LampState StateOf(int lamp) => _live.TryGetValue(lamp, out var m) ? m.Current : LampState.Lit;

    /// <summary>Breaks lamp <paramref name="lamp"/> (a lit or flickering one). False for a lamp already broken or out of range.</summary>
    public bool Shoot(int lamp)
    {
        if ((uint)lamp >= (uint)_heads.Count || lamp > ushort.MaxValue) return false;
        if (!_live.TryGetValue(lamp, out var m)) _live[lamp] = m = new StateMachine<LampState>(Spec);
        if (!m.Enter(LampState.Broken, _look.RespawnSeconds)) return false;
        _changes.Add(new LampStateMsg((ushort)lamp, LampState.Broken));
        return true;
    }

    /// <summary>A round that flew <paramref name="length"/> metres from <paramref name="from"/> along the unit vector <paramref name="dir"/> breaks every lamp whose head it passed. Returns how many it broke.</summary>
    public int ShootAlong(Vector3 from, Vector3 dir, float length)
    {
        if (!(length > 0) || _heads.Count == 0) return 0;
        var broke = 0;
        for (var i = 0; i < _heads.Count; i++)
            if (LampGeometry.Hits(from, dir, length, _heads[i], (float)_look.HitRadiusM) && Shoot(i)) broke++;
        return broke;
    }

    /// <summary>Advances the clocks of the lamps that are not lit.</summary>
    public void Tick(float dt)
    {
        if (_live.Count == 0) return;
        _done.Clear();
        foreach (var (lamp, m) in _live)
        {
            if (!m.Tick(dt)) continue;
            if (m.Is(LampState.Broken)) { m.Enter(LampState.Flickering, _look.FlickerSeconds); _changes.Add(new LampStateMsg((ushort)lamp, LampState.Flickering)); }
            else { m.Enter(LampState.Lit); _changes.Add(new LampStateMsg((ushort)lamp, LampState.Lit)); _done.Add(lamp); }
        }
        foreach (var lamp in _done) _live.Remove(lamp);
    }

    /// <summary>The changes since the last call, in the order they happened.</summary>
    public List<LampStateMsg> TakeChanges()
    {
        if (_changes.Count == 0) return _changes;
        var taken = new List<LampStateMsg>(_changes);
        _changes.Clear();
        return taken;
    }

    /// <summary>Every lamp that is not lit: the joiner's catch-up.</summary>
    public List<LampStateMsg> Unlit() =>
        _live.Select(kv => new LampStateMsg((ushort)kv.Key, kv.Value.Current)).OrderBy(s => s.Index).ToList();

    /// <summary>A new round: every lamp is lit. True when any was not.</summary>
    public bool RestoreAll()
    {
        _changes.Clear();
        if (_live.Count == 0) return false;
        _live.Clear();
        return true;
    }
}

public static class LampGeometry
{
    /// <summary>Whether the segment <paramref name="from"/> + t * <paramref name="dir"/>, t in 0..<paramref name="length"/>, passes within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
    public static bool Hits(Vector3 from, Vector3 dir, float length, Vector3 centre, float radius)
    {
        var t = Math.Clamp(Vector3.Dot(centre - from, dir), 0f, Math.Max(0f, length));
        return Vector3.DistanceSquared(from + dir * t, centre) <= radius * radius;
    }
}

/// <summary>
/// How a lamp that is coming back on looks: lit and dark in short steps, lit more and more often, steady at the end. Deterministic in (lamp, age), so every client
/// shows the same lamp the same way and needs only the moment the server said Flickering.
/// </summary>
public static class LampFlicker
{
    public const float StepSeconds = 1f / 12f;

    public static bool On(int lamp, float age, float seconds)
    {
        if (!(seconds > 0) || age >= seconds) return true;
        var step = (uint)Math.Max(0, (int)(age / StepSeconds));
        var h = unchecked((uint)lamp * 2654435761u ^ step * 2246822519u);
        h ^= h >> 15; h = unchecked(h * 2246822519u); h ^= h >> 13;
        return (h & 0xffff) / 65535f < 0.25f + 0.65f * (age / seconds);
    }
}

/// <summary>
/// Wire side of the pole lamps (ADR 0037), over the reliable channel. <see cref="EventKind.LampState"/>: u16 lamp, u8 <see cref="LampState"/>.
/// <see cref="EventKind.LampSync"/>: u16 count, then count times a lamp as above; every lamp not listed is lit (the join catch-up, a new round).
/// </summary>
public static class LampCodec
{
    const int Bytes = 3;

    static void Write(Span<byte> s, LampStateMsg m) { BinaryPrimitives.WriteUInt16LittleEndian(s, m.Index); s[2] = (byte)m.State; }

    public static byte[] EncodeState(LampStateMsg m) { var b = new byte[Bytes]; Write(b, m); return b; }

    public static LampStateMsg? DecodeState(ReadOnlySpan<byte> p) =>
        p.Length == Bytes && p[2] <= (byte)LampState.Flickering ? new LampStateMsg(BinaryPrimitives.ReadUInt16LittleEndian(p), (LampState)p[2]) : null;

    public static byte[] EncodeSync(IReadOnlyList<LampStateMsg> lamps)
    {
        var b = new byte[2 + lamps.Count * Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)lamps.Count);
        for (var i = 0; i < lamps.Count; i++) Write(b.AsSpan(2 + i * Bytes), lamps[i]);
        return b;
    }

    public static List<LampStateMsg>? DecodeSync(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2) return null;
        var n = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (p.Length != 2 + n * Bytes) return null;
        var list = new List<LampStateMsg>(n);
        for (var i = 0; i < n; i++)
        {
            if (DecodeState(p.Slice(2 + i * Bytes, Bytes)) is not { } m) return null;
            list.Add(m);
        }
        return list;
    }
}

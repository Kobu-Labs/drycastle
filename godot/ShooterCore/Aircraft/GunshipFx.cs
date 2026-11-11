using System.Buffers.Binary;
using System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The coalescing limits of the gunship FX (ticket #159). A server tick is 1/30 s; a gunner at 25 rounds a second lands about one
/// round a tick, so these caps only bite on a stall (a backlog of inputs) or with several gunners. Damage is never capped: only the
/// events the clients draw are.
/// </summary>
public static class GunshipFxLimits
{
    /// <summary>Tracer segments one gunner may send in one tick (the rest are fired and resolved, but not drawn).</summary>
    public const int MaxTracersPerTick = 8;
    /// <summary>The farthest a sky shot's tracer is drawn (a round with no hit). A gunship round's range is 3000 m; the streak stops here.</summary>
    public const float MaxSkyTracerM = 1500f;
    /// <summary>Impacts sent in one tick, all gunners together. Kills are always kept, so the cap may be exceeded by kills.</summary>
    public const int MaxImpactsPerTick = 6;
    /// <summary>Blasts one gunner may send in one tick (the smallest are dropped).</summary>
    public const int MaxBlastsPerTick = 8;
}

/// <summary>
/// One round's tracer: the weapons.json index of the round (its sound), where it left, where it stopped, and how it looks in flight
/// (the gunship weapon's <c>tracer_speed_mps</c> and <c>tracer_length_m</c>, #160). Visual only; the damage is the server's.
/// </summary>
public readonly record struct GunshipTracer(byte Weapon, Vector3 Origin, Vector3 End, float SpeedMps, float LengthM);

/// <summary>A blast of a gunship round: its centre and the splash radius it used.</summary>
public readonly record struct GunshipBlast(Vector3 At, float Radius);

/// <summary>An impact of a gunship round, and whether it killed the body or vehicle it hit (a kill is never dropped by the cap).</summary>
public readonly record struct GunshipImpactFx(ImpactInfo Impact, bool Kill);

/// <summary>What a gunner's rounds hit this tick, for the hit marker. A kill also sets <see cref="Hit"/>.</summary>
[Flags]
public enum GunshipHitFlags : byte { None = 0, Hit = 1, Headshot = 2, Kill = 4 }

/// <summary>Payload of <see cref="EventKind.GunshipShots"/>: one gunner's tracers this tick.</summary>
public sealed record GunshipShotsMsg(ushort Owner, IReadOnlyList<GunshipTracer> Tracers);

/// <summary>Payload of <see cref="EventKind.GunshipBlasts"/>: one gunner's blasts this tick and its hit-marker flags.</summary>
public sealed record GunshipBlastsMsg(ushort Owner, GunshipHitFlags Hits, IReadOnlyList<GunshipBlast> Blasts);

/// <summary>
/// The wire payloads of the coalesced gunship FX (kinds 121-123, little-endian). Per server tick, each gunner sends at most one
/// shots message and one blasts message, and everyone gets at most one impacts message:
/// <list type="bullet">
/// <item><b>GunshipShots (121)</b>: u16 owner, u8 count (at most <see cref="GunshipFxLimits.MaxTracersPerTick"/>), then count × (u8 weapon, three f32 origin, three f32 end, u16 speed in m/s, u16 length in cm).</item>
/// <item><b>GunshipImpacts (122)</b>: u8 count, then count × (three f32 point, u8 surface, u8 size with bit 7 set for a kill).</item>
/// <item><b>GunshipBlasts (123)</b>: u16 owner, u8 hit flags (<see cref="GunshipHitFlags"/>), u8 count (at most <see cref="GunshipFxLimits.MaxBlastsPerTick"/>), then count × (three f32 centre, f32 radius).</item>
/// </list>
/// </summary>
public static class GunshipFxCodec
{
    public const int TracerBytes = 1 + 24 + 4, ImpactBytes = 14, BlastBytes = 16;
    const byte KillBit = 0x80;

    public static byte[] EncodeShots(GunshipShotsMsg m)
    {
        if (m.Tracers.Count > GunshipFxLimits.MaxTracersPerTick) throw new ArgumentException("too many tracers for one tick; select them first", nameof(m));
        var b = new byte[3 + m.Tracers.Count * TracerBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Owner);
        b[2] = (byte)m.Tracers.Count;
        var at = 3;
        foreach (var t in m.Tracers)
        {
            b[at] = t.Weapon;
            PutVec(b, at + 1, t.Origin);
            PutVec(b, at + 13, t.End);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 25), (ushort)Math.Clamp(MathF.Round(t.SpeedMps), 0, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 27), (ushort)Math.Clamp(MathF.Round(t.LengthM * 100f), 0, ushort.MaxValue));
            at += TracerBytes;
        }
        return b;
    }

    public static GunshipShotsMsg? DecodeShots(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3) return null;
        var count = d[2];
        if (count > GunshipFxLimits.MaxTracersPerTick || d.Length != 3 + count * TracerBytes) return null;
        var list = new List<GunshipTracer>(count);
        var at = 3;
        for (var i = 0; i < count; i++)
        {
            var speed = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 25)..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 27)..]);
            list.Add(new GunshipTracer(d[at], GetVec(d, at + 1), GetVec(d, at + 13), speed, length / 100f));
            at += TracerBytes;
        }
        return new GunshipShotsMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), list);
    }

    public static byte[] EncodeImpacts(IReadOnlyList<GunshipImpactFx> impacts)
    {
        if (impacts.Count > byte.MaxValue) throw new ArgumentException("too many impacts for one message", nameof(impacts));
        var b = new byte[1 + impacts.Count * ImpactBytes];
        b[0] = (byte)impacts.Count;
        var at = 1;
        foreach (var i in impacts)
        {
            PutVec(b, at, i.Impact.At);
            b[at + 12] = (byte)i.Impact.Surface;
            b[at + 13] = (byte)((byte)i.Impact.Size | (i.Kill ? KillBit : 0));
            at += ImpactBytes;
        }
        return b;
    }

    public static List<GunshipImpactFx>? DecodeImpacts(ReadOnlySpan<byte> d)
    {
        if (d.Length < 1) return null;
        var count = d[0];
        if (d.Length != 1 + count * ImpactBytes) return null;
        var list = new List<GunshipImpactFx>(count);
        var at = 1;
        for (var n = 0; n < count; n++)
        {
            var surface = d[at + 12];
            var size = d[at + 13] & ~KillBit;
            if (surface > (byte)SurfaceKind.Foliage || size > (byte)ImpactSize.Plume) return null;
            list.Add(new GunshipImpactFx(new ImpactInfo(GetVec(d, at), (SurfaceKind)surface, (ImpactSize)size), (d[at + 13] & KillBit) != 0));
            at += ImpactBytes;
        }
        return list;
    }

    public static byte[] EncodeBlasts(GunshipBlastsMsg m)
    {
        if (m.Blasts.Count > GunshipFxLimits.MaxBlastsPerTick) throw new ArgumentException("too many blasts for one tick; select them first", nameof(m));
        var b = new byte[4 + m.Blasts.Count * BlastBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Owner);
        b[2] = (byte)m.Hits;
        b[3] = (byte)m.Blasts.Count;
        var at = 4;
        foreach (var x in m.Blasts)
        {
            PutVec(b, at, x.At);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 12), x.Radius);
            at += BlastBytes;
        }
        return b;
    }

    public static GunshipBlastsMsg? DecodeBlasts(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return null;
        var flags = d[2];
        var count = d[3];
        if ((flags & ~0b111) != 0 || count > GunshipFxLimits.MaxBlastsPerTick || d.Length != 4 + count * BlastBytes) return null;
        var list = new List<GunshipBlast>(count);
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            list.Add(new GunshipBlast(GetVec(d, at), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 12))));
            at += BlastBytes;
        }
        return new GunshipBlastsMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), (GunshipHitFlags)flags, list);
    }

    static void PutVec(byte[] b, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 8), v.Z);
    }

    static Vector3 GetVec(ReadOnlySpan<byte> d, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at)),
        BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 8)));
}

/// <summary>The pure selection rules the server applies before it encodes a tick's FX (tested without Godot).</summary>
public static class GunshipFxSelect
{
    /// <summary>The first tracers of the tick, up to the cap (in firing order).</summary>
    public static List<GunshipTracer> Tracers(IReadOnlyList<GunshipTracer> rounds) => rounds.Take(GunshipFxLimits.MaxTracersPerTick).ToList();

    /// <summary>
    /// Every kill is kept. The rest fill up to <see cref="GunshipFxLimits.MaxImpactsPerTick"/> in size order, biggest first, so the
    /// smallest are dropped first. Within one size the arrival order decides.
    /// </summary>
    public static List<GunshipImpactFx> Impacts(IReadOnlyList<GunshipImpactFx> impacts)
    {
        var kills = impacts.Where(i => i.Kill).ToList();
        var room = Math.Max(0, GunshipFxLimits.MaxImpactsPerTick - kills.Count);
        var rest = impacts.Where(i => !i.Kill).OrderByDescending(i => (int)i.Impact.Size).Take(room);
        return kills.Concat(rest).ToList();
    }

    /// <summary>The biggest blasts (by radius) up to the cap; the rest are dropped.</summary>
    public static List<GunshipBlast> Blasts(IReadOnlyList<GunshipBlast> blasts) => blasts.OrderByDescending(b => b.Radius).Take(GunshipFxLimits.MaxBlastsPerTick).ToList();
}

/// <summary>
/// One tick's gunship FX, collected while the rounds resolve and flushed once at the end of the tick. Per gunner: tracers, blasts
/// and the hit-marker flags; shared: the impacts. Only events call in here, and damage never waits on it: the server applies the
/// damage when a round resolves, and the batch only decides what the clients are told.
/// </summary>
public sealed class GunshipFxBatch
{
    sealed class Gunner
    {
        public readonly List<GunshipTracer> Tracers = new();
        public readonly List<GunshipBlast> Blasts = new();
        public GunshipHitFlags Hits;
    }

    readonly Dictionary<ushort, Gunner> _gunners = new();
    readonly List<GunshipImpactFx> _impacts = new();

    Gunner For(ushort owner)
    {
        if (!_gunners.TryGetValue(owner, out var g)) _gunners[owner] = g = new Gunner();
        return g;
    }

    /// <summary>A round left the turret: its tracer, from the muzzle to where it stopped.</summary>
    public void Tracer(ushort owner, GunshipTracer t) => For(owner).Tracers.Add(t);

    /// <summary>A round burst: its centre and radius.</summary>
    public void Blast(ushort owner, GunshipBlast b) => For(owner).Blasts.Add(b);

    /// <summary>A round hit a body: the gunner's hit marker this tick (a kill also marks the hit).</summary>
    public void Hit(ushort owner, bool headshot, bool kill)
    {
        var g = For(owner);
        g.Hits |= GunshipHitFlags.Hit;
        if (headshot) g.Hits |= GunshipHitFlags.Headshot;
        if (kill) g.Hits |= GunshipHitFlags.Kill;
    }

    /// <summary>A round's impact point, for the clients to draw.</summary>
    public void Impact(GunshipImpactFx i) => _impacts.Add(i);

    /// <summary>
    /// The messages of the tick, in the order to send them: each gunner's shots, the one impacts message, then each gunner's
    /// blasts (with the hit flags). Clears the batch. Empty when nothing happened this tick.
    /// </summary>
    public List<EventMsg> Flush()
    {
        var msgs = new List<EventMsg>();
        foreach (var (owner, g) in _gunners)
            if (g.Tracers.Count > 0)
                msgs.Add(new EventMsg((byte)EventKind.GunshipShots, GunshipFxCodec.EncodeShots(new GunshipShotsMsg(owner, GunshipFxSelect.Tracers(g.Tracers)))));
        if (_impacts.Count > 0)
            msgs.Add(new EventMsg((byte)EventKind.GunshipImpacts, GunshipFxCodec.EncodeImpacts(GunshipFxSelect.Impacts(_impacts))));
        foreach (var (owner, g) in _gunners)
            if (g.Blasts.Count > 0 || g.Hits != GunshipHitFlags.None)
                msgs.Add(new EventMsg((byte)EventKind.GunshipBlasts, GunshipFxCodec.EncodeBlasts(new GunshipBlastsMsg(owner, g.Hits, GunshipFxSelect.Blasts(g.Blasts)))));
        foreach (var g in _gunners.Values) { g.Tracers.Clear(); g.Blasts.Clear(); g.Hits = GunshipHitFlags.None; }
        _impacts.Clear();
        return msgs;
    }
}

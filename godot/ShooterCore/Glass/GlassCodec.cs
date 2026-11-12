using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>A <see cref="EventKind.GlassSync"/>: the panes of <see cref="Scope"/> are whole except <see cref="Panes"/>.</summary>
public sealed record GlassSyncMsg(ushort Scope, IReadOnlyList<GlassPaneState> Panes);

/// <summary>
/// Wire side of glass panes (ADR 0007), over the reliable channel.
/// <see cref="EventKind.GlassPane"/>: u16 owner, u16 index, u8 hp (one pane's state after it changed).
/// <see cref="EventKind.GlassSync"/>: u16 scope, u16 count, then count times a pane as above. Every pane of the scope (an owner, or
/// <see cref="Everything"/>) is whole except the listed ones: the join catch-up, a new round, a vehicle that returned.
/// </summary>
public static class GlassCodec
{
    /// <summary>The scope of a sync that covers every pane. Not an owner: vehicle ids stay below it.</summary>
    public const ushort Everything = ushort.MaxValue;
    const int PaneBytes = 5;

    static void Write(Span<byte> s, GlassPaneState p)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s, p.Key.Owner);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], p.Key.Index);
        s[4] = p.Hp;
    }

    static GlassPaneState Read(ReadOnlySpan<byte> s) =>
        new(new GlassPaneKey(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..])), s[4]);

    public static byte[] EncodePane(GlassPaneState p)
    {
        var b = new byte[PaneBytes];
        Write(b, p);
        return b;
    }

    public static GlassPaneState? DecodePane(ReadOnlySpan<byte> p) => p.Length == PaneBytes ? Read(p) : null;

    public static byte[] EncodeSync(ushort scope, IReadOnlyList<GlassPaneState> panes)
    {
        if (panes.Count > ushort.MaxValue) throw new ArgumentException("too many panes for one sync", nameof(panes));
        var b = new byte[4 + panes.Count * PaneBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, scope);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)panes.Count);
        for (var i = 0; i < panes.Count; i++) Write(b.AsSpan(4 + i * PaneBytes), panes[i]);
        return b;
    }

    public static GlassSyncMsg? DecodeSync(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) return null;
        var scope = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
        if (p.Length != 4 + count * PaneBytes) return null;
        var panes = new List<GlassPaneState>(count);
        for (var i = 0; i < count; i++) panes.Add(Read(p[(4 + i * PaneBytes)..]));
        return new GlassSyncMsg(scope, panes);
    }
}

using System.Buffers.Binary;

namespace Drycastle.ShooterCore.Fences;

/// <summary>A <see cref="Net.EventKind.FenceBreak"/>: the pieces of <see cref="Section"/> that broke this tick (bit j = piece j), and the push of their fall (flat unit direction * 127, m/s * 10).</summary>
public readonly record struct FenceBreakMsg(ushort Section, ulong Pieces, sbyte PushX, sbyte PushZ, byte Speed10);

/// <summary>A <see cref="Net.EventKind.FenceSync"/>: every listed section's broken mask; <see cref="Reset"/> clears every section first (a new round).</summary>
public sealed record FenceSyncMsg(bool Reset, IReadOnlyList<(ushort Section, ulong Broken)> Sections);

/// <summary>
/// Payloads of events 224 FenceBreak and 225 FenceSync (docs/fence-destruction.md Contract 4), little-endian. 224: u16 section, u64 pieces, i8 push x, i8 push z, u8 speed10.
/// 225: u8 flags (bit 0 reset, only on the first message), u16 count, count x (u16 section, u64 broken); at most <see cref="MaxSyncBytes"/> a message. A bad payload decodes to null.
/// </summary>
public static class FenceCodec
{
    public const int BreakBytes = 13, MaxSyncBytes = 200;
    const int SyncHeader = 3, SyncEntry = 10, SyncPerMessage = (MaxSyncBytes - SyncHeader) / SyncEntry;

    public static byte[] EncodeBreak(FenceBreakMsg m)
    {
        var b = new byte[BreakBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Section);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), m.Pieces);
        b[10] = (byte)m.PushX;
        b[11] = (byte)m.PushZ;
        b[12] = m.Speed10;
        return b;
    }

    public static FenceBreakMsg? DecodeBreak(ReadOnlySpan<byte> p) =>
        p.Length != BreakBytes ? null : new FenceBreakMsg(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt64LittleEndian(p[2..]), (sbyte)p[10], (sbyte)p[11], p[12]);

    /// <summary>The 225 messages of <paramref name="m"/>: a reset with nothing broken is one message; no reset and nothing listed is none.</summary>
    public static List<byte[]> EncodeSync(FenceSyncMsg m)
    {
        var res = new List<byte[]>();
        var all = m.Sections;
        for (var at = 0; at < all.Count || (at == 0 && m.Reset); at += SyncPerMessage)
        {
            var n = Math.Min(SyncPerMessage, all.Count - at);
            var b = new byte[SyncHeader + n * SyncEntry];
            b[0] = (byte)(m.Reset && at == 0 ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)n);
            for (var i = 0; i < n; i++)
            {
                var o = SyncHeader + i * SyncEntry;
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), all[at + i].Section);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 2), all[at + i].Broken);
            }
            res.Add(b);
        }
        return res;
    }

    public static FenceSyncMsg? DecodeSync(ReadOnlySpan<byte> p)
    {
        if (p.Length < SyncHeader) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(p[1..]);
        if (p.Length != SyncHeader + n * SyncEntry) return null;
        var list = new List<(ushort Section, ulong Broken)>(n);
        for (var i = 0; i < n; i++)
        {
            var o = SyncHeader + i * SyncEntry;
            list.Add((BinaryPrimitives.ReadUInt16LittleEndian(p[o..]), BinaryPrimitives.ReadUInt64LittleEndian(p[(o + 2)..])));
        }
        return new FenceSyncMsg((p[0] & 1) != 0, list);
    }
}

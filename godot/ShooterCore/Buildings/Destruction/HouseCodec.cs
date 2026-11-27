using System.Buffers.Binary;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>A <see cref="Net.EventKind.HouseCarve"/>: <see cref="Op"/> on house <see cref="House"/>, which is at <see cref="OpSeq"/> with <see cref="Hash"/> after it.</summary>
public readonly record struct HouseCarveMsg(ushort House, ushort OpSeq, CarveOp Op, uint Hash);

/// <summary>
/// One <see cref="Net.EventKind.HouseCells"/> message: a stretch of house <see cref="House"/>'s removed runs (<see cref="HouseCells.EncodeRemoved"/>), from run
/// number <see cref="FirstRun"/> on (even runs are kept cells, odd removed). <see cref="Runs"/> are the raw varints, <see cref="RunCount"/> of them.
/// </summary>
public sealed record HouseCellsMsg(ushort House, ushort OpSeq, uint Hash, uint FirstRun, int RunCount, byte[] Runs);

/// <summary>
/// A <see cref="Net.EventKind.HouseCut"/>: a plough's cells (<see cref="Cells"/>, ascending) on house <see cref="House"/>, cut by vehicle <see cref="Car"/> at server
/// tick <see cref="Tick"/>, the support pass after it when <see cref="Settle"/>; the house is at <see cref="OpSeq"/> with <see cref="Hash"/> after it.
/// </summary>
public sealed record HouseCutMsg(ushort House, ushort OpSeq, uint Hash, bool Settle, uint Tick, ushort Car, int[] Cells);

/// <summary>A <see cref="Net.EventKind.HouseSettings"/>: the rubble mode and whether every house is whole again (round reset).</summary>
public readonly record struct HouseSettingsMsg(bool RubbleStay, bool Reset);

/// <summary>
/// Payloads of events 242 HouseCarve, 243 HouseCells, 244 HouseSettings and 249 HouseResync (docs/house-destruction.md Contract 4), little-endian.
/// 242: u16 house, u16 op seq (after), i16 cx, cy, cz, u16 radius_q, u32 hash (after Settle). 254: u16 house, u16 op seq, u32 hash, u8 flags (bit 0 settle), u32 tick,
/// u16 car, then the cells as varint pairs (gap from the end of the run before, run length) over ascending indices. 243: u16 house, u16 op seq, u32 hash, u32 first run, varint runs;
/// at most <see cref="MaxCellsBytes"/> a message, split at run boundaries. 244: u8 flags (bit 0 rubble stay), u8 reset. 249: u16 house.
/// A decode of a payload of the wrong size or with bad varints returns null.
/// </summary>
public static class HouseCodec
{
    public const int CarveBytes = 16, CellsHeaderBytes = 12, SettingsBytes = 2, ResyncBytes = 2, CutHeaderBytes = 15;
    /// <summary>The largest 243 message.</summary>
    public const int MaxCellsBytes = 200;

    public static byte[] EncodeCarve(HouseCarveMsg m)
    {
        var b = new byte[CarveBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.House);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.OpSeq);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), m.Op.Cx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), m.Op.Cy);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), m.Op.Cz);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), m.Op.RadiusQ);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), m.Hash);
        return b;
    }

    public static HouseCarveMsg? DecodeCarve(ReadOnlySpan<byte> p) =>
        p.Length != CarveBytes ? null : new HouseCarveMsg(
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            new CarveOp(BinaryPrimitives.ReadInt16LittleEndian(p[4..]), BinaryPrimitives.ReadInt16LittleEndian(p[6..]),
                BinaryPrimitives.ReadInt16LittleEndian(p[8..]), BinaryPrimitives.ReadUInt16LittleEndian(p[10..])),
            BinaryPrimitives.ReadUInt32LittleEndian(p[12..]));

    /// <summary>The 243 messages of <paramref name="c"/>'s state: always at least one (an undamaged house is one message with no runs).</summary>
    public static List<byte[]> EncodeCells(HouseCells c, int maxBytes = MaxCellsBytes) =>
        EncodeCells(c.Grid.Index, c.OpSeq, c.Hash(), c.EncodeRemoved(), maxBytes);

    /// <summary>Splits <paramref name="runs"/> (varints) into 243 messages of at most <paramref name="maxBytes"/>, each starting at a run boundary.</summary>
    public static List<byte[]> EncodeCells(ushort house, ushort opSeq, uint hash, ReadOnlySpan<byte> runs, int maxBytes = MaxCellsBytes)
    {
        if (maxBytes < CellsHeaderBytes + 5) throw new ArgumentOutOfRangeException(nameof(maxBytes), "room for at least one varint");
        var list = new List<byte[]>();
        uint run = 0, first = 0;
        int start = 0, p = 0;
        void Flush(ReadOnlySpan<byte> r, int end)
        {
            var b = new byte[CellsHeaderBytes + end - start];
            BinaryPrimitives.WriteUInt16LittleEndian(b, house);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), opSeq);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), first);
            r[start..end].CopyTo(b.AsSpan(CellsHeaderBytes));
            list.Add(b);
        }
        while (p < runs.Length)
        {
            var at = p;
            Varint.Read(runs, ref p);
            if (CellsHeaderBytes + p - start > maxBytes) { Flush(runs, at); start = at; first = run; }
            run++;
        }
        Flush(runs, runs.Length);
        return list;
    }

    public static HouseCellsMsg? DecodeCells(ReadOnlySpan<byte> p)
    {
        if (p.Length < CellsHeaderBytes) return null;
        var runs = p[CellsHeaderBytes..];
        var n = 0;
        try { for (var q = 0; q < runs.Length; n++) Varint.Read(runs, ref q); }
        catch (InvalidDataException) { return null; }
        return new HouseCellsMsg(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[4..]), BinaryPrimitives.ReadUInt32LittleEndian(p[8..]), n, runs.ToArray());
    }

    /// <summary>254. <see cref="HouseCutMsg.Cells"/> must be ascending and distinct.</summary>
    public static byte[] EncodeCut(HouseCutMsg m)
    {
        var o = new List<byte>(CutHeaderBytes + m.Cells.Length / 2 + 8);
        Span<byte> h = stackalloc byte[CutHeaderBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(h, m.House);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], m.OpSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], m.Hash);
        h[8] = (byte)(m.Settle ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[9..], m.Tick);
        BinaryPrimitives.WriteUInt16LittleEndian(h[13..], m.Car);
        foreach (var b in h) o.Add(b);
        var end = 0;
        for (var k = 0; k < m.Cells.Length;)
        {
            var start = m.Cells[k];
            var len = 1;
            while (k + len < m.Cells.Length && m.Cells[k + len] == start + len) len++;
            Varint.Write(o, (uint)(start - end));
            Varint.Write(o, (uint)len);
            end = start + len;
            k += len;
        }
        return o.ToArray();
    }

    public static HouseCutMsg? DecodeCut(ReadOnlySpan<byte> p)
    {
        if (p.Length < CutHeaderBytes) return null;
        var cells = new List<int>();
        try
        {
            var runs = p[CutHeaderBytes..];
            long end = 0;
            for (var q = 0; q < runs.Length;)
            {
                var gap = Varint.Read(runs, ref q);
                if (q >= runs.Length) return null;
                var len = Varint.Read(runs, ref q);
                var start = end + gap;
                if (len == 0 || start + len > int.MaxValue || cells.Count + (long)len > 1 << 20) return null;
                for (var i = 0; i < len; i++) cells.Add((int)(start + i));
                end = start + len;
            }
        }
        catch (InvalidDataException) { return null; }
        return new HouseCutMsg(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p[2..]), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            (p[8] & 1) != 0, BinaryPrimitives.ReadUInt32LittleEndian(p[9..]), BinaryPrimitives.ReadUInt16LittleEndian(p[13..]), cells.ToArray());
    }

    public static byte[] EncodeSettings(HouseSettingsMsg m) => [(byte)(m.RubbleStay ? 1 : 0), (byte)(m.Reset ? 1 : 0)];

    public static HouseSettingsMsg? DecodeSettings(ReadOnlySpan<byte> p) =>
        p.Length != SettingsBytes ? null : new HouseSettingsMsg((p[0] & 1) != 0, p[1] == 1);

    public static byte[] EncodeResync(ushort house)
    {
        var b = new byte[ResyncBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, house);
        return b;
    }

    public static ushort? DecodeResync(ReadOnlySpan<byte> p) => p.Length != ResyncBytes ? null : BinaryPrimitives.ReadUInt16LittleEndian(p);
}

/// <summary>The outcome of one 243 message given to <see cref="HouseCellsCatchUp.Add"/>.</summary>
public enum CatchUpStep
{
    /// <summary>Applied so far; more messages to come (the hash does not match yet).</summary>
    Pending,
    /// <summary>The house now equals the server's (op seq and hash).</summary>
    Done,
    /// <summary>A message is missing or does not belong to the set in progress, or the runs do not fit the house: ask for a resync (249).</summary>
    Broken,
}

/// <summary>
/// The client side of one house's 243 set. A message with first run 0 starts a new set; each next one must carry the same op seq and hash and go on
/// where the last ended. After each message the runs so far are applied (<see cref="HouseCells.DecodeRemoved"/>); the set is done when the house's hash
/// matches the server's (a cut-short set lacks removed cells, since the encoder leaves out only the trailing kept run).
/// </summary>
public sealed class HouseCellsCatchUp
{
    readonly List<byte> _runs = new();
    uint _next;
    bool _open;
    ushort _opSeq;
    uint _hash;

    public CatchUpStep Add(HouseCellsMsg m, HouseCells into)
    {
        if (m.FirstRun == 0) { _runs.Clear(); _next = 0; _open = true; _opSeq = m.OpSeq; _hash = m.Hash; }
        else if (!_open || m.FirstRun != _next || m.OpSeq != _opSeq || m.Hash != _hash) { _open = false; return CatchUpStep.Broken; }
        _runs.AddRange(m.Runs);
        _next += (uint)m.RunCount;
        try { into.DecodeRemoved(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_runs), m.OpSeq); }
        catch (InvalidDataException) { _open = false; return CatchUpStep.Broken; }
        if (into.Hash() != m.Hash) return CatchUpStep.Pending;
        _open = false;
        _runs.Clear();
        return CatchUpStep.Done;
    }
}

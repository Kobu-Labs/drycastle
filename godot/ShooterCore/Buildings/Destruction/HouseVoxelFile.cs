using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// house_voxels.bin (docs/house-destruction.md Contract 1): magic "HVOX", u16 version 1, u16 house count, then per house (sorted by id, the index is the wire id)
/// its id, grid corner, yaw, cell size, size, palette and an RLE of (u8 palette index, varint run). Little-endian. A bad file throws <see cref="InvalidDataException"/>.
/// </summary>
public static class HouseVoxelFile
{
    public const ushort Version = 1;
    static ReadOnlySpan<byte> Magic => "HVOX"u8;

    public static IReadOnlyList<HouseGrid> Read(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var d = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var p = 0;
        if (d.Length < 8 || !d[..4].SequenceEqual(Magic)) throw new InvalidDataException("house_voxels.bin: no HVOX magic");
        p = 4;
        var version = U16(d, ref p);
        if (version != Version) throw new InvalidDataException($"house_voxels.bin: version {version}, this game reads {Version}");
        int count = U16(d, ref p);
        var houses = new List<HouseGrid>(count);
        string? prev = null;
        for (var h = 0; h < count; h++)
        {
            int idLen = U16(d, ref p);
            Need(d, p, idLen);
            var id = Encoding.UTF8.GetString(d.Slice(p, idLen)); p += idLen;
            if (prev != null && string.CompareOrdinal(prev, id) >= 0) throw new InvalidDataException($"house_voxels.bin: houses not sorted by id at {id}");
            prev = id;
            var origin = new Vector3(F32(d, ref p), F32(d, ref p), F32(d, ref p));
            var yaw = F32(d, ref p);
            var cell = F32(d, ref p);
            int nx = U16(d, ref p), ny = U16(d, ref p), nz = U16(d, ref p);
            Need(d, p, 1);
            int pc = d[p++];
            if (pc is 0 or > 64) throw new InvalidDataException($"house_voxels.bin: house {id} has {pc} palette entries");
            Need(d, p, pc * 4);
            var palette = new (CellMaterial, byte, byte, byte)[pc];
            for (var i = 0; i < pc; i++)
            {
                var m = d[p];
                if (m > (byte)CellMaterial.Roof) throw new InvalidDataException($"house_voxels.bin: house {id} palette material {m}");
                palette[i] = ((CellMaterial)m, d[p + 1], d[p + 2], d[p + 3]);
                p += 4;
            }
            var rleBytes = (int)U32(d, ref p);
            Need(d, p, rleBytes);
            var rle = d.Slice(p, rleBytes); p += rleBytes;
            var n = (long)nx * ny * nz;
            if (n == 0 || n > 1 << 26) throw new InvalidDataException($"house_voxels.bin: house {id} size {nx}x{ny}x{nz}");
            var cells = new byte[n];
            long at = 0;
            var q = 0;
            while (q < rle.Length)
            {
                var idx = rle[q++];
                var run = Varint.Read(rle, ref q);
                if (idx >= pc) throw new InvalidDataException($"house_voxels.bin: house {id} palette index {idx} of {pc}");
                if (run == 0 || at + run > n) throw new InvalidDataException($"house_voxels.bin: house {id} RLE run overflows its cells");
                cells.AsSpan((int)at, (int)run).Fill(idx);
                at += run;
            }
            if (at != n) throw new InvalidDataException($"house_voxels.bin: house {id} RLE covers {at} of {n} cells");
            try { houses.Add(new HouseGrid((ushort)h, id, origin, yaw, cell, nx, ny, nz, palette, cells)); }
            catch (ArgumentException e) { throw new InvalidDataException("house_voxels.bin: " + e.Message, e); }
        }
        if (p != d.Length) throw new InvalidDataException($"house_voxels.bin: {d.Length - p} bytes after the last house");
        return houses;
    }

    /// <summary>Writes <paramref name="houses"/> in their order, which must be sorted by id (ordinal); the reader numbers them by position. For tests and fixtures.</summary>
    public static void Write(Stream s, IReadOnlyList<HouseGrid> houses)
    {
        if (houses.Count > ushort.MaxValue) throw new ArgumentException("too many houses");
        var o = new List<byte>();
        o.AddRange(Magic);
        PutU16(o, Version);
        PutU16(o, (ushort)houses.Count);
        string? prev = null;
        foreach (var g in houses)
        {
            if (prev != null && string.CompareOrdinal(prev, g.Id) >= 0) throw new ArgumentException($"houses must be sorted by id, {g.Id} after {prev}");
            prev = g.Id;
            var id = Encoding.UTF8.GetBytes(g.Id);
            PutU16(o, (ushort)id.Length);
            o.AddRange(id);
            PutF32(o, g.Origin.X); PutF32(o, g.Origin.Y); PutF32(o, g.Origin.Z);
            PutF32(o, g.Yaw); PutF32(o, g.Cell);
            PutU16(o, (ushort)g.Nx); PutU16(o, (ushort)g.Ny); PutU16(o, (ushort)g.Nz);
            var pal = g.Palette.Span;
            o.Add((byte)pal.Length);
            foreach (var e in pal) { o.Add((byte)e.Material); o.Add(e.R); o.Add(e.G); o.Add(e.B); }
            var rle = new List<byte>();
            var cells = g.Cells.Span;
            for (var i = 0; i < cells.Length;)
            {
                var j = i;
                while (j < cells.Length && cells[j] == cells[i]) j++;
                rle.Add(cells[i]);
                Varint.Write(rle, (uint)(j - i));
                i = j;
            }
            PutU32(o, (uint)rle.Count);
            o.AddRange(rle);
        }
        s.Write(o.ToArray());
    }

    static void Need(ReadOnlySpan<byte> d, int p, int n)
    {
        if (n < 0 || p + n > d.Length) throw new InvalidDataException("house_voxels.bin: cut short");
    }
    static ushort U16(ReadOnlySpan<byte> d, ref int p) { Need(d, p, 2); var v = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]); p += 2; return v; }
    static uint U32(ReadOnlySpan<byte> d, ref int p) { Need(d, p, 4); var v = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]); p += 4; return v; }
    static float F32(ReadOnlySpan<byte> d, ref int p) { Need(d, p, 4); var v = BinaryPrimitives.ReadSingleLittleEndian(d[p..]); p += 4; return v; }
    static void PutU16(List<byte> o, ushort v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
    static void PutU32(List<byte> o, uint v) { for (var i = 0; i < 4; i++) o.Add((byte)(v >> (8 * i))); }
    static void PutF32(List<byte> o, float v) => PutU32(o, BitConverter.SingleToUInt32Bits(v));
}

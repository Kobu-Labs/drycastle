namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Unsigned LEB128 varints (7 bits a byte, low first), for the RLE of house_voxels.bin and the removed-cell runs on the wire.</summary>
internal static class Varint
{
    public static void Write(List<byte> o, uint v)
    {
        while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }

    /// <summary>Reads one varint at <paramref name="p"/> and moves past it; throws <see cref="InvalidDataException"/> when cut short or longer than 5 bytes.</summary>
    public static uint Read(ReadOnlySpan<byte> d, ref int p)
    {
        uint v = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (p >= d.Length) throw new InvalidDataException("varint cut short");
            var b = d[p++];
            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
        throw new InvalidDataException("varint too long");
    }
}

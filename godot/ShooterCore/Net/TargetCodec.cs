using System.Buffers.Binary;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Target dummies on the wire (#148). The roster (<see cref="EventKind.TargetRoster"/>) is sent on join: u16 count, then per
/// target u16 id and a name (u8 length, utf8, clamped to 255 bytes). Positions, health, state and pose ride in the snapshot
/// as entity type <see cref="EntityType.Target"/>.
/// </summary>
public static class TargetCodec
{
    public static byte[] EncodeRoster(IReadOnlyList<(ushort Id, string Name)> targets)
    {
        var o = new List<byte>();
        var count = (ushort)Math.Min(targets.Count, ushort.MaxValue);
        o.Add((byte)count); o.Add((byte)(count >> 8));
        foreach (var (id, name) in targets.Take(count))
        {
            o.Add((byte)id); o.Add((byte)(id >> 8));
            var bytes = Encoding.UTF8.GetBytes(name);
            var len = Math.Min(bytes.Length, 255);
            o.Add((byte)len);
            o.AddRange(bytes.AsSpan(0, len).ToArray());
        }
        return o.ToArray();
    }

    /// <summary>The roster, or null if the payload is malformed.</summary>
    public static List<(ushort Id, string Name)>? DecodeRoster(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var o = 2;
        var list = new List<(ushort, string)>(count);
        for (var i = 0; i < count; i++)
        {
            if (o + 3 > d.Length) return null;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            var len = d[o + 2];
            o += 3;
            if (o + len > d.Length) return null;
            list.Add((id, Encoding.UTF8.GetString(d.Slice(o, len))));
            o += len;
        }
        return list;
    }
}

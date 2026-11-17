using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// What a radar unit tells its operator, about ten times a second while the dish turns (<see cref="EventKind.RadarSweep"/>, to the operator only). <paramref name="Angle"/> is the sweep arm's bearing at
/// the moment of sending (radians, 0 along -Z, clockwise), <paramref name="Revolution"/> seconds per turn (the client carries the arm on from there). <paramref name="Swept"/> are the enemies the sweep has
/// found within the last revolution, at the place it found them, with their age; <paramref name="Friendlies"/> are the operator's teammates (team deathmatch), live.
/// </summary>
public sealed record RadarSweepMsg(ushort Unit, float Angle, float Revolution, IReadOnlyList<RadarContact> Swept, IReadOnlyList<RadarBlip> Friendlies);

/// <summary>
/// Payload of <see cref="EventKind.RadarSweep"/> (little endian): u16 radar unit (vehicle id), f32 angle, f32 revolution seconds, u16 swept count, per swept contact u16 id, u8 <see cref="RadarKind"/>,
/// f32 x, f32 z, u8 age in 1/40 s (a revolution is at most 6.3 s, so it never saturates for the shipped 6 s); u16 friendly count, per friendly u16 id, u8 kind, f32 x, f32 z. 14 bytes plus 12 per swept
/// contact and 11 per friendly. Decoding returns null on a short or malformed payload.
/// </summary>
public static class RadarCodec
{
    /// <summary>Age resolution on the wire: 1/40 s steps in one byte.</summary>
    public const float AgeStep = 1f / 40f;
    const int Header = 2 + 4 + 4 + 2;
    const int PerSwept = 2 + 1 + 4 + 4 + 1;
    const int PerFriend = 2 + 1 + 4 + 4;

    public static byte[] Encode(RadarSweepMsg m)
    {
        var swept = Math.Min(m.Swept.Count, ushort.MaxValue);
        var friends = Math.Min(m.Friendlies.Count, ushort.MaxValue);
        var b = new byte[Header + swept * PerSwept + 2 + friends * PerFriend];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), m.Unit);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(2), m.Angle);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), m.Revolution);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)swept);
        var at = Header;
        for (var i = 0; i < swept; i++, at += PerSwept)
        {
            var c = m.Swept[i];
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), c.Id);
            b[at + 2] = (byte)c.Kind;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 3), c.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 7), c.Z);
            b[at + 11] = (byte)Math.Clamp((int)MathF.Round(c.Age / AgeStep), 0, 255);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)friends);
        at += 2;
        for (var i = 0; i < friends; i++, at += PerFriend)
        {
            var f = m.Friendlies[i];
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), f.Id);
            b[at + 2] = (byte)f.Kind;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 3), f.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 7), f.Z);
        }
        return b;
    }

    public static RadarSweepMsg? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < Header) return null;
        var unit = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var angle = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(2));
        var rev = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(6));
        var swept = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(10));
        if (!float.IsFinite(angle) || !float.IsFinite(rev) || d.Length < Header + swept * PerSwept + 2) return null;
        var list = new List<RadarContact>(swept);
        var at = Header;
        for (var i = 0; i < swept; i++, at += PerSwept)
        {
            if (d[at + 2] > (byte)RadarKind.Vehicle) return null;
            list.Add(new RadarContact(BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at)), (RadarKind)d[at + 2],
                BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 3)), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 7)), d[at + 11] * AgeStep));
        }
        var friends = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at));
        at += 2;
        if (d.Length < at + friends * PerFriend) return null;
        var fl = new List<RadarBlip>(friends);
        for (var i = 0; i < friends; i++, at += PerFriend)
        {
            if (d[at + 2] > (byte)RadarKind.Vehicle) return null;
            fl.Add(new RadarBlip(BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at)), (RadarKind)d[at + 2],
                BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 3)), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(at + 7))));
        }
        return new RadarSweepMsg(unit, angle, rev, list, fl);
    }
}

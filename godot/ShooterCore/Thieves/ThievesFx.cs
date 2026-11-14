using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>A one-shot effect of the Thieves event (event kind <see cref="EventKind.ThievesFx"/>). Honk: a Raid van sounds its horn (the id is the van's world entity id).</summary>
public enum ThievesFxKind : byte { Honk = 0 }

/// <summary>What happened and where; <paramref name="Id"/> is the world entity it belongs to.</summary>
public readonly record struct ThievesFxMsg(ThievesFxKind Kind, Vector3 Position, ushort Id);

/// <summary>Payload of <see cref="EventKind.ThievesFx"/>: u8 kind, i32 x, i32 y, i32 z (<see cref="Quant.Pos(float)"/>), u16 id (15 bytes).</summary>
public static class ThievesFxCodec
{
    public const int Bytes = 15;

    public static byte[] Encode(ThievesFxMsg m)
    {
        var b = new byte[Bytes];
        b[0] = (byte)m.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), Quant.Pos(m.Position.X));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), Quant.Pos(m.Position.Y));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), Quant.Pos(m.Position.Z));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), m.Id);
        return b;
    }

    public static ThievesFxMsg? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes || !Enum.IsDefined((ThievesFxKind)d[0])) return null;
        var pos = new Vector3(Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[1..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[5..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[9..])));
        return new ThievesFxMsg((ThievesFxKind)d[0], pos, BinaryPrimitives.ReadUInt16LittleEndian(d[13..]));
    }
}

using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a player sees of their seat: nothing near, a free seat in reach (the prompt), or sitting in one.</summary>
public enum MassSeatState : byte { None = 0, CanSit = 1, Seated = 2 }

/// <summary>The Mass as every client sees it: the phase, whether the chant plays, how often the pastor was killed, where the chant comes from, and (once, at the start) whether the bells ring.</summary>
public readonly record struct MassStateMsg(MassPhase Phase, bool Chanting, byte PastorKills, Vector3 ChantAt, bool Bells = false);

/// <summary>To the player: their seat state, and the way the seat faces (the look turns to it as they sit).</summary>
public readonly record struct MassSeatMsg(MassSeatState State, float Yaw);

/// <summary>Wire format of the Holy Mass events (<see cref="EventKind.MassState"/>, <see cref="EventKind.MassSeat"/>, <see cref="EventKind.MassBlessing"/>). Little-endian.</summary>
public static class MassCodec
{
    public const byte FlagChanting = 1, FlagBells = 2;

    /// <summary>u8 phase, u8 flags, u8 pastor kills, i32 x, y, z of the chant (1/128 m).</summary>
    public static byte[] EncodeState(MassStateMsg m)
    {
        var b = new byte[15];
        b[0] = (byte)m.Phase;
        b[1] = (byte)((m.Chanting ? FlagChanting : 0) | (m.Bells ? FlagBells : 0));
        b[2] = m.PastorKills;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(3), Quant.Pos(m.ChantAt.X));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(7), Quant.Pos(m.ChantAt.Y));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(11), Quant.Pos(m.ChantAt.Z));
        return b;
    }

    public static MassStateMsg? DecodeState(ReadOnlySpan<byte> d)
    {
        if (d.Length != 15 || d[0] > (byte)MassPhase.Void) return null;
        return new MassStateMsg((MassPhase)d[0], (d[1] & FlagChanting) != 0, d[2],
            new Vector3(Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[3..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[7..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[11..]))),
            (d[1] & FlagBells) != 0);
    }

    /// <summary>u8 state, f32 yaw.</summary>
    public static byte[] EncodeSeat(MassSeatMsg m)
    {
        var b = new byte[5];
        b[0] = (byte)m.State;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), m.Yaw);
        return b;
    }

    public static MassSeatMsg? DecodeSeat(ReadOnlySpan<byte> d) =>
        d.Length == 5 && d[0] <= (byte)MassSeatState.Seated ? new MassSeatMsg((MassSeatState)d[0], BinaryPrimitives.ReadSingleLittleEndian(d[1..])) : null;

    /// <summary>f32 yaw: the way the blessed player faces at the church door.</summary>
    public static byte[] EncodeBlessing(float yaw)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, yaw);
        return b;
    }

    public static float? DecodeBlessing(ReadOnlySpan<byte> d) => d.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(d) : null;
}

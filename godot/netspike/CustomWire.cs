using System.Buffers.Binary;
using Godot;

/// <summary>
/// Wire format of approach B (all little-endian).
///
/// Client -> server, channel 1, unreliable-sequenced, 8 bytes:
///   u8 type=10, u16 seq, i8 mx, i8 mz, u16 yaw, u8 jump
/// Server -> client, channel 0, reliable control:
///   u8 type=1 welcome: u8 id, f32 bounds_min[3]      (14 bytes)
///   u8 type=2 go / u8 type=3 stop
/// Server -> client, channel 1, unreliable-sequenced snapshot:
///   u8 type=20, u32 tick, u16 ackSeq, u8 count, then count entries:
///     u8 id, u8 flags, then per flag
///       F_POS_ABS   (1): u16 x, u16 y, u16 z    (1/128 m units above bounds_min)
///       F_POS_DELTA (2): i8 dx, i8 dy, i8 dz    (vs the last state sent to this client)
///       F_YAW       (4): u16 yaw
///       F_VEL       (8): i8 vx, i8 vy, i8 vz    (1/8 m/s)
///   Entities unchanged since the last send are omitted; a full entry is sent for new/re-entering
///   entities (interest management) and every KeyEvery ticks (loss recovery).
/// </summary>
public static class Wire
{
    public const byte TWelcome = 1, TGo = 2, TStop = 3, TInput = 10, TSnap = 20;
    public const byte FPosAbs = 1, FPosDelta = 2, FYaw = 4, FVel = 8;
    public const int ChanCtl = 0, ChanData = 1, MaxEntries = 90, KeyEvery = 30;
    public const int Channels = 2;

    public struct Ent
    {
        public ushort X, Y, Z, Yaw; public sbyte Vx, Vy, Vz; public bool Valid; public int LastTick;
    }

    public static Ent Quantise(NetPlayer p, Vector3 bmin)
    {
        return new Ent
        {
            X = Net.QPos(p.Position.X, bmin.X), Y = Net.QPos(p.Position.Y, bmin.Y), Z = Net.QPos(p.Position.Z, bmin.Z),
            Yaw = Net.QYaw(p.Rotation.Y),
            Vx = Net.QVel(p.Velocity.X), Vy = Net.QVel(p.Velocity.Y), Vz = Net.QVel(p.Velocity.Z),
            Valid = true,
        };
    }

    public static byte[] Input(int seq, int mx, int mz, int yawQ, int jump)
    {
        var b = new byte[8];
        b[0] = TInput;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)seq);
        b[3] = (byte)(sbyte)mx; b[4] = (byte)(sbyte)mz;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)yawQ);
        b[7] = (byte)jump;
        return b;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>Event kinds carried in the reliable event envelope. Payload codecs are added by the issues that use them.</summary>
public enum EventKind : byte
{
    None = 0,
    Spawn = 1,
    Shot = 2,
    Hit = 3,
    Kill = 4,
    MatchState = 5,
    Explosion = 6,
    Death = 7,
    /// <summary>Client to server developer commands (only honoured when the server runs with --debug-cmds).</summary>
    Debug = 8,
    /// <summary>Server to the owning client: ammo, slot and reload state.</summary>
    Ammo = 9,
    /// <summary>Server broadcast: a rocket was launched (projectile id, owner, origin, velocity).</summary>
    Launch = 10,
}

public abstract record ReliableMsg;
/// <summary>Client to server, first message after connecting.</summary>
public sealed record HelloMsg(byte Version, string Name) : ReliableMsg;
/// <summary>Server to client, tells the client which entity id it is and the tick rate.</summary>
public sealed record WelcomeMsg(byte Version, ushort PlayerId, byte TickRate, uint ServerTick) : ReliableMsg;
public sealed record JoinMsg(ushort PlayerId, string Name) : ReliableMsg;
public sealed record LeaveMsg(ushort PlayerId) : ReliableMsg;
/// <summary>Event envelope: a type byte plus an opaque payload decoded by the owner of that kind.</summary>
public sealed record EventMsg(byte Kind, byte[] Payload) : ReliableMsg;

/// <summary>
/// Wire format (little-endian). Channel 0 is reliable (control + events), channel 1 is unreliable-sequenced (input, snapshots).
///
/// ch0 messages: u8 type, then
///   Hello(1):   u8 version, u8 nameLen, name utf8
///   Welcome(2): u8 version, u16 playerId, u8 tickRate, u32 serverTick
///   Join(3):    u16 playerId, u8 nameLen, name utf8
///   Leave(4):   u16 playerId
///   Event(5):   u8 kind, u16 len, payload
/// ch1 client to server, Input(10): u8 count (1..3, newest last), count * 11 bytes:
///   u16 seq, i8 mx, i8 my, u16 yaw, i16 pitch, u16 buttons, u8 slot
/// ch1 server to client, Snapshot(20): u32 tick, u16 ackSeq, u8 count, then count entities:
///   u16 id, u8 type, u8 flags, then per flag
///     PosAbs(1):   i32 x, i32 y, i32 z   (1/128 m)
///     PosDelta(2): i16 dx, dy, dz        (vs the last state sent to this client)
///     Rot(4):      u16 yaw, i16 pitch
///     Status(8):   u8 health, u8 state, u8 weapon
///   Unchanged entities are omitted; a keyframe (all of the above, absolute) is sent for new entities and every 30 ticks.
/// </summary>
public static class Wire
{
    public const byte ProtocolVersion = 1;
    public const byte TickRate = 30;
    public const int ChanReliable = 0, ChanData = 1, Channels = 2;
    public const int KeyEvery = 30;
    public const int MaxInputsPerPacket = 3;
    public const int MaxNameBytes = 24;
    public const int InputBytes = 11;

    public const byte THello = 1, TWelcome = 2, TJoin = 3, TLeave = 4, TEvent = 5, TInput = 10, TSnapshot = 20;
    public const byte FPosAbs = 1, FPosDelta = 2, FRot = 4, FStatus = 8;

    static string ClampName(string n)
    {
        var b = Encoding.UTF8.GetBytes(n ?? "");
        if (b.Length <= MaxNameBytes) return n ?? "";
        var s = Encoding.UTF8.GetString(b, 0, MaxNameBytes);
        return s.TrimEnd('�');
    }

    static void WriteName(List<byte> o, string name)
    {
        var b = Encoding.UTF8.GetBytes(ClampName(name));
        o.Add((byte)b.Length);
        o.AddRange(b);
    }

    public static byte[] Encode(ReliableMsg m)
    {
        var o = new List<byte>();
        Span<byte> t = stackalloc byte[4];
        switch (m)
        {
            case HelloMsg h: o.Add(THello); o.Add(h.Version); WriteName(o, h.Name); break;
            case WelcomeMsg w:
                o.Add(TWelcome); o.Add(w.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(t, w.PlayerId); o.AddRange(t[..2].ToArray());
                o.Add(w.TickRate);
                BinaryPrimitives.WriteUInt32LittleEndian(t, w.ServerTick); o.AddRange(t.ToArray());
                break;
            case JoinMsg j:
                o.Add(TJoin);
                BinaryPrimitives.WriteUInt16LittleEndian(t, j.PlayerId); o.AddRange(t[..2].ToArray());
                WriteName(o, j.Name);
                break;
            case LeaveMsg l:
                o.Add(TLeave);
                BinaryPrimitives.WriteUInt16LittleEndian(t, l.PlayerId); o.AddRange(t[..2].ToArray());
                break;
            case EventMsg e:
                if (e.Payload.Length > ushort.MaxValue) throw new ArgumentException("event payload too large");
                o.Add(TEvent); o.Add(e.Kind);
                BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)e.Payload.Length); o.AddRange(t[..2].ToArray());
                o.AddRange(e.Payload);
                break;
            default: throw new ArgumentException("unknown message");
        }
        return o.ToArray();
    }

    /// <summary>Decode a reliable message; null if malformed or unknown.</summary>
    public static ReliableMsg? DecodeReliable(ReadOnlySpan<byte> d)
    {
        try
        {
            if (d.Length == 0) return null;
            switch (d[0])
            {
                case THello:
                {
                    int n = d[2];
                    return new HelloMsg(d[1], Encoding.UTF8.GetString(d.Slice(3, n)));
                }
                case TWelcome:
                    return new WelcomeMsg(d[1], BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d[4], BinaryPrimitives.ReadUInt32LittleEndian(d[5..]));
                case TJoin:
                {
                    int n = d[3];
                    return new JoinMsg(BinaryPrimitives.ReadUInt16LittleEndian(d[1..]), Encoding.UTF8.GetString(d.Slice(4, n)));
                }
                case TLeave: return new LeaveMsg(BinaryPrimitives.ReadUInt16LittleEndian(d[1..]));
                case TEvent:
                {
                    int n = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
                    return new EventMsg(d[1], d.Slice(4, n).ToArray());
                }
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return null;
    }

    /// <summary>Encode the newest inputs (up to 3, oldest first) as one packet.</summary>
    public static byte[] EncodeInputs(ReadOnlySpan<NetInput> inputs)
    {
        var n = Math.Min(inputs.Length, MaxInputsPerPacket);
        var b = new byte[2 + n * InputBytes];
        b[0] = TInput; b[1] = (byte)n;
        for (var i = 0; i < n; i++)
        {
            var s = b.AsSpan(2 + i * InputBytes);
            var x = inputs[inputs.Length - n + i];
            BinaryPrimitives.WriteUInt16LittleEndian(s, x.Seq);
            s[2] = (byte)x.MoveX; s[3] = (byte)x.MoveY;
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], x.Yaw);
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], x.Pitch);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], x.Buttons);
            s[10] = x.Slot;
        }
        return b;
    }

    public static bool TryDecodeInputs(ReadOnlySpan<byte> d, List<NetInput> into)
    {
        if (d.Length < 2 || d[0] != TInput) return false;
        int n = d[1];
        if (n < 1 || n > MaxInputsPerPacket || d.Length < 2 + n * InputBytes) return false;
        for (var i = 0; i < n; i++)
        {
            var s = d[(2 + i * InputBytes)..];
            into.Add(new NetInput
            {
                Seq = BinaryPrimitives.ReadUInt16LittleEndian(s),
                MoveX = (sbyte)s[2], MoveY = (sbyte)s[3],
                Yaw = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(s[6..]),
                Buttons = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
                Slot = s[10],
            });
        }
        return true;
    }
}

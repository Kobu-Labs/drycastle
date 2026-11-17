using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a crew member asks of the launcher (client to server, <see cref="EventKind.ScudCommand"/>).</summary>
public enum ScudCmd : byte
{
    /// <summary>The operator opens the targeting screen.</summary>
    Open = 0,
    /// <summary>The operator closes it.</summary>
    Close = 1,
    /// <summary>The operator's camera: centre (x, z) and zoom, a few times a second while panning.</summary>
    View = 2,
    /// <summary>The operator locks the point (x, z): the proposal.</summary>
    Lock = 3,
    /// <summary>Any crew member confirms the proposal.</summary>
    Confirm = 4,
    /// <summary>A crew member who confirmed takes it back: the proposal is dropped.</summary>
    Withdraw = 5,
    /// <summary>The operator aborts the rising rack.</summary>
    Abort = 6,
}

/// <summary>A crew member's command. <see cref="X"/> and <see cref="Z"/> are the ground point of a Lock or the camera centre of a View; <see cref="Zoom"/> the camera's zoom.</summary>
public readonly record struct ScudCommandMsg(ScudCmd Cmd, float X = 0, float Z = 0, float Zoom = 1);

/// <summary>One launcher as everyone draws it (<see cref="EventKind.ScudState"/>). <see cref="Left"/> is infinity when the state has no countdown.</summary>
public sealed record ScudStateMsg(ushort Car, ScudLauncherState State, float Elapsed, float Left, float RackFrom, ushort Operator, Vector3? Lock, byte Confirmed, byte Need);

/// <summary>The operator's camera, shown read-only to the rest of the crew (<see cref="EventKind.ScudView"/>).</summary>
public sealed record ScudViewMsg(ushort Car, float X, float Z, float Zoom);

/// <summary>A missile left the rack (<see cref="EventKind.ScudLaunch"/>): the entity that flies it, the truck, who placed the lock, where it left and where it lands, and how long it flies.</summary>
public sealed record ScudLaunchMsg(ushort Missile, ushort Car, ushort Placer, Vector3 From, Vector3 Target, float Seconds);

/// <summary>The missile landed (<see cref="EventKind.ScudImpact"/>): the blast's centre and who placed the lock.</summary>
public sealed record ScudImpactMsg(ushort Missile, ushort Placer, Vector3 At);

/// <summary>Why a command did nothing, or why a proposal ended (<see cref="EventKind.ScudNotice"/>, to the crew).</summary>
public enum ScudNoticeCode : byte
{
    TooClose = 1, OffMap = 2, Moving = 3, NotOperator = 4, WrongState = 5,
    CrewChanged = 6, Withdrawn = 7, Moved = 8, Timeout = 9, Retarget = 10,
    /// <summary>A hold on a rocket cart began the reload (to the player who held, and the crew).</summary>
    LoadStarted = 11,
    /// <summary>The loading from a cart was dropped: the cart or the truck moved, or the cart was lost; the cart keeps its rocket.</summary>
    LoadCancelled = 12,
}

/// <summary>
/// Payloads of the Scud events (little-endian). Commands: u8 cmd, f32 x, f32 z, f32 zoom. State: u16 car, u8 state, f32 elapsed, f32 left (-1: none), u8 rack-from (the rack's angle
/// when the state began, 0..255 over 0..90 degrees), u16 operator, u8 has-lock, 3 x f32 lock, u8 confirmed seats, u8 seats that must. View: u16 car, f32 x, f32 z, f32 zoom.
/// Launch: u16 missile, u16 car, u16 placer, 3 x f32 from, 3 x f32 target, f32 seconds. Impact: u16 missile, u16 placer, 3 x f32 at. Notice: u8 code.
/// </summary>
public static class ScudCodec
{
    public const int CommandBytes = 13, StateBytes = 29, ViewBytes = 14, LaunchBytes = 34, ImpactBytes = 16;

    static void F(Span<byte> b, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b[o..], v);
    static float F(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
    static bool Finite(params float[] v) { foreach (var x in v) if (!float.IsFinite(x)) return false; return true; }

    public static byte[] Encode(ScudCommandMsg m)
    {
        var b = new byte[CommandBytes];
        b[0] = (byte)m.Cmd; F(b, 1, m.X); F(b, 5, m.Z); F(b, 9, m.Zoom);
        return b;
    }

    public static ScudCommandMsg? DecodeCommand(ReadOnlySpan<byte> d)
    {
        if (d.Length != CommandBytes || d[0] > (byte)ScudCmd.Abort) return null;
        float x = F(d, 1), z = F(d, 5), zoom = F(d, 9);
        return Finite(x, z, zoom) ? new ScudCommandMsg((ScudCmd)d[0], x, z, zoom) : null;
    }

    public static byte[] Encode(ScudStateMsg m)
    {
        var b = new byte[StateBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Car);
        b[2] = (byte)m.State;
        F(b, 3, m.Elapsed);
        F(b, 7, float.IsFinite(m.Left) ? m.Left : -1f);
        b[11] = (byte)Math.Clamp((int)MathF.Round(m.RackFrom / ScudRules.RackMax * 255f), 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), m.Operator);
        b[14] = m.Lock != null ? (byte)1 : (byte)0;
        var l = m.Lock ?? Vector3.Zero;
        F(b, 15, l.X); F(b, 19, l.Y); F(b, 23, l.Z);
        b[27] = m.Confirmed; b[28] = m.Need;
        return b;
    }

    public static ScudStateMsg? DecodeState(ReadOnlySpan<byte> d)
    {
        if (d.Length != StateBytes || d[2] > (byte)ScudLauncherState.Loading) return null;
        var elapsed = F(d, 3); var left = F(d, 7);
        var l = new Vector3(F(d, 15), F(d, 19), F(d, 23));
        if (!Finite(elapsed, left, l.X, l.Y, l.Z)) return null;
        return new ScudStateMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), (ScudLauncherState)d[2], elapsed, left < 0 ? float.PositiveInfinity : left,
            d[11] / 255f * ScudRules.RackMax, BinaryPrimitives.ReadUInt16LittleEndian(d[12..]), d[14] != 0 ? l : null, d[27], d[28]);
    }

    public static byte[] Encode(ScudViewMsg m)
    {
        var b = new byte[ViewBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Car);
        F(b, 2, m.X); F(b, 6, m.Z); F(b, 10, m.Zoom);
        return b;
    }

    public static ScudViewMsg? DecodeView(ReadOnlySpan<byte> d)
    {
        if (d.Length != ViewBytes) return null;
        float x = F(d, 2), z = F(d, 6), zoom = F(d, 10);
        return Finite(x, z, zoom) ? new ScudViewMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), x, z, zoom) : null;
    }

    public static byte[] Encode(ScudLaunchMsg m)
    {
        var b = new byte[LaunchBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Missile);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Car);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), m.Placer);
        F(b, 6, m.From.X); F(b, 10, m.From.Y); F(b, 14, m.From.Z);
        F(b, 18, m.Target.X); F(b, 22, m.Target.Y); F(b, 26, m.Target.Z);
        F(b, 30, m.Seconds);
        return b;
    }

    public static ScudLaunchMsg? DecodeLaunch(ReadOnlySpan<byte> d)
    {
        if (d.Length != LaunchBytes) return null;
        var from = new Vector3(F(d, 6), F(d, 10), F(d, 14)); var target = new Vector3(F(d, 18), F(d, 22), F(d, 26)); var seconds = F(d, 30);
        if (!Finite(from.X, from.Y, from.Z, target.X, target.Y, target.Z, seconds) || seconds <= 0) return null;
        return new ScudLaunchMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), from, target, seconds);
    }

    public static byte[] Encode(ScudImpactMsg m)
    {
        var b = new byte[ImpactBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Missile);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Placer);
        F(b, 4, m.At.X); F(b, 8, m.At.Y); F(b, 12, m.At.Z);
        return b;
    }

    public static ScudImpactMsg? DecodeImpact(ReadOnlySpan<byte> d)
    {
        if (d.Length != ImpactBytes) return null;
        var at = new Vector3(F(d, 4), F(d, 8), F(d, 12));
        return Finite(at.X, at.Y, at.Z) ? new ScudImpactMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), at) : null;
    }

    public static byte[] Encode(ScudNoticeCode n) => new[] { (byte)n };

    public static ScudNoticeCode? DecodeNotice(ReadOnlySpan<byte> d) => d.Length == 1 && d[0] is >= (byte)ScudNoticeCode.TooClose and <= (byte)ScudNoticeCode.LoadCancelled ? (ScudNoticeCode)d[0] : null;

    /// <summary>The notice a refused command gets, or null for an Ok.</summary>
    public static ScudNoticeCode? NoticeOf(ScudAnswer a) => a switch
    {
        ScudAnswer.TooClose => ScudNoticeCode.TooClose,
        ScudAnswer.OffMap => ScudNoticeCode.OffMap,
        ScudAnswer.Moving => ScudNoticeCode.Moving,
        ScudAnswer.NotOperator => ScudNoticeCode.NotOperator,
        ScudAnswer.WrongState or ScudAnswer.NotAboard => ScudNoticeCode.WrongState,
        _ => null,
    };

    /// <summary>The notice for a dropped loading: the cart or the truck moved, or the cart was lost.</summary>
    public static ScudNoticeCode NoticeOf(ScudLoadCancel c) => ScudNoticeCode.LoadCancelled;

    /// <summary>The notice for a dropped proposal.</summary>
    public static ScudNoticeCode NoticeOf(ScudCancel c) => c switch
    {
        ScudCancel.CrewChanged => ScudNoticeCode.CrewChanged,
        ScudCancel.Withdrawn => ScudNoticeCode.Withdrawn,
        ScudCancel.Moved => ScudNoticeCode.Moved,
        ScudCancel.Timeout => ScudNoticeCode.Timeout,
        _ => ScudNoticeCode.Retarget,
    };
}

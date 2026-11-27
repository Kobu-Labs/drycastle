using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a player asks of the Cartel at the Contact (<see cref="Net.EventKind.CartelRequest"/>). The wire carries the byte.</summary>
public enum CartelAction : byte { Join = 1, Quit = 2, StartJob = 3 }

/// <summary>A request: join, quit, or start a Job of a kind at a tier (kind and tier only for <see cref="CartelAction.StartJob"/>).</summary>
public readonly record struct CartelRequest(CartelAction Action, string Kind = "", byte Tier = 0);

/// <summary>What the status says beside the standing.</summary>
[Flags]
public enum CartelStatusFlags : byte
{
    None = 0,
    /// <summary>No stored Profile: the player cannot join.</summary>
    Guest = 1,
    /// <summary>In sandbox mode: runs jobs, earns nothing.</summary>
    Sandbox = 2,
    /// <summary>The Respect just made a new rank (this message follows a reward or a grant).</summary>
    RankedUp = 4,
}

/// <summary>A player's own Cartel standing (<see cref="Net.EventKind.CartelStatus"/>): membership, Respect and the rank it makes (an index of cartel.json, which the client has for the names and the bar).</summary>
public readonly record struct CartelStatusMsg(CartelMembership Membership, int Respect, byte Rank, CartelStatusFlags Flags);

/// <summary>What the Job message says about the receiver.</summary>
[Flags]
public enum CartelJobFlags : byte
{
    None = 0,
    /// <summary>The receiver earns the Job's Respect on success (a member with a stored Profile, not in sandbox mode).</summary>
    Earns = 1,
    /// <summary>The receiver is not in the Cartel: they share the objective and earn nothing (the hint "Join the cartel at the Contact").</summary>
    NotMember = 2,
    /// <summary>The receiver leads it (starts it; their death fails it).</summary>
    Leader = 4,
    /// <summary>The receiver is a guest (no saved profile): they earn nothing and cannot join, so they are not told to (the tracker says why instead).</summary>
    Guest = 8,
}

/// <summary>
/// The objective of the receiver's Job (<see cref="Net.EventKind.CartelObjective"/>): the HUD tracker's line (empty: the kind's name only) and where the party's marker points
/// (Godot axes; null: no marker). Sent to the party when either changes and to a member who joins it midway; the Job's end (<see cref="CartelJobMsg"/>) clears it.
/// </summary>
public readonly record struct CartelObjectiveMsg(int JobId, string Line, Vector3? Marker);

/// <summary>
/// The Contact refused a request (<see cref="Net.EventKind.CartelRefused"/>): the action asked, the refusal's code (a <see cref="CartelJoinRefusal"/> for join and quit, a
/// <see cref="JobStartRefusal"/> for a start) and the words the player reads.
/// </summary>
public readonly record struct CartelRefusalMsg(CartelAction Action, byte Code, string Words);

/// <summary>
/// The receiver's Job (<see cref="Net.EventKind.CartelJob"/>): sent to its party when it starts, when the party, its leader or the receiver's flags change, and when it ends (then once, with the
/// end state, the reason and what the receiver was paid). <see cref="JobId"/> 0 is "no job" (the receiver left the party).
/// </summary>
public readonly record struct CartelJobMsg(int JobId, JobState State, JobFailReason Reason, string Kind, byte Tier, ushort Leader, float SecondsLeft, int RespectPaid, CartelJobFlags Flags)
{
    public static readonly CartelJobMsg None = new(0, 0, JobFailReason.None, "", 0, 0, 0, 0, CartelJobFlags.None);
}

/// <summary>
/// The Cartel's wire (protocol history: v88, v89). Kinds 33-38: <see cref="Net.EventKind.CartelRequest"/> (client to server), <see cref="Net.EventKind.CartelRefused"/> (server to a player),
/// <see cref="Net.EventKind.CartelStatus"/>, <see cref="Net.EventKind.CartelJob"/> (server to a player), <see cref="Net.EventKind.CartelRankUp"/> (server to everyone), <see cref="Net.EventKind.CartelObjective"/> (server to a Job's party).
/// Little endian throughout. A decode of a short or bad payload is false.
/// </summary>
public static class CartelCodec
{
    /// <summary>Longest kind id on the wire, bytes of UTF-8.</summary>
    public const int MaxKindBytes = 32;

    static byte[] KindBytes(string kind)
    {
        var b = Encoding.UTF8.GetBytes(kind ?? "");
        if (b.Length > MaxKindBytes) throw new ArgumentException($"a job kind id is at most {MaxKindBytes} bytes", nameof(kind));
        return b;
    }

    /// <summary>u8 action, u8 tier, u8 kind length, kind (UTF-8).</summary>
    public static byte[] EncodeRequest(CartelRequest r)
    {
        var k = KindBytes(r.Kind);
        var b = new byte[3 + k.Length];
        b[0] = (byte)r.Action; b[1] = r.Tier; b[2] = (byte)k.Length;
        k.CopyTo(b, 3);
        return b;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> d, out CartelRequest r)
    {
        r = default;
        if (d.Length < 3 || d[0] is < (byte)CartelAction.Join or > (byte)CartelAction.StartJob || d[2] > MaxKindBytes || d.Length != 3 + d[2]) return false;
        r = new CartelRequest((CartelAction)d[0], Encoding.UTF8.GetString(d.Slice(3, d[2])), d[1]);
        return true;
    }

    /// <summary>u8 membership, i32 respect, u8 rank, u8 flags (7 bytes).</summary>
    public static byte[] EncodeStatus(CartelStatusMsg s)
    {
        var b = new byte[7];
        b[0] = (byte)s.Membership;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), s.Respect);
        b[5] = s.Rank; b[6] = (byte)s.Flags;
        return b;
    }

    public static bool TryDecodeStatus(ReadOnlySpan<byte> d, out CartelStatusMsg s)
    {
        s = default;
        if (d.Length != 7 || d[0] > (byte)CartelMembership.Quit) return false;
        s = new CartelStatusMsg((CartelMembership)d[0], BinaryPrimitives.ReadInt32LittleEndian(d[1..]), d[5], (CartelStatusFlags)d[6]);
        return true;
    }

    /// <summary>i32 job id, u8 state (0 none), u8 reason, u8 tier, u16 leader, f32 seconds left, i32 respect paid, u8 flags, u8 kind length, kind (UTF-8).</summary>
    public static byte[] EncodeJob(CartelJobMsg j)
    {
        var k = KindBytes(j.Kind);
        var b = new byte[19 + k.Length];
        var s = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, j.JobId);
        b[4] = (byte)j.State; b[5] = (byte)j.Reason; b[6] = j.Tier;
        BinaryPrimitives.WriteUInt16LittleEndian(s[7..], j.Leader);
        BinaryPrimitives.WriteSingleLittleEndian(s[9..], j.SecondsLeft);
        BinaryPrimitives.WriteInt32LittleEndian(s[13..], j.RespectPaid);
        b[17] = (byte)j.Flags;
        b[18] = (byte)k.Length;
        k.CopyTo(b, 19);
        return b;
    }

    public static bool TryDecodeJob(ReadOnlySpan<byte> d, out CartelJobMsg j)
    {
        j = default;
        if (d.Length < 19 || d[18] > MaxKindBytes || d.Length != 19 + d[18] || d[4] > (byte)JobState.Failed) return false;
        j = new CartelJobMsg(BinaryPrimitives.ReadInt32LittleEndian(d), (JobState)d[4], (JobFailReason)d[5], Encoding.UTF8.GetString(d.Slice(19, d[18])), d[6],
            BinaryPrimitives.ReadUInt16LittleEndian(d[7..]), BinaryPrimitives.ReadSingleLittleEndian(d[9..]), BinaryPrimitives.ReadInt32LittleEndian(d[13..]), (CartelJobFlags)d[17]);
        return true;
    }

    /// <summary>Longest objective line on the wire, bytes of UTF-8 (a longer one is cut at a character boundary).</summary>
    public const int MaxLineBytes = 255;

    /// <summary>i32 job id, u8 1 when there is a marker, f32 x3 the marker (0 without one), u8 line length, line (UTF-8).</summary>
    public static byte[] EncodeObjective(CartelObjectiveMsg o)
    {
        var line = Encoding.UTF8.GetBytes(o.Line ?? "");
        var n = Math.Min(line.Length, MaxLineBytes);
        while (n > 0 && n < line.Length && (line[n] & 0xC0) == 0x80) n--; // never split a character
        var b = new byte[18 + n];
        var s = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, o.JobId);
        var m = o.Marker ?? Vector3.Zero;
        b[4] = o.Marker.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(s[5..], m.X);
        BinaryPrimitives.WriteSingleLittleEndian(s[9..], m.Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[13..], m.Z);
        b[17] = (byte)n;
        line.AsSpan(0, n).CopyTo(s[18..]);
        return b;
    }

    public static bool TryDecodeObjective(ReadOnlySpan<byte> d, out CartelObjectiveMsg o)
    {
        o = default;
        if (d.Length < 18 || d[4] > 1 || d.Length != 18 + d[17]) return false;
        var m = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[5..]), BinaryPrimitives.ReadSingleLittleEndian(d[9..]), BinaryPrimitives.ReadSingleLittleEndian(d[13..]));
        if (d[4] == 1 && !(float.IsFinite(m.X) && float.IsFinite(m.Y) && float.IsFinite(m.Z))) return false;
        o = new CartelObjectiveMsg(BinaryPrimitives.ReadInt32LittleEndian(d), Encoding.UTF8.GetString(d.Slice(18, d[17])), d[4] == 1 ? m : null);
        return true;
    }

    /// <summary>u8 action, u8 code, u8 words length, words (UTF-8, cut at <see cref="MaxLineBytes"/> on a character boundary).</summary>
    public static byte[] EncodeRefusal(CartelRefusalMsg r)
    {
        var words = Encoding.UTF8.GetBytes(r.Words ?? "");
        var n = Math.Min(words.Length, MaxLineBytes);
        while (n > 0 && n < words.Length && (words[n] & 0xC0) == 0x80) n--; // never split a character
        var b = new byte[3 + n];
        b[0] = (byte)r.Action; b[1] = r.Code; b[2] = (byte)n;
        words.AsSpan(0, n).CopyTo(b.AsSpan(3));
        return b;
    }

    public static bool TryDecodeRefusal(ReadOnlySpan<byte> d, out CartelRefusalMsg r)
    {
        r = default;
        if (d.Length < 3 || d[0] is < (byte)CartelAction.Join or > (byte)CartelAction.StartJob || d.Length != 3 + d[2]) return false;
        r = new CartelRefusalMsg((CartelAction)d[0], d[1], Encoding.UTF8.GetString(d.Slice(3, d[2])));
        return true;
    }

    /// <summary>u16 player id, u8 the new rank (3 bytes).</summary>
    public static byte[] EncodeRankUp(ushort player, byte rank)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        b[2] = rank;
        return b;
    }

    public static bool TryDecodeRankUp(ReadOnlySpan<byte> d, out ushort player, out byte rank)
    {
        player = 0; rank = 0;
        if (d.Length != 3) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(d);
        rank = d[2];
        return true;
    }
}

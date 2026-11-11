using System.Buffers.Binary;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>What the owner learns when a streak is earned (<see cref="EventKind.StreakEarned"/>).</summary>
public sealed record StreakEarnedInfo(string StreakId, int Kills);
/// <summary>A streak was used (<see cref="EventKind.StreakUsed"/>).</summary>
public sealed record StreakUsedInfo(string StreakId);
/// <summary>An active streak ended (<see cref="EventKind.StreakEnded"/>).</summary>
public sealed record StreakEndedInfo(string StreakId, KillstreakEndReason Reason);

/// <summary>
/// Payloads of the owner-only killstreak events (reliable, sent to the owner alone). Layout: u8 length + utf8 streak id,
/// then the event's own fields (earned: u16 kills; ended: u8 reason). Decoders return null on malformed input.
/// </summary>
public static class KillstreakEvents
{
    public static byte[] EncodeEarned(string streakId, int kills)
    {
        var b = new List<byte>(Id(streakId));
        var k = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(k, (ushort)Math.Clamp(kills, 0, ushort.MaxValue));
        b.AddRange(k);
        return b.ToArray();
    }

    public static byte[] EncodeUsed(string streakId) => Id(streakId);

    public static byte[] EncodeEnded(string streakId, KillstreakEndReason reason)
    {
        var b = new List<byte>(Id(streakId)) { (byte)reason };
        return b.ToArray();
    }

    public static StreakEarnedInfo? DecodeEarned(ReadOnlySpan<byte> d)
    {
        if (!ReadId(d, out var id, out var rest) || rest.Length < 2) return null;
        return new StreakEarnedInfo(id, BinaryPrimitives.ReadUInt16LittleEndian(rest));
    }

    public static StreakUsedInfo? DecodeUsed(ReadOnlySpan<byte> d) =>
        ReadId(d, out var id, out _) ? new StreakUsedInfo(id) : null;

    public static StreakEndedInfo? DecodeEnded(ReadOnlySpan<byte> d)
    {
        if (!ReadId(d, out var id, out var rest) || rest.Length < 1 || rest[0] > (byte)KillstreakEndReason.RoundReset) return null;
        return new StreakEndedInfo(id, (KillstreakEndReason)rest[0]);
    }

    static byte[] Id(string streakId)
    {
        var bytes = Encoding.UTF8.GetBytes(streakId ?? "");
        if (bytes.Length > byte.MaxValue) Array.Resize(ref bytes, byte.MaxValue);
        var o = new byte[1 + bytes.Length];
        o[0] = (byte)bytes.Length;
        bytes.CopyTo(o, 1);
        return o;
    }

    static bool ReadId(ReadOnlySpan<byte> d, out string id, out ReadOnlySpan<byte> rest)
    {
        id = "";
        rest = ReadOnlySpan<byte>.Empty;
        if (d.Length < 1 || d.Length < 1 + d[0]) return false;
        id = Encoding.UTF8.GetString(d.Slice(1, d[0]));
        rest = d[(1 + d[0])..];
        return true;
    }
}

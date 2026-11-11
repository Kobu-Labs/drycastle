using System.Buffers.Binary;

namespace Suchohrad.ShooterCore;

/// <summary>One line of the post-match XP summary: why, and how much.</summary>
public readonly record struct XpLine(XpReason Reason, int Amount);

/// <summary>Sent to a player when a grant takes them to a new level (the new level and the XP that caused it).</summary>
public sealed record LevelUpInfo(int Level, int Xp);

/// <summary>Sent to a player at the end of a round: their XP now, their level, and every grant since the round began.</summary>
public sealed record XpSummaryInfo(int Xp, int Level, IReadOnlyList<XpLine> Lines)
{
    public int Total => Lines.Sum(l => l.Amount);
}

/// <summary>
/// Payloads of the two progression events (reliable event envelope, <see cref="EventKind.LevelUp"/> and
/// <see cref="EventKind.XpSummary"/>). Little-endian: LevelUp = u16 level, u32 xp. XpSummary = u32 xp, u16 level,
/// u8 count, then count x (u8 reason, u32 amount). Decoders return null on a malformed payload.
/// </summary>
public static class ProgressionEvents
{
    /// <summary>The most lines one summary carries (a round does not grant that many in practice).</summary>
    public const int MaxSummaryLines = byte.MaxValue;

    public static byte[] EncodeLevelUp(LevelUpInfo info)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)info.Level);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)info.Xp);
        return b;
    }

    public static LevelUpInfo? DecodeLevelUp(ReadOnlySpan<byte> d) =>
        d.Length == 6 ? new LevelUpInfo(BinaryPrimitives.ReadUInt16LittleEndian(d), (int)BinaryPrimitives.ReadUInt32LittleEndian(d[2..])) : null;

    public static byte[] EncodeXpSummary(XpSummaryInfo info)
    {
        var lines = info.Lines.Take(MaxSummaryLines).ToList();
        var b = new byte[7 + lines.Count * 5];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)info.Xp);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)info.Level);
        b[6] = (byte)lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var at = 7 + i * 5;
            b[at] = (byte)lines[i].Reason;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 1), (uint)lines[i].Amount);
        }
        return b;
    }

    public static XpSummaryInfo? DecodeXpSummary(ReadOnlySpan<byte> d)
    {
        if (d.Length < 7) return null;
        var count = d[6];
        if (d.Length != 7 + count * 5) return null;
        var lines = new List<XpLine>(count);
        for (var i = 0; i < count; i++)
        {
            var at = 7 + i * 5;
            if (!Enum.IsDefined((XpReason)d[at])) return null;
            lines.Add(new XpLine((XpReason)d[at], (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 1)..])));
        }
        return new XpSummaryInfo((int)BinaryPrimitives.ReadUInt32LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), lines);
    }
}

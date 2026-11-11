using System.Buffers.Binary;
using System.Text;

namespace Suchohrad.ShooterCore;

/// <summary>The server's answer to one <see cref="SaveLoadoutMsg"/>: the slot, the client's seq, and ok or the reason.</summary>
public sealed record LoadoutAckInfo(int Slot, uint Seq, bool Ok, string Reason);

/// <summary>
/// Payloads of the loadout editor events (ticket #145, kinds 130-132; they ride the reliable event envelope like the rest).
/// <list type="bullet">
/// <item>CopyLoadout (client to server): u8 from, u8 to.</item>
/// <item>ResetLoadout (client to server): u8 slot.</item>
/// <item>LoadoutAck (server to client): u8 slot, u32 seq, u8 ok, u8 reasonLen, reason utf8.</item>
/// </list>
/// Decoders return null on a malformed payload.
/// </summary>
public static class LoadoutEvents
{
    public static byte[] EncodeCopy(int from, int to) => new[] { (byte)from, (byte)to };

    public static (int From, int To)? DecodeCopy(ReadOnlySpan<byte> p) =>
        p.Length == 2 ? (p[0], p[1]) : null;

    public static byte[] EncodeReset(int slot) => new[] { (byte)slot };

    public static int? DecodeReset(ReadOnlySpan<byte> p) => p.Length == 1 ? p[0] : null;

    public static byte[] EncodeAck(LoadoutAckInfo a)
    {
        var reason = Encoding.UTF8.GetBytes(a.Reason ?? "");
        if (reason.Length > byte.MaxValue) Array.Resize(ref reason, byte.MaxValue);
        var b = new byte[7 + reason.Length];
        b[0] = (byte)a.Slot;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), a.Seq);
        b[5] = a.Ok ? (byte)1 : (byte)0;
        b[6] = (byte)reason.Length;
        reason.CopyTo(b, 7);
        return b;
    }

    public static LoadoutAckInfo? DecodeAck(ReadOnlySpan<byte> p)
    {
        if (p.Length < 7) return null;
        var n = p[6];
        if (p.Length != 7 + n) return null;
        return new LoadoutAckInfo(p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]), p[5] != 0, Encoding.UTF8.GetString(p.Slice(7, n)));
    }
}

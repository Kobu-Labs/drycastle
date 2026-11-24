using System.Buffers.Binary;

namespace Drycastle.ShooterCore.Net;

/// <summary>Little-endian writes into a record buffer at a cursor, which moves on.</summary>
internal static class Bytes
{
    public static void W16(byte[] b, ref int o, short v) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); o += 2; }
    public static void W32(byte[] b, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); o += 4; }
}

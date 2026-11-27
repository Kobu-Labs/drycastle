using System.Text;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// A hollow house's building id on the wire, the one format the Trap house and Drill events share (<see cref="TrapHouseCodec"/>, <see cref="DrillCodec"/>): u8 length, then the id in UTF-8.
/// An event that carries only the house is exactly that (<see cref="Encode"/>, <see cref="Decode"/>).
/// </summary>
public static class HouseIdWire
{
    /// <summary>The id's UTF-8 bytes; a longer id than the length byte can say is refused.</summary>
    public static byte[] Bytes(string house)
    {
        var id = Encoding.UTF8.GetBytes(house);
        if (id.Length > 255) throw new ArgumentException($"house id '{house}' is longer than 255 bytes");
        return id;
    }

    /// <summary>Writes <paramref name="id"/> (<see cref="Bytes"/>) at <paramref name="at"/>; the index after it. <paramref name="o"/> has 1 + id.Length bytes free there.</summary>
    public static int Write(byte[] o, int at, byte[] id)
    {
        o[at] = (byte)id.Length;
        id.CopyTo(o, at + 1);
        return at + 1 + id.Length;
    }

    /// <summary>The id at <paramref name="at"/> and the index after it, or null when the payload ends before it does.</summary>
    public static string? Read(byte[] payload, int at, out int end)
    {
        end = at;
        if (payload.Length < at + 1 || payload.Length < at + 1 + payload[at]) return null;
        end = at + 1 + payload[at];
        return Encoding.UTF8.GetString(payload, at + 1, payload[at]);
    }

    /// <summary>A payload that is the house alone.</summary>
    public static byte[] Encode(string house)
    {
        var id = Bytes(house);
        var o = new byte[1 + id.Length];
        Write(o, 0, id);
        return o;
    }

    /// <summary>The house of a payload that is the house alone; null for anything else.</summary>
    public static string? Decode(byte[] payload) => Read(payload, 0, out var end) is { } house && end == payload.Length ? house : null;
}

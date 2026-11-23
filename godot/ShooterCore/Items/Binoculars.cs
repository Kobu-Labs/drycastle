using System.Buffers.Binary;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Items;

/// <summary>
/// The binoculars (GLOSSARY "Binoculars"): an item carried in the Bag (items.json <c>binoculars</c>), raised and lowered with one key. Raised, the
/// view is narrowed to <see cref="FovDeg"/> and the middle of it reads the distance to what is under it, the number a sniper dials as the Zero
/// (<see cref="BulletDrop"/>). The distance is measured on the client, against the world it draws. What travels is only whether they are up and
/// which pair it is, so the other players see that pair held at the face: the client says so (<see cref="EventKind.RaiseBinoculars"/>), the server
/// checks the bag and tells everyone else (<see cref="EventKind.BinocularsRaised"/>); a new life starts with them down.
/// </summary>
public static class Binoculars
{
    /// <summary>Vertical FOV while raised, degrees (about 8x of the default view).</summary>
    public const float FovDeg = 9f;
    /// <summary>The FOV while raised of this pair: its own (an upgrade, #473) or the standard <see cref="FovDeg"/>.</summary>
    public static float FovOf(ItemDef pair) => pair.Fov > 0 ? pair.Fov : FovDeg;

    /// <summary>How long a marked animal stays highlighted (#473, the mark key while raised); 0 for a pair that cannot mark.</summary>
    public static float MarkSecondsOf(ItemDef pair) => pair.MarkSeconds;

    /// <summary>Farthest distance read, metres; beyond it (the sky) there is no reading.</summary>
    public const float MaxRangeM = 1500f;
    /// <summary>Seconds between two measurements, so the number does not flicker.</summary>
    public const double MeasureSeconds = 0.1;

    /// <summary>The number shown for a measured distance: whole metres, or null with nothing in range.</summary>
    public static int? Reading(float? metres) => metres is { } m && float.IsFinite(m) && m <= MaxRangeM ? (int)MathF.Round(m) : null;

    /// <summary>Client to server, <see cref="EventKind.RaiseBinoculars"/>: u8, 1 raised, 0 lowered.</summary>
    public static byte[] EncodeRaise(bool up) => new[] { (byte)(up ? 1 : 0) };

    public static bool TryDecodeRaise(byte[] payload, out bool up)
    {
        up = payload.Length == 1 && payload[0] == 1;
        return payload.Length == 1 && payload[0] <= 1;
    }

    /// <summary>Server to the others, <see cref="EventKind.BinocularsRaised"/>: u16 player id, u8 1 raised, 0 lowered, u8 the pair's item index (0 when lowered).</summary>
    public static byte[] EncodeRaised(ushort player, bool up, int item)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        b[2] = (byte)(up ? 1 : 0);
        b[3] = (byte)(up ? Math.Clamp(item, 0, 255) : 0);
        return b;
    }

    public static bool TryDecodeRaised(byte[] payload, out ushort player, out bool up, out int item)
    {
        player = 0; up = false; item = 0;
        if (payload.Length != 4 || payload[2] > 1) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        up = payload[2] == 1;
        item = payload[3];
        return true;
    }

    /// <summary>The pair a player raises of the items carried (item indexes): the narrowest view when there are several; -1 with none.</summary>
    public static int Held(ItemsFile items, IEnumerable<int> carried)
    {
        var best = -1;
        foreach (var index in carried)
            if (items.At(index) is { Binoculars: true } pair && (best < 0 || FovOf(pair) < FovOf(items.At(best)!))) best = index;
        return best;
    }

    /// <summary>True when one of the carried items is a pair of binoculars.</summary>
    public static bool Carried(ItemsFile items, IEnumerable<int?> bagItems)
    {
        foreach (var i in bagItems)
            if (i is { } index && items.At(index) is { Binoculars: true }) return true;
        return false;
    }
}

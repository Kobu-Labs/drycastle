using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>The divine blessing (<see cref="StatusKind.Blessing"/>): one lethal hit is negated. The host asks <see cref="TryConsume"/> from <see cref="MatchSim.SaveFromDeath"/>.</summary>
public static class BlessingRules
{
    /// <summary>Seconds the saved player takes no damage (the invulnerability after the save).</summary>
    public const float InvulnerableS = 1f;

    /// <summary>Uses up the blessing: true when the player had one (the death is negated), false otherwise (or for no statuses).</summary>
    public static bool TryConsume(PlayerStatuses? statuses) => statuses != null && statuses.Remove(StatusKind.Blessing);
}

/// <summary>The burn of the devil's curse (0.5 hp/s for 10 s, the numbers of holy_mass_demon.json's <c>burn</c>); the host uses the file's when it has loaded it.</summary>
public static class CurseBurn
{
    public static BurnDef Default => new() { Dps = 0.5f, DurationS = 10, TickIntervalS = 1 };
}

/// <summary>
/// Turns a <see cref="BurnTick"/>'s fractional damage into whole hit points (hit events carry whole points): the part below one point is kept
/// per victim until the next tick. A full 10 s burn of 0.5 hp/s therefore pays 1 hp every 2 s, 5 hp in all, however the ticks are cut.
/// </summary>
public sealed class BurnPayout
{
    const float Eps = 1e-3f;
    readonly Dictionary<ushort, float> _owed = new();

    /// <summary>Adds the tick's damage to what the victim is owed and returns the whole points due now (0 when under one).</summary>
    public int Whole(BurnTick tick)
    {
        var owed = _owed.GetValueOrDefault(tick.Victim) + tick.Damage;
        var whole = (int)MathF.Floor(owed + Eps);
        _owed[tick.Victim] = owed - whole;
        return whole;
    }

    /// <summary>The victim is out (died, left, the burn ended): what was owed is dropped.</summary>
    public void Forget(ushort victim) => _owed.Remove(victim);

    public void Clear() => _owed.Clear();
}

/// <summary>Kinds 210 and 211 of the wire: a player's statuses and the blessing's save.</summary>
public static class StatusCodec
{
    /// <summary>Bit of the flags byte of <see cref="EncodeState"/>: the player is burning.</summary>
    public const byte FlagBurning = 1;

    /// <summary>Server to everyone, <see cref="EventKind.StatusState"/>: u16 player id, u8 <see cref="PlayerStatuses.Mask"/>, u8 flags (<see cref="FlagBurning"/>).</summary>
    public static byte[] EncodeState(ushort player, byte mask, bool burning)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        b[2] = mask;
        b[3] = burning ? FlagBurning : (byte)0;
        return b;
    }

    public static bool TryDecodeState(ReadOnlySpan<byte> p, out ushort player, out byte mask, out bool burning)
    {
        player = 0; mask = 0; burning = false;
        if (p.Length != 4) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(p);
        mask = p[2];
        burning = (p[3] & FlagBurning) != 0;
        return true;
    }

    /// <summary>Server to everyone, <see cref="EventKind.BlessingSaved"/>: u16 id of the player a blessing just saved.</summary>
    public static byte[] EncodeSaved(ushort player)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        return b;
    }

    public static bool TryDecodeSaved(ReadOnlySpan<byte> p, out ushort player)
    {
        player = 0;
        if (p.Length != 2) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }
}

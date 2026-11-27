namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// What the plantation's hold of the interact key does for a player of a running Harvest now (GLOSSARY "Heist"; docs/cartel.md "Harvest"): what the prompt and the hold ring say. Not a
/// machine: it is read off the Heist's phase, the filled count and the hands each time, never entered.
/// </summary>
public enum FillReach : byte
{
    /// <summary>The hold is not the Harvest's here: no Harvest of the player's, outside a plantation, or the key is another thing's. Nothing shows (the High's hold stays silent).</summary>
    Off = 0,
    /// <summary>A hold fills a weed Duffel: "Hold E: fill a duffel", the ring.</summary>
    Ready = 1,
    /// <summary>Blocked: the player carries a Duffel already (one per player).</summary>
    Carrying = 2,
    /// <summary>Blocked: the harvest's maximum is filled.</summary>
    Full = 3,
    /// <summary>Blocked: the Cartel van has not reached the plantation yet (the Assault clock has not started).</summary>
    VanFirst = 4,
}

/// <summary>The rules of the Harvest's fill hold and its wire form (event <see cref="Net.EventKind.HarvestFill"/>).</summary>
public static class HarvestFillRules
{
    /// <summary>
    /// What the hold does for a Harvest player in a plantation: the van first (before it reaches the plantation nothing fills, or the most would be loaded at the edge with no Assault at
    /// all), then a Duffel in the hands, then the maximum filled; else it fills.
    /// </summary>
    public static FillReach Reach(HeistPhase phase, int filled, int max, bool carrying) =>
        phase == HeistPhase.ToObjective ? FillReach.VanFirst
        : carrying ? FillReach.Carrying
        : filled >= max ? FillReach.Full
        : FillReach.Ready;

    /// <summary>The filler's figure bends and reaches (the status bit <see cref="Status.StatusKind.Filling"/>) once the hold has run this long: a tap of the key shows nothing.</summary>
    public static bool Shows(FillReach reach, bool charging, double heldSeconds, double afterSeconds) => reach == FillReach.Ready && charging && heldSeconds >= afterSeconds;

    /// <summary>The wire form: u8 <see cref="FillReach"/>, u8 the harvest's maximum (the prompt's "All N duffels are filled").</summary>
    public static byte[] Encode(FillReach reach, int max) => new[] { (byte)reach, (byte)Math.Clamp(max, 0, 255) };

    public static bool TryDecode(ReadOnlySpan<byte> d, out FillReach reach, out int max)
    {
        reach = FillReach.Off; max = 0;
        if (d.Length < 2 || d[0] > (byte)FillReach.VanFirst) return false;
        reach = (FillReach)d[0]; max = d[1];
        return true;
    }
}

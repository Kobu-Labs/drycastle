using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The Trap house heist's numbers (cartel.json <c>jobs.trap_house</c>, #496, docs/cartel.md "Trap house"): the Heist template's (<see cref="HeistData"/>) and the house's own: its posted
/// defenders by tier and squad size, and the area the Cartel van loads in (round the house and the road spot where it parks).
/// </summary>
public sealed class TrapHouseHeistData : HeistData
{
    public TrapHouseHeistData()
    {
        Name = "Trap house";
        MinDuffels = new[] { 2, 2, 3, 3, 3 };
        MinPerExtraMember = 0;
        MaxDuffels = new[] { 4, 5, 6, 7, 8 };
    }

    /// <summary>Posted defenders (the Rival crew holding the house) per tier for a party of one, plus Gunmen per party member beyond the first; at most max_defenders, and never more than the dressing's posts.</summary>
    public int[] DefenderGunmen { get; set; } = { 2, 2, 3, 3, 4 };
    public int[] DefenderEnforcers { get; set; } = { 1, 2, 2, 3, 3 };
    public int DefenderGunmenPerExtraMember { get; set; } = 1;
    public int MaxDefenders { get; set; } = 10;
    /// <summary>The objective's area: within this of the house's centre or of the road spot where the van parks.</summary>
    public float AreaM { get; set; } = 20;

    /// <summary>The template's checks and the house's own.</summary>
    public void ValidateTrapHouse(string what, int tiers)
    {
        Validate(what, tiers);
        DataFileException Bad(string text) => new($"{what}: {text}");
        if (DefenderGunmen.Length != tiers || DefenderEnforcers.Length != tiers) throw Bad($"defender_gunmen and defender_enforcers need one number per tier ({tiers})");
        if (DefenderGunmen.Any(n => n < 0) || DefenderEnforcers.Any(n => n < 0) || DefenderGunmenPerExtraMember < 0) throw Bad("the defender counts must be 0 or more");
        if (MaxDefenders < 0) throw Bad("max_defenders must be 0 or more");
        if (!float.IsFinite(AreaM) || !(AreaM > 0)) throw Bad("area_m must be more than 0");
    }
}

/// <summary>The Trap house heist's rules (docs/cartel.md "Trap house"), pure: the defenders, the Duffels on the tables, the tracker line's Drill part, where the marker points.</summary>
public static class TrapHouseHeistRules
{
    /// <summary>The book's name of a house a Trap house Job holds (two Jobs never share one).</summary>
    public static string Key(string house) => "trap_house:" + house;

    static T PerTier<T>(T[] a, int tier) => a[Math.Clamp(tier, 1, a.Length) - 1];

    /// <summary>The posted defenders for a party of <paramref name="size"/>: the tier's Gunmen plus one per extra member and its Enforcers, Gunmen first within max_defenders and the <paramref name="posts"/>.</summary>
    public static (int Gunmen, int Enforcers) Defenders(TrapHouseHeistData d, int tier, int size, int posts)
    {
        var cap = Math.Max(0, Math.Min(d.MaxDefenders, posts));
        var gunmen = Math.Min(cap, PerTier(d.DefenderGunmen, tier) + d.DefenderGunmenPerExtraMember * Math.Max(0, size - 1));
        var enforcers = Math.Min(cap - gunmen, PerTier(d.DefenderEnforcers, tier));
        return (gunmen, enforcers);
    }

    /// <summary>
    /// How many Duffels lie on the tables: what makes the heist's maximum together with the safe's <paramref name="safeCash"/>, never fewer than the minimum (the safe is a bonus), never more
    /// than the dressing's <paramref name="spots"/>.
    /// </summary>
    public static int TableDuffels(int spots, int min, int max, int safeCash) => Math.Clamp(Math.Max(min, max - safeCash), 0, Math.Max(0, spots));

    /// <summary>The house's centre on the ground axes (Godot x, z): the mean of its shell's corners (the plan's y is Godot's -z).</summary>
    public static Vector2 Centre(HouseFloorPlan house)
    {
        var sum = Vector2.Zero;
        foreach (var p in house.Shell) sum += p;
        var c = house.Shell.Length > 0 ? sum / house.Shell.Length : Vector2.Zero;
        return new Vector2(c.X, -c.Y);
    }

    /// <summary>The van stands in the house's area: within <paramref name="areaM"/> of the house's centre or of the road spot (ground axes x, z).</summary>
    public static bool InArea(Vector2 centre, Vector2 road, float areaM, float x, float z)
    {
        var p = new Vector2(x, z);
        var a2 = areaM * areaM;
        return Vector2.DistanceSquared(p, centre) <= a2 || Vector2.DistanceSquared(p, road) <= a2;
    }

    /// <summary>The marker points at the safe instead of the van: loading at the house while the Drill runs or is jammed.</summary>
    public static bool MarkSafe(Heist heist, DrillState? drill) =>
        heist.Phase == HeistPhase.AtObjective && heist.VanInArea && drill is DrillState.Running or DrillState.Jammed;

    /// <summary>The tracker line: the template's, with the safe's Drill after it while the party is at the house and the safe is not open yet.</summary>
    public static string Line(Heist heist, string line, DrillState? drill)
    {
        if (heist.Phase != HeistPhase.AtObjective || !heist.VanInArea) return line;
        return drill switch
        {
            DrillState.Armed => line + "; the safe: hold E to set the drill",
            DrillState.Running => line + "; the drill is on the safe",
            DrillState.Jammed => line + "; the drill jammed: hold E at the safe to fix it",
            _ => line,
        };
    }
}

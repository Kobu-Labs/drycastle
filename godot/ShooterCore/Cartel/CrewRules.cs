using Drycastle.ShooterCore.Thieves;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The size of a Rival crew a Job puts down (docs/cartel.md "Difficulty and reward"): a Hit's guards, a Heist's Assault, a Trap house's defenders. Each kind brings its own numbers; the
/// formula and the order they get out in are one.
/// </summary>
public static class CrewRules
{
    /// <summary>
    /// The Gunmen and Enforcers of a crew for a party of <paramref name="size"/>: each the tier's base plus its per-member number for every member past the first, Gunmen first within
    /// <paramref name="cap"/> and the Enforcers in what is left of it.
    /// </summary>
    public static (int Gunmen, int Enforcers) Split(int gunmenBase, int gunmenPerExtra, int enforcersBase, int enforcersPerExtra, int size, int cap)
    {
        var extra = Math.Max(0, size - 1);
        cap = Math.Max(0, cap);
        var gunmen = Math.Min(cap, gunmenBase + gunmenPerExtra * extra);
        var enforcers = Math.Min(cap - gunmen, enforcersBase + enforcersPerExtra * extra);
        return (gunmen, enforcers);
    }

    /// <summary>The order a crew is put down in (out of its van, round a dealer, onto a house's posts): Gunmen and Enforcers in turn, a Gunman first, the rest of the larger part last.</summary>
    public static List<GangMemberKind> Order(int gunmen, int enforcers)
    {
        var list = new List<GangMemberKind>(gunmen + enforcers);
        while (gunmen > 0 || enforcers > 0)
        {
            if (gunmen-- > 0) list.Add(GangMemberKind.Gunman);
            if (enforcers-- > 0) list.Add(GangMemberKind.Enforcer);
        }
        return list;
    }
}

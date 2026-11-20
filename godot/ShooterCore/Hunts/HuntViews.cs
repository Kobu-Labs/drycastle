namespace Drycastle.ShooterCore;

/// <summary>
/// One hunt as a viewer may see it (<see cref="HuntRedaction"/>). <see cref="Species"/> is the index id, the mystery id, or a hidden id when
/// <see cref="Hidden"/>; a hidden hunt has no <see cref="Exact"/>, <see cref="Variant"/> or <see cref="Diagram"/>, while hunter, time, distance and weapon stay.
/// </summary>
public sealed record HuntView(long PlayerId, string Hunter, string Species, string Exact, string Variant, float? Kg, float? DistanceM, string Weapon,
    HitDiagram? Diagram, long AtUnix, bool Hidden);

/// <summary>
/// One Life List tile of a player's log. <see cref="Caught"/>: the owner has hunted it (filled picture, else outline). A hidden tile carries no numbers.
/// <see cref="Tier"/>: the rarest weight the owner has hunted of it (<see cref="HuntRarity"/>); common for a mystery and a hidden tile.
/// </summary>
public sealed record LifeEntry(string Species, bool Hidden, bool Caught, float? BestKg, float? LongestM, long CaughtAtUnix, HuntTier Tier = HuntTier.Common);

/// <summary>A longest player kill (<see cref="PlayerKillBook"/>): who killed whom, how far, with what, when. Names as players.name has them.</summary>
public sealed record PlayerKillView(long KillerId, string Killer, long VictimId, string Victim, float DistanceM, string Weapon, long AtUnix);

/// <summary>A player's log: totals, the heaviest and the longest shot, a tile for every species of <see cref="HuntSpecies.Keys"/>, and their longest player kill.</summary>
public sealed record PlayerLogView(long PlayerId, string Name, int Hunts, int Species, HuntView? Heaviest, HuntView? Longest, IReadOnlyList<LifeEntry> Life, PlayerKillView? Kill = null);

/// <summary>
/// One species' card on the Records tab. <see cref="Caught"/>: somebody has hunted it. <see cref="Heaviest"/> is null for a mystery,
/// <see cref="First"/> (the first catch) is kept for every species. <see cref="Longest"/> is null until a shot with a distance.
/// </summary>
public sealed record SpeciesRecordView(string Species, bool Hidden, bool Caught, HuntView? Heaviest, HuntView? Longest, HuntView? First);

/// <summary>Which sort column the All hunts table uses.</summary>
public enum HuntSort : byte { Time = 0, Species = 1, Hunter = 2, Kg = 3, Distance = 4, Weapon = 5 }

/// <summary>
/// A page of the hunt table, as the store takes it. <see cref="SpeciesAny"/> null: every species; else only those species keys (empty: none).
/// <see cref="Weapon"/> "" and <see cref="PlayerId"/> 0: no filter. <see cref="HiddenLast"/>: the mystery ids the viewer has not caught, sorted after the rest by species.
/// Nulls (a mystery's weight, a run over's distance) sort last in either direction; ties break on the newer hunt.
/// </summary>
public sealed record HuntQuery(int Page, IReadOnlyList<string>? SpeciesAny, string Weapon, long PlayerId, HuntSort Sort, bool Descending, IReadOnlyList<string> HiddenLast)
{
    public int Skip => Math.Max(0, Page) * HuntBook.PageSize;
}

/// <summary>One page of <see cref="HuntRow"/>s and how many hunts match in all.</summary>
public sealed record HuntPage(IReadOnlyList<HuntRow> Rows, int Total);

/// <summary>The same rule as the SQL stores' WHERE and ORDER BY, over rows in memory (the in-memory store; the contract tests hold the three together).</summary>
public static class HuntQueryRules
{
    public static HuntPage Apply(IEnumerable<HuntRow> rows, HuntQuery q)
    {
        var hit = rows.Where(r => Matches(r.Hunt, q)).ToList();
        hit.Sort((a, b) => Compare(a, b, q));
        return new HuntPage(hit.Skip(q.Skip).Take(HuntBook.PageSize).ToList(), hit.Count);
    }

    public static bool Matches(HuntRecord h, HuntQuery q) =>
        (q.SpeciesAny == null || q.SpeciesAny.Contains(h.Species)) && (q.Weapon == "" || h.Weapon == q.Weapon) && (q.PlayerId == 0 || h.PlayerId == q.PlayerId);

    static int Compare(HuntRow a, HuntRow b, HuntQuery q)
    {
        var dir = q.Descending ? -1 : 1;
        int c = q.Sort switch
        {
            HuntSort.Time => dir * a.Hunt.AtUtc.CompareTo(b.Hunt.AtUtc),
            HuntSort.Species => q.HiddenLast.Contains(a.Hunt.Species).CompareTo(q.HiddenLast.Contains(b.Hunt.Species)) is var h and not 0 ? h : dir * string.CompareOrdinal(a.Hunt.Species, b.Hunt.Species),
            HuntSort.Hunter => dir * string.CompareOrdinal(a.Hunter.ToLowerInvariant(), b.Hunter.ToLowerInvariant()),
            HuntSort.Kg => Nullable(a.Hunt.Kg, b.Hunt.Kg, dir),
            HuntSort.Distance => Nullable(a.Hunt.DistanceM, b.Hunt.DistanceM, dir),
            HuntSort.Weapon => dir * string.CompareOrdinal(a.Hunt.Weapon, b.Hunt.Weapon),
            _ => 0,
        };
        if (c != 0) return c;
        return q.Sort == HuntSort.Time ? dir * a.Hunt.Id.CompareTo(b.Hunt.Id) : b.Hunt.Id.CompareTo(a.Hunt.Id);
    }

    static int Nullable(float? a, float? b, int dir)
    {
        if (a == null || b == null) return (a == null).CompareTo(b == null);
        return dir * a.Value.CompareTo(b.Value);
    }
}

namespace Drycastle.ShooterCore;

/// <summary>What a new hunt did to the book: the player's first of its species, and the records it took (a first one that is also a record is one outcome).</summary>
public readonly record struct HuntOutcome(bool FirstTime, bool Heaviest, bool Longest)
{
    /// <summary>Whether the server announces it. An ordinary hunt is not.</summary>
    public bool Announce => FirstTime || Heaviest || Longest;
}

/// <summary>
/// The Hunter's Log's rules, in one place (GLOSSARY "Hunter's Log"): what the records, the Life List, the totals and the announcements are. Pure
/// and in memory, built by replaying the stored hunts oldest first and then fed each new one, so the server answers every query and detects every
/// first and record without the database. A tie keeps the earlier hunt (a record is beaten only by more); a run over holds no longest shot; a mystery
/// holds its first catch and its longest shot. Names of hunters are kept beside, for the views. Not thread-safe: the server's tick owns it.
/// </summary>
public sealed class HuntBook
{
    public const int PageSize = 50;

    sealed class Best
    {
        public HuntRecord? Heaviest, Longest, First;
        public int Count;
        /// <summary>The rarest weight among the hunts (not always the heaviest: a heavy doe is rarer than a middling deer).</summary>
        public HuntTier Tier;

        public HuntOutcome Add(HuntRecord h, HuntTier tier)
        {
            if (tier > Tier) Tier = tier;
            var first = Count == 0;
            Count++;
            First ??= h;
            var heavy = h.Kg is { } kg && (Heaviest?.Kg is not { } hk || kg > hk);
            var far = h.CountsForLongest && (Longest?.DistanceM is not { } ld || h.DistanceM!.Value > ld);
            if (heavy) Heaviest = h;
            if (far) Longest = h;
            return new HuntOutcome(first, heavy, far);
        }
    }

    readonly Dictionary<string, Best> _server = new();
    readonly Dictionary<long, Dictionary<string, Best>> _players = new();
    readonly Dictionary<long, string> _names = new();
    static readonly IReadOnlySet<string> None = new HashSet<string>();
    readonly Func<HuntRecord, HuntTier>? _rarity;

    /// <param name="rarity">The rarity of a hunt's weight (<see cref="HuntRarity.Of(AnimalsFile, HuntRecord)"/>); null: every hunt is common.</param>
    public HuntBook(Func<HuntRecord, HuntTier>? rarity = null) => _rarity = rarity;

    /// <summary>The book of hunts that were stored, oldest first.</summary>
    public static HuntBook From(IEnumerable<HuntRow> rows, Func<HuntRecord, HuntTier>? rarity = null)
    {
        var book = new HuntBook(rarity);
        foreach (var r in rows)
        {
            book.SetName(r.Hunt.PlayerId, r.Hunter);
            book.Add(r.Hunt);
        }
        return book;
    }

    public void SetName(long playerId, string name)
    {
        if (playerId != 0 && name != "") _names[playerId] = name;
    }

    public string NameOf(long playerId) => _names.GetValueOrDefault(playerId) ?? "";

    /// <summary>Takes one hunt (newer than all that are in it) and says what it changed. A species the log does not know is ignored.</summary>
    public HuntOutcome Add(HuntRecord h)
    {
        if (!HuntSpecies.IsKnown(h.Species)) return default;
        if (!_server.TryGetValue(h.Species, out var s)) _server[h.Species] = s = new Best();
        if (!_players.TryGetValue(h.PlayerId, out var mine)) _players[h.PlayerId] = mine = new Dictionary<string, Best>();
        if (!mine.TryGetValue(h.Species, out var p)) mine[h.Species] = p = new Best();
        var tier = _rarity?.Invoke(h) ?? HuntTier.Common;
        var against = s.Add(h, tier);
        var own = p.Add(h, tier);
        return new HuntOutcome(own.FirstTime, against.Heaviest, against.Longest);
    }

    /// <summary>The mystery creatures a player has caught: what they may see the names and shapes of.</summary>
    public IReadOnlySet<string> CaughtBy(long playerId)
    {
        if (!_players.TryGetValue(playerId, out var mine)) return None;
        HashSet<string>? set = null;
        foreach (var m in Mysteries.All)
            if (mine.TryGetValue(m.Id, out var b) && b.Count > 0) (set ??= new()).Add(m.Id);
        return set ?? None;
    }

    /// <summary>The records of every species, index species first, then the mysteries, as <paramref name="viewer"/> may see them.</summary>
    public IReadOnlyList<SpeciesRecordView> Records(IReadOnlySet<string> viewer)
    {
        var list = new List<SpeciesRecordView>(HuntSpecies.Keys.Count);
        foreach (var key in HuntSpecies.Keys)
        {
            _server.TryGetValue(key, out var b);
            list.Add(new SpeciesRecordView(HuntRedaction.SpeciesFor(key, viewer), HuntRedaction.IsHidden(key, viewer), b is { Count: > 0 },
                View(b?.Heaviest, viewer), View(b?.Longest, viewer), View(b?.First, viewer)));
        }
        return list;
    }

    /// <summary>One player's log as <paramref name="viewer"/> may see it; null for a player the book has never heard of.</summary>
    public PlayerLogView? PlayerLog(long playerId, IReadOnlySet<string> viewer)
    {
        _players.TryGetValue(playerId, out var mine);
        if (mine == null && !_names.ContainsKey(playerId)) return null;
        var life = new List<LifeEntry>(HuntSpecies.Keys.Count);
        int hunts = 0, species = 0;
        HuntRecord? heaviest = null, longest = null;
        foreach (var key in HuntSpecies.Keys)
        {
            Best? b = null;
            mine?.TryGetValue(key, out b);
            var caught = b is { Count: > 0 };
            var hidden = HuntRedaction.IsHidden(key, viewer);
            life.Add(new LifeEntry(HuntRedaction.SpeciesFor(key, viewer), hidden, caught,
                hidden ? null : b?.Heaviest?.Kg, hidden ? null : b?.Longest?.DistanceM, hidden || b?.First == null ? 0 : Unix(b.First.AtUtc), hidden || b == null ? HuntTier.Common : b.Tier));
            if (!caught) continue;
            hunts += b!.Count;
            species++;
            heaviest = Earlier(heaviest, b.Heaviest, (x, y) => x.Kg!.Value.CompareTo(y.Kg!.Value));
            longest = Earlier(longest, b.Longest, (x, y) => x.DistanceM!.Value.CompareTo(y.DistanceM!.Value));
        }
        return new PlayerLogView(playerId, NameOf(playerId), hunts, species, View(heaviest, viewer), View(longest, viewer), life);
    }

    /// <summary>One hunt as <paramref name="viewer"/> may see it (<see cref="HuntRedaction.View"/>), with its hunter's name.</summary>
    public HuntView? View(HuntRecord? h, IReadOnlySet<string> viewer) => h == null ? null : HuntRedaction.View(h, NameOf(h.PlayerId), viewer);

    /// <summary>The better of two hunts by <paramref name="cmp"/> (null: none); a tie keeps the earlier one.</summary>
    static HuntRecord? Earlier(HuntRecord? a, HuntRecord? b, Comparison<HuntRecord> cmp)
    {
        if (a == null) return b;
        if (b == null) return a;
        var c = cmp(b, a);
        return c > 0 || c == 0 && b.AtUtc < a.AtUtc ? b : a;
    }

    internal static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

/// <summary>
/// The per-viewer redaction (docs/hunters-log.md "Redaction"): a viewer who has not caught a mystery creature is never sent its name or shape, only
/// a hidden id, and no hit diagram; hunter, time, distance and weapon stay. Every query goes through here, so a new catch unlocks old rows,
/// other players' tiles and the records at once. A viewer is the set of mystery ids they have caught (<see cref="HuntBook.CaughtBy"/>), empty for a guest.
/// </summary>
public static class HuntRedaction
{
    public static bool IsHidden(string species, IReadOnlySet<string> viewer) => Mysteries.Is(species) && !viewer.Contains(species);

    /// <summary>The species key a viewer is sent: itself, or its hidden id.</summary>
    public static string SpeciesFor(string species, IReadOnlySet<string> viewer) => IsHidden(species, viewer) ? Mysteries.HiddenId(species) : species;

    public static HuntView View(HuntRecord h, string hunter, IReadOnlySet<string> viewer)
    {
        var hidden = IsHidden(h.Species, viewer);
        return hidden
            ? new HuntView(h.PlayerId, hunter, Mysteries.HiddenId(h.Species), "", "", h.Kg, h.DistanceM, h.Weapon, null, HuntBook.Unix(h.AtUtc), true)
            : new HuntView(h.PlayerId, hunter, h.Species, h.Exact, h.Variant, h.Kg, h.DistanceM, h.Weapon, h.Diagram, HuntBook.Unix(h.AtUtc), false);
    }

    /// <summary>
    /// The species keys the table filter means for a viewer: the one species asked for; or, for <see cref="Mysteries.AllHidden"/> or a mystery they
    /// have not caught (the two are one option, so asking for it reveals nothing), the mysteries hidden from them. Null: no filter.
    /// </summary>
    public static IReadOnlyList<string>? FilterFor(string species, IReadOnlySet<string> viewer)
    {
        if (species == "") return null;
        if (species == Mysteries.AllHidden || IsHidden(species, viewer)) return HiddenFor(viewer);
        return IndexSpecies.Is(species) || Mysteries.Is(species) ? new[] { species } : Array.Empty<string>();
    }

    /// <summary>The mystery ids a viewer has not caught.</summary>
    public static IReadOnlyList<string> HiddenFor(IReadOnlySet<string> viewer) => Mysteries.All.Where(m => !viewer.Contains(m.Id)).Select(m => m.Id).ToArray();
}

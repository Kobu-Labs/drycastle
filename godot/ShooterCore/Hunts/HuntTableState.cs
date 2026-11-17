namespace Drycastle.ShooterCore;

/// <summary>
/// The All hunts table's query: filters, sort and page (docs/hunters-log.md). Pure. Any change of filter or sort goes back to the first page; a header
/// click sorts by it (the columns of words ascending first, time and numbers descending first) and a second click reverses.
/// </summary>
public sealed class HuntTableState
{
    public string Species { get; private set; } = "";
    public string Weapon { get; private set; } = "";
    public long PlayerId { get; private set; }
    public HuntSort Sort { get; private set; } = HuntSort.Time;
    public bool Descending { get; private set; } = true;
    public int Page { get; private set; }

    public bool Filtered => Species != "" || Weapon != "" || PlayerId != 0;

    /// <summary>Newest first is the default; a column of words starts A to Z.</summary>
    public static bool DescendingFirst(HuntSort column) => column is HuntSort.Time or HuntSort.Kg or HuntSort.Distance;

    public void ClickHeader(HuntSort column)
    {
        if (column == Sort) Descending = !Descending;
        else { Sort = column; Descending = DescendingFirst(column); }
        Page = 0;
    }

    public void SetSpecies(string species) { Species = species; Page = 0; }
    public void SetWeapon(string weapon) { Weapon = weapon; Page = 0; }
    public void SetPlayer(long id) { PlayerId = id; Page = 0; }
    public void SetPage(int page) => Page = Math.Max(0, page);

    public void ClearFilters() { Species = Weapon = ""; PlayerId = 0; Page = 0; }

    /// <summary>"All &lt;species&gt; hunts": that species only, heaviest first.</summary>
    public void JumpToSpecies(string species)
    {
        ClearFilters();
        Species = species; Sort = HuntSort.Kg; Descending = true;
    }

    /// <summary>One player's hunts, newest first.</summary>
    public void JumpToPlayer(long id)
    {
        ClearFilters();
        PlayerId = id; Sort = HuntSort.Time; Descending = true;
    }

    public HuntsRequest ToRequest(ushort req) => new(req, (ushort)Math.Min(Page, ushort.MaxValue), Sort, Descending, Species, Weapon, PlayerId);

    public static int PageCount(long total) => (int)Math.Max(1, (total + HuntBook.PageSize - 1) / HuntBook.PageSize);

    /// <summary>The page numbers the pager shows (0-based; -1 is a gap): the first, the last, and two either side of the current one.</summary>
    public static IReadOnlyList<int> PageWindow(int page, int pages)
    {
        var list = new List<int>();
        var last = -2;
        for (var i = 0; i < pages; i++)
        {
            if (i != 0 && i != pages - 1 && Math.Abs(i - page) > 2) continue;
            if (last >= 0 && i - last > 1) list.Add(-1);
            list.Add(i);
            last = i;
        }
        return list;
    }

    /// <summary>The 1-based range of the hunts a page shows ("a-b of N"); (0, 0) when there are none.</summary>
    public static (int From, int To) Range(int page, int rowsOnPage) => rowsOnPage <= 0 ? (0, 0) : (page * HuntBook.PageSize + 1, page * HuntBook.PageSize + rowsOnPage);
}

/// <summary>The players the log has seen so far (every hunter of every answer), for the player filter: the server sends no list of players.</summary>
public sealed class HuntPeople
{
    readonly Dictionary<long, string> _names = new();
    List<(long Id, string Name)>? _sorted;

    public int Count => _names.Count;

    public void Learn(long id, string name)
    {
        if (id == 0 || name == "" || (_names.TryGetValue(id, out var known) && known == name)) return;
        _names[id] = name;
        _sorted = null;
    }

    public void Learn(HuntView? h) { if (h != null) Learn(h.PlayerId, h.Hunter); }

    public string NameOf(long id) => _names.GetValueOrDefault(id) ?? "";

    /// <summary>By name, A to Z, then id.</summary>
    public IReadOnlyList<(long Id, string Name)> Sorted() =>
        _sorted ??= _names.Select(kv => (kv.Key, kv.Value)).OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Key).ToList();
}

namespace Drycastle.ShooterCore;

/// <summary>
/// Id lookups for the parsed data files (T3-019). The first lookup builds a dictionary once, so per-frame callers (the gunner HUD)
/// do not scan the list. Same answer as the <c>FirstOrDefault(x =&gt; x.Id == id)</c> scans it replaces: the first item with an id wins,
/// and a null or missing id finds nothing.
/// </summary>
public static class DataIndex
{
    public static T? Find<T>(ref Dictionary<string, T>? cache, IEnumerable<T> items, Func<T, string?> id, string? key) where T : class
    {
        if (key is null) return null;
        cache ??= Build(items, id);
        return cache.TryGetValue(key, out var found) ? found : null;
    }

    public static Dictionary<string, T> Build<T>(IEnumerable<T> items, Func<T, string?> id)
    {
        var map = new Dictionary<string, T>();
        foreach (var item in items)
        {
            var key = id(item);
            if (key is not null) map.TryAdd(key, item);
        }
        return map;
    }
}

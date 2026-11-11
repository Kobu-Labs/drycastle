namespace Suchohrad.ShooterCore;

/// <summary>
/// A queue of things due at a time (seconds on the server's clock): <see cref="TakeDue"/> hands back the ones whose time has come, in
/// due order (ties in the order they were added). Pure, so the tests run it; the server ticks it once a tick.
/// </summary>
public sealed class TimedQueue<T>
{
    readonly List<(double Due, long Seq, T Item)> _items = new();
    long _seq;

    public int Count => _items.Count;

    public void Add(double due, T item) => _items.Add((due, _seq++, item));

    /// <summary>Removes and returns the items due at or before <paramref name="now"/>, earliest first.</summary>
    public List<T> TakeDue(double now)
    {
        var due = _items.Where(i => i.Due <= now).OrderBy(i => i.Due).ThenBy(i => i.Seq).Select(i => i.Item).ToList();
        if (due.Count > 0) _items.RemoveAll(i => i.Due <= now);
        return due;
    }

    /// <summary>Drops everything still waiting (a round reset: pending shells are gone with the round).</summary>
    public void Clear() => _items.Clear();
}

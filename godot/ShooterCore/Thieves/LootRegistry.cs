using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The kinds of Loot the world has (<see cref="IStealSource"/>) and which targets Thieves have claimed. One source per kind; one Thief per target (a claim is exclusive until released).
/// Pure.
/// </summary>
public sealed class LootRegistry
{
    readonly Dictionary<LootKind, IStealSource> _sources = new();
    readonly HashSet<(LootKind, int, bool)> _claims = new();

    /// <summary>Adds a kind of Loot. Throws when the kind has a source already.</summary>
    public void Register(IStealSource source)
    {
        if (!_sources.TryAdd(source.Kind, source)) throw new InvalidOperationException($"loot kind {source.Kind} is already registered");
    }

    public IStealSource? Source(LootKind kind) => _sources.GetValueOrDefault(kind);

    public IEnumerable<LootKind> Kinds => _sources.Keys;

    /// <summary>Targets claimed now (a Thief is on its way to or at them).</summary>
    public int Claimed => _claims.Count;

    public bool IsClaimed(in StealTarget t) => _claims.Contains(t.Key);

    /// <summary>Claims a target for one Thief. False when it is claimed already.</summary>
    public bool Claim(in StealTarget t) => _claims.Add(t.Key);

    /// <summary>The claim ends (the Thief took it, gave up or died). A claim that is not there is ignored.</summary>
    public void Release(in StealTarget t) => _claims.Remove(t.Key);

    /// <summary>Appends every free, unclaimed thing of every registered kind within <paramref name="radius"/> of <paramref name="near"/>.</summary>
    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        var from = into.Count;
        foreach (var s in _sources.Values) s.FreeNear(near, radius, into);
        for (var i = into.Count - 1; i >= from; i--)
            if (_claims.Contains(into[i].Key)) into.RemoveAt(i);
    }

    /// <summary>Appends the ground (x, z) of everything a Thief could take: where a van's stop is worth something (claimed things count: they are still there).</summary>
    public void Places(List<Vector2> into)
    {
        foreach (var s in _sources.Values) s.Places(into);
    }

    /// <summary>The target as it is now, if its kind has a source and it is still free there. A claim does not matter.</summary>
    public bool TryFind(in StealTarget t, out StealTarget now)
    {
        now = t;
        return _sources.TryGetValue(t.Kind, out var s) && s.TryFind(t.Id, out now);
    }

    /// <summary>A new round: no claims, every source back to its start.</summary>
    public void Reset()
    {
        _claims.Clear();
        foreach (var s in _sources.Values) s.Reset();
    }
}

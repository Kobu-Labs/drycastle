using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// What a shot, a blast or a turret can hit right now: every living player, dummy, citizen, animal and the like as a <see cref="HitTarget"/>. The kinds are not known here: each
/// one that can be hit adds itself with <see cref="AddSource"/> (the order of the sources is the order of the list), and everything that asks gets one list from <see cref="Live"/>.
/// </summary>
internal sealed class TargetBook
{
    readonly List<Action<List<HitTarget>>> _sources = new();
    readonly Func<int> _expected;

    /// <summary>Lists that <see cref="Live"/> handed out and their callers gave back (<see cref="Return"/>), for the next call to reuse.</summary>
    readonly Stack<List<HitTarget>> _pool = new();

    /// <param name="expected">How many players there are, to size a new list.</param>
    public TargetBook(Func<int> expected) => _expected = expected;

    /// <summary>A kind of target: it adds its own to the list <see cref="Live"/> builds.</summary>
    public void AddSource(Action<List<HitTarget>> addTo) => _sources.Add(addTo);

    /// <summary>
    /// The live targets now, in a list the caller owns until it gives it back with <see cref="Return"/>. Each call gets its own list on purpose:
    /// callers nest (an explosion can detonate a car, which explodes again) and each holds its list while it iterates, so one shared buffer would be
    /// cleared under them. A list that is not given back only costs an allocation.
    /// </summary>
    public List<HitTarget> Live()
    {
        var profT = Prof.Begin(Prof.S.LiveTargets);
        var list = Rent();
        foreach (var addTo in _sources) addTo(list);
        PerfCounters.LiveTargetsCalls++; PerfCounters.LiveTargetsElems += list.Count;
        Prof.End(Prof.S.LiveTargets, profT);
        return list;
    }

    /// <summary>An empty list, to be given back with <see cref="Return"/> like the ones <see cref="Live"/> hands out (for a caller that builds its own targets).</summary>
    public List<HitTarget> Rent() => _pool.Count > 0 ? _pool.Pop() : new List<HitTarget>(_expected() + 4);

    /// <summary>Gives a list from <see cref="Live"/> back, once nothing iterates it any more.</summary>
    public void Return(List<HitTarget> list)
    {
        list.Clear();
        _pool.Push(list);
    }
}

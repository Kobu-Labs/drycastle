namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// A Job's party as a set, read live from <see cref="IJobParties"/> at every question (a Squad mate who joins midway is in it at once, one who leaves is out). One instance per Job: what a
/// Job hands its Duffels (<see cref="Cartel.Duffel.Party"/>, removed together by reference) and the players its Rival crew attacks on sight. <see cref="Contains"/> is
/// <see cref="IJobParties.Has"/> (asked per lying Duffel per player per tick: no allocation); the count and the enumeration read the members list.
/// </summary>
public sealed class JobPartySet(IJobParties parties, JobOwner owner) : IReadOnlySet<ushort>
{
    public JobOwner Owner => owner;

    IReadOnlyList<ushort> Now => parties.MembersOf(owner);

    public bool Contains(ushort player) => parties.Has(owner, player);

    public int Count => Now.Count;
    public IEnumerator<ushort> GetEnumerator() => Now.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    HashSet<ushort> Copy() => new(Now);
    public bool IsProperSubsetOf(IEnumerable<ushort> other) => Copy().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<ushort> other) => Copy().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<ushort> other) => Copy().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<ushort> other) => Copy().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<ushort> other) => Copy().Overlaps(other);
    public bool SetEquals(IEnumerable<ushort> other) => Copy().SetEquals(other);
}

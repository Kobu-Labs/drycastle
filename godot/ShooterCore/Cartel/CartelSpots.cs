using System.Numerics;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The Drop points and Hit spots the running Jobs hold (docs/cartel.md "Who runs a job": two Jobs never share a drop point or a target spot). A Job claims one in the book under
/// <see cref="Key"/> (<c>"cartel_drop:3"</c>, the index in <see cref="CartelMap.DropPoints"/> or <see cref="CartelMap.HitSpots"/>); a new pick stays <c>apart_m</c> from every point any running Job holds
/// (of both kinds) and a data distance from a point: the Contact for a simple Job, the objective for a Heist's Drop point.
/// </summary>
public static class CartelSpots
{
    /// <summary>The book's name of a point: the zone kind (<see cref="CartelZones.DropKind"/>, <see cref="CartelZones.HitKind"/>) and its index in the map's pool.</summary>
    public static string Key(string zoneKind, int index) => $"{zoneKind}:{index}";

    static IReadOnlyList<Vector3> Pool(CartelMap map, string zoneKind) => zoneKind switch
    {
        CartelZones.DropKind => map.DropPoints,
        CartelZones.HitKind => map.HitSpots,
        CartelZones.HeliKind => map.HeliPoints,
        CartelZones.BoatKind => map.BoatPoints,
        _ => Array.Empty<Vector3>(),
    };

    /// <summary>The points the running Jobs hold now, of both kinds.</summary>
    public static List<Vector3> Held(JobBook book, CartelMap map)
    {
        var held = new List<Vector3>();
        foreach (var job in book.Running)
            foreach (var key in book.ReservedBy(job))
            {
                var colon = key.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(key.AsSpan(colon + 1), out var i)) continue;
                var pool = Pool(map, key[..colon]);
                if (i >= 0 && i < pool.Count) held.Add(pool[i]);
            }
        return held;
    }

    /// <summary>
    /// Picks a random point of the kind at least <paramref name="minFromM"/> (ground metres) from <paramref name="from"/> (the Contact for a simple Job, the objective for a Heist's Drop point;
    /// none: no such rule) and <paramref name="apartM"/> from every point a running Job holds, and claims it for <paramref name="job"/>. Null with the reason when there is none: the map has none of
    /// the kind, none lies far enough from <paramref name="fromName"/> ("the Contact", "the plantation"), or the Jobs hold every one that does (the book releases nothing: a refused Begin is
    /// released by the book).
    /// </summary>
    public static Vector3? Claim(Job job, JobBook book, CartelMap map, string zoneKind, Random rng, Vector3? from, string fromName, float minFromM, float apartM, out string? refusal)
    {
        refusal = null;
        var pool = Pool(map, zoneKind);
        var what = zoneKind switch { CartelZones.HitKind => "hit spot", CartelZones.HeliKind => "landing spot", CartelZones.BoatKind => "mooring", _ => "drop point" };
        if (pool.Count == 0) { refusal = $"This map has no {what}."; return null; }
        var apart = CartelMap.Apart(Held(book, map), apartM);
        var min2 = minFromM * minFromM;
        bool FarEnough(Vector3 p) => from is not { } f || Vector2.DistanceSquared(new Vector2(p.X, p.Z), new Vector2(f.X, f.Z)) >= min2;
        var pick = CartelMap.Pick(pool, rng, p => apart(p) && FarEnough(p) && !book.IsReserved(Key(zoneKind, IndexOf(pool, p))));
        if (pick is not { } at)
        {
            // the Jobs free their points as they end; a map whose every point sits near the from point never will
            refusal = pool.Any(FarEnough) ? $"No {what} {minFromM:F0} m or more from {fromName} is free now: another job holds every one." : $"No {what} is far enough from {fromName} on this map.";
            return null;
        }
        book.Reserve(job, Key(zoneKind, IndexOf(pool, at)));
        return at;
    }

    static int IndexOf(IReadOnlyList<Vector3> pool, Vector3 p)
    {
        for (var i = 0; i < pool.Count; i++) if (pool[i] == p) return i;
        return -1;
    }
}

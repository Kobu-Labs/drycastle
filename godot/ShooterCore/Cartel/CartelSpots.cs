using System.Numerics;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The Drop points and Hit spots the running Jobs hold (docs/cartel.md "Who runs a job": two Jobs never share a drop point or a target spot). A Job claims one in the book under
/// <see cref="Key"/> (<c>"cartel_drop:3"</c>, the index in <see cref="CartelMap.DropPoints"/> or <see cref="CartelMap.HitSpots"/>); a new pick stays <c>apart_m</c> from every point any running Job holds
/// (of both kinds) and a data distance from the Contact. Heists that end at a drop point claim it the same way.
/// </summary>
public static class CartelSpots
{
    /// <summary>The book's name of a point: the zone kind (<see cref="CartelZones.DropKind"/>, <see cref="CartelZones.HitKind"/>) and its index in the map's pool.</summary>
    public static string Key(string zoneKind, int index) => $"{zoneKind}:{index}";

    static IReadOnlyList<Vector3> Pool(CartelMap map, string zoneKind) => zoneKind switch
    {
        CartelZones.DropKind => map.DropPoints,
        CartelZones.HitKind => map.HitSpots,
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
    /// Picks a random point of the kind at least <paramref name="minFromContactM"/> (ground metres) from the Contact and <paramref name="apartM"/> from every point a running Job holds, and claims it
    /// for <paramref name="job"/>. Null with the reason when none is left: all held by Jobs, or none far enough from the Contact on this map (the book releases nothing: a refused Begin is
/// released by the book).
    /// </summary>
    public static Vector3? Claim(Job job, JobBook book, CartelMap map, string zoneKind, Random rng, float minFromContactM, float apartM, out string? refusal)
    {
        refusal = null;
        var pool = Pool(map, zoneKind);
        var what = zoneKind == CartelZones.HitKind ? "hit spot" : "drop point";
        if (pool.Count == 0) { refusal = $"This map has no {what}."; return null; }
        var apart = CartelMap.Apart(Held(book, map), apartM);
        var contact = map.Contact;
        bool FarEnough(Vector3 p) => contact is not { } c || c.GroundDistance(p) >= minFromContactM;
        var pick = CartelMap.Pick(pool, rng, p => apart(p) && FarEnough(p) && !book.IsReserved(Key(zoneKind, IndexOf(pool, p))));
        if (pick is not { } at)
        {
            // the Jobs free their points as they end; a map whose every point sits near the Contact never will
            refusal = pool.Any(FarEnough) ? $"No {what} is free now: another job holds every one." : $"No {what} is far enough from the Contact on this map.";
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

    /// <summary>Checks a kind's per-tier array: one positive number per tier of cartel.json.</summary>
    internal static void CheckPerTier(float[] values, CartelFile file, string what)
    {
        if (values.Length != file.Tiers.Count) throw new Data.DataFileException($"cartel.json: {what} needs one number per tier ({file.Tiers.Count})");
        if (values.Any(s => !float.IsFinite(s) || s <= 0)) throw new Data.DataFileException($"cartel.json: {what} must all be more than 0");
    }

    internal static void CheckPerTier(int[] values, CartelFile file, string what)
    {
        if (values.Length != file.Tiers.Count) throw new Data.DataFileException($"cartel.json: {what} needs one number per tier ({file.Tiers.Count})");
        if (values.Any(s => s < 0)) throw new Data.DataFileException($"cartel.json: {what} must not be negative");
    }

    internal static T OfTier<T>(T[] values, int tier) => values[Math.Clamp(tier, 1, values.Length) - 1];
}

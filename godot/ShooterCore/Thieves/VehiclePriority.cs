using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Vehicles are the Thieves' priority (GLOSSARY "Loot"): when a car newly becomes available to a Raid, a share of its free Thieves is sent to its tires. Pure: who goes and how many
/// (<see cref="Count"/>, <see cref="Assign"/>) and which cars are new (<see cref="VehicleArrivals"/>).
/// </summary>
public static class VehiclePriority
{
    public const int Wheels = 4;

    /// <summary>
    /// How many of <paramref name="freeThieves"/> go to a car: <see cref="VehiclePriorityConfig.ThiefShare"/> of them rounded up (at least one), never more than
    /// <see cref="VehiclePriorityConfig.MaxThievesPerVehicle"/> less the <paramref name="alreadyOn"/> it, one per wheel, nor than free tires.
    /// </summary>
    public static int Count(int freeThieves, int freeTires, int alreadyOn, VehiclePriorityConfig cfg)
    {
        if (freeThieves <= 0 || freeTires <= 0) return 0;
        var n = Math.Max(1, (int)MathF.Ceiling(cfg.ThiefShare * freeThieves - 1e-4f));
        var cap = Math.Min(Math.Min(cfg.MaxThievesPerVehicle, Wheels) - alreadyOn, freeTires);
        return Math.Clamp(Math.Min(n, cap), 0, freeThieves);
    }

    /// <summary>
    /// Who takes which tire: the <paramref name="count"/> Thieves nearest the car, each (nearest first) to the nearest tire still unassigned. Pairs of (index into
    /// <paramref name="thieves"/>, index into <paramref name="tires"/>), from the positions of the Thieves and the tires' work spots.
    /// </summary>
    public static List<(int Thief, int Tire)> Assign(IReadOnlyList<Vector3> thieves, IReadOnlyList<Vector3> tires, Vector3 car, int count)
    {
        var result = new List<(int, int)>();
        count = Math.Min(count, Math.Min(thieves.Count, tires.Count));
        if (count <= 0) return result;
        var order = Enumerable.Range(0, thieves.Count).OrderBy(i => Flat(thieves[i], car)).Take(count).ToList();
        var left = Enumerable.Range(0, tires.Count).ToList();
        foreach (var t in order)
        {
            var best = left.OrderBy(w => Flat(tires[w], thieves[t])).First();
            left.Remove(best);
            result.Add((t, best));
        }
        return result;
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}

/// <summary>
/// The cars of one Raid that are available to it (at rest, a wheel still on, within the radius of the van): which are new since the last scan. A car stays handled until it is no longer available (driven
/// off, out of the radius, stripped) and comes back. The first scan only notes what is there (the Thieves' pick prefers tires anyway).
/// </summary>
public sealed class VehicleArrivals
{
    readonly HashSet<int> _handled = new();
    readonly HashSet<int> _now = new();
    bool _primed;

    public int HandledCount => _handled.Count;

    /// <summary>Appends the ids of the cars that became available since the last scan to <paramref name="arrived"/>.</summary>
    public void Scan(IEnumerable<TireCar> cars, Vector3 centre, float radius, List<int> arrived)
    {
        _now.Clear();
        foreach (var c in cars)
        {
            if (!c.AtRest || (c.WheelsOff & 0b1111) == 0b1111) continue;
            var dx = c.Position.X - centre.X; var dz = c.Position.Z - centre.Z;
            if (dx * dx + dz * dz > radius * radius) continue;
            _now.Add(c.Id);
            if (_handled.Add(c.Id) && _primed) arrived.Add(c.Id);
        }
        _handled.IntersectWith(_now);
        _primed = true;
    }

    public void Clear() { _handled.Clear(); _primed = false; }
}

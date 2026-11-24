using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Thieves;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The poles of the map that carry wires, as the pure <see cref="ConnectorSource"/> sees them (docs/events/thieves/07). A pole's climb base is a point of the navigation mesh beside the column
/// (<see cref="ConnectorLootConfig.BaseOffsetM"/> away: the closest of eight points around it that the mesh really has), its top is straight above the base at the height of the highest wire seat (or
/// <see cref="ConnectorLootConfig.TopFraction"/> of the pole's height). A pole with no mesh point near is no target (no Thief could walk to it).
/// </summary>
internal sealed class PoleSites : IPoles
{
    /// <summary>The farthest the mesh may be from a wanted base point for the point to count (metres).</summary>
    const float BaseSnapM = 1.5f;

    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<BotNav?> _nav;
    readonly ConnectorLootConfig _cfg;
    List<PoleSpot>? _spots;
    readonly Dictionary<int, PoleSpot> _byId = new();

    public PoleSites(Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav, ConnectorLootConfig cfg) { _map = map; _nav = nav; _cfg = cfg; }

    /// <summary>Built on first use, once the map and the mesh exist (nothing is cached while they do not).</summary>
    List<PoleSpot> Built()
    {
        if (_spots != null) return _spots;
        if (_map == null || _nav() == null) return new List<PoleSpot>();
        var spots = new List<PoleSpot>();
        foreach (var site in _map.PoleSites())
        {
            var foot = site.Foot;
            if (BaseBeside(foot, _cfg.BaseOffsetM) is not { } b) continue;
            var topY = (site.TopSeat?.Y ?? foot.Y + site.Height * _cfg.TopFraction) - _cfg.FeetBelowTopM;
            if (topY < b.Y + 1.5f) continue;
            spots.Add(new PoleSpot(spots.Count, foot, b, new NVec3(b.X, topY, b.Z)));
        }
        _spots = spots;
        foreach (var p in spots) _byId[p.Id] = p;
        GD.Print($"server: Thieves: {spots.Count} poles with wires are connector targets");
        return spots;
    }

    /// <summary>The mesh point nearest to one of eight points <paramref name="r"/> around the column (not inside the column's reach), or null when the mesh is farther than <see cref="BaseSnapM"/> from all of them.</summary>
    NVec3? BaseBeside(NVec3 foot, float r)
    {
        NVec3? best = null;
        var bestErr = BaseSnapM;
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4f;
            var want = new Vector3(foot.X + MathF.Cos(a) * r, foot.Y, foot.Z + MathF.Sin(a) * r);
            var got = _nav()!.Snap(want);
            var err = new Vector2(got.X - want.X, got.Z - want.Z).Length();
            var fromColumn = new Vector2(got.X - foot.X, got.Z - foot.Z).Length();
            if (err >= bestErr || fromColumn < r * 0.5f) continue;
            bestErr = err; best = got.ToNumerics();
        }
        return best;
    }

    public IReadOnlyList<PoleSpot> All() => Built();

    public bool TryGet(int id, out PoleSpot pole)
    {
        Built();
        return _byId.TryGetValue(id, out pole);
    }
}

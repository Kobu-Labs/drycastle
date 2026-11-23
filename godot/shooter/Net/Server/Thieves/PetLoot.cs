using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The pet target of the Thieves event on the server (docs/events/thieves/05): the world the pure <see cref="PetSource"/> asks (the ground, the navigation mesh) and the fences of the map
/// (<c>fences.json</c> in the map folder, read once; none: nobody hops, a Thief walks the mesh in through the gate gap). A pet killed in a Thief's hands empties the Thief's hands.
/// </summary>
internal sealed class PetLoot : IPetWorld
{
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<BotNav?> _nav;
    readonly PetLootConfig _cfg;
    readonly bool _arena;
    readonly string _mapDir;
    FenceBook? _fences;

    /// <summary>The fences load on the first look at the pets (once the map is up), not here.</summary>
    public PetLoot(PetLootConfig cfg, PetKeeper keeper, GangRoster gangs, Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav, bool arenaMode, string mapDir)
    {
        _cfg = cfg; _map = map; _nav = nav; _arena = arenaMode; _mapDir = mapDir;
        Source = new PetSource(cfg, keeper, LoadFences, this);
        Source.CarriedKilled += entity =>
        {
            foreach (var gang in gangs.Gangs)
                foreach (var m in gang.Members)
                    if (m.CarriedOrigin is { Kind: LootKind.Pet } o && o.Id == entity) m.LoseCarried();
        };
    }

    public PetSource Source { get; }

    public float? GroundY(float x, float z)
    {
        if (_map == null) return null;
        var y = _map.GroundHeight(x, z);
        return float.IsNaN(y) ? null : y;
    }

    /// <summary>On the navigation mesh: its nearest point is within <c>nav_snap_m</c> (flat). No mesh: nowhere.</summary>
    public bool Walkable(NVec3 at)
    {
        if (_nav() is not { } nav) return false;
        var p = nav.Snap(new Vector3(at.X, at.Y, at.Z));
        var dx = p.X - at.X; var dz = p.Z - at.Z;
        return dx * dx + dz * dz <= _cfg.NavSnapM * _cfg.NavSnapM;
    }

    /// <summary>fences.json of the map folder, once. A map without it (not rebuilt yet), a broken one or no map: no fences.</summary>
    FenceBook LoadFences()
    {
        if (_fences != null) return _fences;
        _fences = FenceBook.Empty;
        if (_arena || string.IsNullOrEmpty(_mapDir)) return _fences;
        var path = Path.Combine(_mapDir, "fences.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}: pets are fetched through the gate gap (rebuild the map for the fence hop)"); return _fences; }
        try { _fences = FenceBook.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or InvalidOperationException or FormatException)
        {
            GD.PrintErr($"server: {path}: {e.Message}; no fence hop");
            return _fences;
        }
        GD.Print($"server: {_fences.Sections} fence sections in {_fences.Groups} property groups, from {path}");
        return _fences;
    }
}

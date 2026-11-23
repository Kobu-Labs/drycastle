using Godot;
using Drycastle.ShooterCore.Thieves;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The pet target of the Thieves event on the server (docs/events/thieves/05): the world the pure <see cref="PetSource"/> asks (the ground, the navigation mesh), the fences of the map (<c>fences.json</c> in the map
/// folder, read once; none: nobody hops, a Thief walks the mesh in through the gate gap) and the registration of the source (<see cref="InitLoot"/> calls <see cref="MakePetSource"/>). A pet killed in a Thief's hands
/// empties the Thief's hands.
/// </summary>
public partial class NetServer
{
    FenceBook? _fences;

    sealed class PetLootWorld : IPetWorld
    {
        readonly NetServer _s;
        public PetLootWorld(NetServer server) => _s = server;

        public float? GroundY(float x, float z)
        {
            if (_s.Map == null) return null;
            var y = _s.Map.GroundHeight(x, z);
            return float.IsNaN(y) ? null : y;
        }

        /// <summary>On the navigation mesh: its nearest point is within <c>nav_snap_m</c> (flat). No mesh: nowhere.</summary>
        public bool Walkable(NVec3 at)
        {
            if (_s._nav == null) return false;
            var p = _s._nav.Snap(new Vector3(at.X, at.Y, at.Z));
            var dx = p.X - at.X; var dz = p.Z - at.Z;
            return dx * dx + dz * dz <= _s.ThievesData.Loot.Pet.NavSnapM * _s.ThievesData.Loot.Pet.NavSnapM;
        }
    }

    /// <summary>fences.json of the map folder, once. A map without it (not rebuilt yet), a broken one or no map: no fences.</summary>
    FenceBook LoadFences()
    {
        if (_fences != null) return _fences;
        _fences = FenceBook.Empty;
        if (ArenaMode || string.IsNullOrEmpty(MapDir)) return _fences;
        var path = Path.Combine(MapDir, "fences.json");
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

    /// <summary>The pet target's source (registered by <see cref="InitLoot"/>). The fences load on the first look at the pets (once the map is up), not here.</summary>
    IStealSource MakePetSource()
    {
        var source = new PetSource(ThievesData.Loot.Pet, _pets.Keeper, LoadFences, new PetLootWorld(this));
        source.CarriedKilled += entity =>
        {
            if (_gangs == null) return;
            foreach (var gang in _gangs.Gangs)
                foreach (var m in gang.Members)
                    if (m.CarriedOrigin is { Kind: LootKind.Pet } o && o.Id == entity) m.LoseCarried();
        };
        return source;
    }
}

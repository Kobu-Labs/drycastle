using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The ladders on placed objects (#372, object_ladders.json): one per object of an asset that carries one, where that object stands in the loaded map.</summary>
public static class ObjectLadders
{
    const string DataPath = "res://data/object_ladders.json";

    /// <summary>object_ladders.json. A broken file fails the run, like the other data files.</summary>
    public static ObjectLaddersFile LoadConfig()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"object_ladders.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        return ObjectLaddersFile.Parse(json);
    }

    /// <summary>The ladders of every placed object of the map that carries one (none without a map).</summary>
    public static List<Ladder> Of(Drycastle.MapLoader.MapLoader? map)
    {
        var found = new List<Ladder>();
        if (map == null) return found;
        var cfg = LoadConfig();
        foreach (var asset in cfg.Ladders.Select(l => l.Asset).Distinct())
            foreach (var (_, t) in map.ObjectsOf(asset))
            {
                var x = t.Basis.X; // the turn from the axis, as MapLoader.FirstObjectOfAsset does
                found.AddRange(cfg.Place(asset, new System.Numerics.Vector2(t.Origin.X, t.Origin.Z), t.Origin.Y, Mathf.RadToDeg(MathF.Atan2(-x.Z, x.X)), x.Length()));
            }
        return found;
    }

    /// <summary>The ladders of the map: the bunkers' (bunkers.json) and the ones on placed objects.</summary>
    public static Ladders WithObjects(Ladders bunkers, Drycastle.MapLoader.MapLoader? map) => bunkers.Plus(Of(map));
}

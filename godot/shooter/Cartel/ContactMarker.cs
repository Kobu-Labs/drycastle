using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Scenery;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Cartel;

/// <summary>
/// The cartel's map data from the loaded map (docs/cartel.md): the Contact is the first placed object of <see cref="CartelZones.ContactAsset"/> (contact.json says
/// how he stands on it), the pallet the first of <see cref="CartelZones.PalletAsset"/>, the Drop points and Hit spots are the zone points of their kinds. Static map data: the server and every client read the same map.
/// </summary>
public static class ContactMarker
{
    const string DataPath = "res://data/contact.json";

    /// <summary>contact.json. A broken file fails the run, like the other data files.</summary>
    public static ContactFile LoadConfig()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"contact.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        return ContactFile.Parse(json);
    }

    /// <summary>The Contact of the map, or null when it has none (or there is no map): then there is no cartel.</summary>
    public static ContactPose? PoseIn(Drycastle.MapLoader.MapLoader? map, ContactFile cfg)
    {
        if (map?.FirstObjectOfAsset(CartelZones.ContactAsset) is not { } o) return null;
        return ContactPose.FromObject(new NVec3(o.Position.X, o.Position.Y, o.Position.Z), o.YawDeg, cfg);
    }

    /// <summary>The Cartel pallet of the map, or null when it has none (then there is no Delivery).</summary>
    public static PalletPose? PalletIn(Drycastle.MapLoader.MapLoader? map)
        => map?.FirstObjectOfAsset(CartelZones.PalletAsset) is { } o ? new PalletPose(new NVec3(o.Position.X, o.Position.Y, o.Position.Z), o.YawDeg) : null;

    /// <summary>The cartel's map data: the Contact, the pallet and the points of the cartel zones of <paramref name="points"/> (the map's zone_points.json).</summary>
    public static CartelMap Of(Drycastle.MapLoader.MapLoader? map, ZonePoints points)
    {
        var contact = PoseIn(map, LoadConfig());
        var markers = map?.ObjectsOf(CartelZones.ContactAsset).Count ?? 0;
        if (markers > 1) GD.PrintErr($"cartel: the map has {markers} Contact markers; the first (lowest object id) is the Contact, the rest do nothing");
        var pallets = map?.ObjectsOf(CartelZones.PalletAsset).Count ?? 0;
        if (pallets > 1) GD.PrintErr($"cartel: the map has {pallets} pallet markers; the first (lowest object id) is the pallet, the rest do nothing");
        return CartelMap.From(points, contact, PalletIn(map));
    }
}

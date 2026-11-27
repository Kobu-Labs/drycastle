using Godot;
using Drycastle.Shooter.Bunkers;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Scenery;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Terrain;

/// <summary>
/// What the map's baked files say about areas, each read on first use: the bunker rooms that shelter from a blast and the ladders (#224, ADR 0009), the candidate points inside every zone
/// kind (zone_points.json, ADR 0022: the Bigfoot and Scud events and the wildlife take them) and the zone polygons (zone_polygons.json: the plantation). A missing or broken file is logged and counts as none.
/// </summary>
internal sealed class MapAreas
{
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<ChurchShell?> _church;
    readonly Random _rng;
    Shelters? _shelters;
    ZonePoints? _points;
    ZonePolygons? _polygons;
    CartelMap? _cartel;

    public MapAreas(Drycastle.MapLoader.MapLoader? map, Func<ChurchShell?> church, Random rng) { _map = map; _church = church; _rng = rng; }

    /// <summary>The bunker rooms of the map and the church's. Empty without a map or a bunkers.json.</summary>
    public Shelters Shelters
    {
        get
        {
            if (_shelters != null) return _shelters;
            _shelters = Shelters.FromJsonOrEmpty(_map?.BunkersJson, m => GD.PrintErr($"server: {m}; bunkers give no shelter from blasts and no ladders"))
                .WithLadders(ObjectLadders.Of(_map)) // the ladders of the placed objects (hunter outposts, #372)
                .WithChurch(_church()); // the nave and the chancel shelter like a bunker room (ADR 0019)
            if (_shelters.Rooms.Count > 0) GD.Print($"server: {_shelters.Rooms.Count} bunker and church rooms shelter from blasts");
            return _shelters;
        }
    }

    /// <summary>The ladders of the map, given to every body the server simulates so it climbs as the client predicts.</summary>
    public Ladders Ladders => Shelters.Ladders;

    /// <summary>The baked points of the map. Empty without a map or the file; a broken, old or newer file counts as none.</summary>
    public ZonePoints ZonePoints
    {
        get
        {
            if (_points != null) return _points;
            _points = ZonePoints.FromJsonOrEmpty(_map?.ZonePointsJson, m => GD.PrintErr($"server: {m}; there are no zone points"));
            if (_points != ZonePoints.Empty) GD.Print("server: zone points loaded");
            return _points;
        }
    }

    /// <summary>The map's zone polygons. Empty without a map or the file; a broken or other-version file counts as none.</summary>
    public ZonePolygons ZonePolygons
    {
        get
        {
            if (_polygons != null) return _polygons;
            _polygons = ZonePolygons.FromJsonOrEmpty(_map?.ZonePolygonsJson, m => GD.PrintErr($"server: {m}; there are no zone polygons"));
            if (_polygons.Has(PlantationRules.Kind)) GD.Print($"server: plantation: {_polygons.Rings(PlantationRules.Kind).Count} outline(s)");
            return _polygons;
        }
    }

    /// <summary>
    /// The cartel's map data (docs/cartel.md): the Contact (null on a map without his marker: then there is no cartel) and the Drop point and Hit spot
    /// candidate points. Read once; the Job picks from it with the server's random generator.
    /// </summary>
    public CartelMap Cartel
    {
        get
        {
            if (_cartel != null) return _cartel;
            _cartel = Drycastle.Shooter.Cartel.ContactMarker.Of(_map, ZonePoints, ZonePolygons);
            GD.Print(_cartel.Contact is { } c
                ? $"server: cartel: Contact at ({c.Position.X:F1}, {c.Position.Z:F1}), {_cartel.DropPoints.Count} drop points, {_cartel.HitSpots.Count} hit spots, {_cartel.Helis.Count} landing spots, {_cartel.Boats.Count} moorings, {(_cartel.Pallet == null ? "no pallet" : "a pallet")}"
                : "server: cartel: no Contact on this map, no cartel");
            return _cartel;
        }
    }

    /// <summary>
    /// A random point where an event may spawn: a candidate point baked inside the zones of the kind named after the event (<c>"bigfoot"</c>), drawn with the server's random generator.
    /// In Godot ground axes (x east, y the terrain height there, z = -north); the height is the build's, so a caller that needs the ground under a moving body snaps to the live terrain.
    /// Null when there is no such point (no zone of the kind in this map's build): the caller refuses to start the event.
    /// </summary>
    public NVec3? PickEventSpawn(string eventName) => ZonePoints.Pick(eventName, _rng);
}

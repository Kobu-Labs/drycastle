using Godot;
using Drycastle.ShooterCore.Scenery;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Server side of the zones (ADR 0022): the candidate points the pipeline baked inside every ground area and drawn zone of each
/// zone kind (zone_points.json). Users: the Bigfoot event (#264), which calls <see cref="PickEventSpawn"/>, and the wildlife, which
/// takes the points of each species' kind. The areas and zones themselves are not known to the game.
/// </summary>
public partial class NetServer
{
    ZonePoints? _zonePoints;

    /// <summary>The baked points of the map. Empty without a map or the file; a broken, old or newer file is logged and counts as none.</summary>
    ZonePoints MapZonePoints
    {
        get
        {
            if (_zonePoints != null) return _zonePoints;
            _zonePoints = ZonePoints.FromJsonOrEmpty(Map?.ZonePointsJson, m => GD.PrintErr($"server: {m}; there are no zone points"));
            if (_zonePoints != ZonePoints.Empty) GD.Print("server: zone points loaded");
            return _zonePoints;
        }
    }

    /// <summary>
    /// A random point where an event may spawn: the candidate points baked inside the zones of the kind named after the event
    /// (<c>"bigfoot"</c>), drawn with the server's random generator. In Godot ground axes (x east, y the terrain height there,
    /// z = -north); the height is the build's, so a caller that needs the ground under a moving body snaps to the live terrain.
    /// Returns <c>null</c> when there is no such point (no zone of the kind in this map's build, or the map has none): the caller
    /// must handle it, for example by refusing to start the event.
    /// </summary>
    public NVec3? PickEventSpawn(string eventName) => MapZonePoints.Pick(eventName, _rng);
}

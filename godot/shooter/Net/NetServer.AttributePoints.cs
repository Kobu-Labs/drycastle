using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Server side of the runtime attributes (#263): the candidate points the pipeline baked inside every area that carries a
/// ground attribute the server needs while it runs (attribute_points.json). The first user is the Bigfoot event (#264), which
/// calls <see cref="PickEventSpawn"/>. The areas themselves are not known to the game.
/// </summary>
public partial class NetServer
{
    AttributePoints? _attributePoints;

    /// <summary>The baked points of the map. Empty without a map or the file; a broken file is logged and counts as none.</summary>
    AttributePoints MapAttributePoints
    {
        get
        {
            if (_attributePoints != null) return _attributePoints;
            _attributePoints = AttributePoints.FromJsonOrEmpty(Map?.AttributePointsJson, m => GD.PrintErr($"server: {m}; there are no attribute points"));
            if (_attributePoints != AttributePoints.Empty) GD.Print("server: attribute points loaded");
            return _attributePoints;
        }
    }

    /// <summary>
    /// A random point where an event may spawn: the candidate points baked inside the areas that carry <c>event spawn:
    /// &lt;eventName&gt;</c> (<c>"bigfoot"</c>), drawn with the server's random generator. In Godot ground axes (x east, y the
    /// terrain height there, z = -north); the height is the build's, so a caller that needs the ground under a moving body
    /// snaps to the live terrain. Returns <c>null</c> when there is no such point (no area carries the attribute in this
    /// map's build, or the map has none): the caller must handle it, for example by refusing to start the event.
    /// </summary>
    public NVec3? PickEventSpawn(string eventName) => MapAttributePoints.PickEventSpawn(eventName, _rng);
}

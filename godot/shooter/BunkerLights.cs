using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The look of a bunker interior (#224, ADR 0009), client only: dark, with a dim lamp in every room. Inside a room the ambient
/// light is turned down (<see cref="DayNightCycle.Indoor"/>), so only the sun through the doorway and the slits and the lamps
/// light it. The lamps are unshadowed warm omni lights, made only for the bunker near the camera and always on, whatever the
/// hour; the street lamp pool is not touched. Nothing here is replicated: the rooms come from bunkers.json.
/// </summary>
public partial class BunkerLights : Node3D
{
    /// <summary>Bunkers further than this from the camera (m) get no lamps; they are freed again past <see cref="DropRange"/>.</summary>
    public const float LampRange = 30f;
    public const float DropRange = 40f;
    /// <summary>The lamp hangs this far under the room's roof volume.</summary>
    public const float RoofInset = 0.5f;
    /// <summary>Seconds for the daylight to fade out on stepping inside, and back in.</summary>
    public const float FadeSeconds = 0.3f;

    public Shelters Shelters { get; set; } = Shelters.Empty;

    readonly Dictionary<string, List<OmniLight3D>> _lamps = new();
    float _indoor;

    public override void _Process(double delta)
    {
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null || Shelters.Rooms.Count == 0) return;
        var eye = cam.GlobalPosition;
        var here = Shelters.RoomAt(new System.Numerics.Vector3(eye.X, eye.Y, eye.Z)) >= 0;
        _indoor = Mathf.MoveToward(_indoor, here ? 1f : 0f, (float)delta / FadeSeconds);
        if (DayNightCycle.Current is { } cycle) cycle.Indoor = _indoor;

        foreach (var id in Shelters.Rooms.Select(r => r.BunkerId).Distinct())
        {
            var rooms = Shelters.Rooms.Where(r => r.BunkerId == id).ToList();
            var c = rooms[0];
            var d = new Vector2((c.MinX + c.MaxX) / 2 - eye.X, (c.MinZ + c.MaxZ) / 2 - eye.Z).Length();
            if (d <= LampRange && !_lamps.ContainsKey(id)) _lamps[id] = rooms.Select(Hang).ToList();
            else if (d > DropRange && _lamps.Remove(id, out var gone)) foreach (var l in gone) l.QueueFree();
        }
    }

    OmniLight3D Hang(BunkerRoom r)
    {
        var x = r.Ring.Average(p => p.X);
        var z = r.Ring.Average(p => p.Y);
        var light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.78f, 0.45f), LightEnergy = 1.3f, OmniRange = 5.5f, OmniAttenuation = 1.2f,
            ShadowEnabled = false, Position = new Vector3(x, r.Y1 - RoofInset, z),
        };
        AddChild(light);
        return light;
    }
}

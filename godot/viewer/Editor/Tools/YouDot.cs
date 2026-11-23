using Godot;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The character as the editor sees it. <see cref="PlacePlayer"/> moves it (Street View does; the editor keeps the walk-mode
/// player frozen and hidden, so the move has to reach what F2 restores as well); in the top-down view (T) a red
/// point marks the spot straight below it, drawn over everything so a roof or a tree does not hide it. The editor's
/// camera follows the character: the first position takes it to the top-down view above the character, then in the
/// top-down view it stays centred on the character (at the height it has), and in any other view it moves by the
/// same step as the character, so the view of the character does not change.
/// </summary>
public sealed class YouDot : EditorPart
{
    const float RimScale = 1.35f, Lift = 0.15f, ScreenShare = 0.011f, MinScale = 0.4f, MaxScale = 8f;

    Node3D? _dot;
    bool _placed;

    public YouDot(IEditorHost host) : base(host) { }

    /// <summary>Put the character on the ground at <paramref name="feet"/> facing <paramref name="yawDeg"/> (degrees about +Y; null keeps the facing).</summary>
    public void PlacePlayer(Vector3 feet, float? yawDeg)
    {
        var player = Host.Player;
        var yaw = yawDeg ?? player.RotationDegrees.Y;
        var before = player.GlobalPosition;
        var first = !_placed;
        player.GlobalPosition = feet;
        player.RotationDegrees = new Vector3(0, yaw, 0);
        player.Velocity = Vector3.Zero;
        // while the editor is open the player is parked: F2 puts it back where it was parked, so park it here
        if (Host.Active) Host.Camera.ParkPlayer(feet, yaw);
        _placed = true;
        if (Host.Active) Host.Camera.Follow(before, feet, first);
    }

    public override void Update(double delta)
    {
        if (!_placed || !Host.Camera.TopDown)
        {
            if (_dot != null) _dot.Visible = false;
            return;
        }
        if (_dot == null) BuildDot();
        var p = Host.Player.GlobalPosition;
        var at = new Vector3(p.X, Host.Map.GroundHeightOrMean(p.X, p.Z) + Lift, p.Z);
        // a constant share of the screen, whatever the height of the camera
        var r = Mathf.Clamp(Host.FlyCam.GlobalPosition.DistanceTo(at) * ScreenShare, MinScale, MaxScale);
        _dot!.GlobalPosition = at;
        _dot.Scale = new Vector3(r, 1f, r);
        _dot.Visible = true;
    }

    void BuildDot()
    {
        _dot = new Node3D { Name = "YouDot", Visible = false };
        StandardMaterial3D Mat(Color c, int priority) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = priority,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        // two flat discs: a white rim under the red point (unit radius, the node's scale sets the size)
        _dot.AddChild(new MeshInstance3D { Name = "Rim", Mesh = new CylinderMesh { TopRadius = RimScale, BottomRadius = RimScale, Height = 0.02f, RadialSegments = 32, Rings = 1 }, MaterialOverride = Mat(Colors.White, 11) });
        _dot.AddChild(new MeshInstance3D { Name = "Point", Mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.02f, RadialSegments = 32, Rings = 1 }, MaterialOverride = Mat(EditorTheme.You, 12) });
        Host.MarkerRoot.AddChild(_dot);
    }
}

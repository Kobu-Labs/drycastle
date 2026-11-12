using Godot;

/// <summary>
/// The character as the editor sees it. <see cref="StreetViewFollow"/> moves it (the editor keeps the walk-mode
/// player frozen and hidden, so the move has to reach what F2 restores as well); in the top-down view (T) a red
/// point marks the spot straight below it, drawn over everything so a roof or a tree does not hide it.
/// </summary>
public partial class EditorMode
{
    const float YouDotRimScale = 1.35f, YouDotLift = 0.15f, YouDotScreenShare = 0.011f, YouDotMin = 0.4f, YouDotMax = 8f;

    Node3D? _youDot;
    bool _youPlaced;

    /// <summary>Put the character on the ground at <paramref name="feet"/> facing <paramref name="yawDeg"/> (degrees about +Y; null keeps the facing).</summary>
    public void PlacePlayer(Vector3 feet, float? yawDeg)
    {
        var yaw = yawDeg ?? Player.RotationDegrees.Y;
        Player.GlobalPosition = feet;
        Player.RotationDegrees = new Vector3(0, yaw, 0);
        Player.Velocity = Vector3.Zero;
        // while the editor is open the player is parked: F2 puts it back where it was parked, so park it here
        if (Active) _savedPlayer = (feet, new Vector3(0, yaw, 0), 0f, _savedPlayer?.input ?? true);
        _youPlaced = true;
    }

    void UpdateYouDot()
    {
        if (!_youPlaced || !TopDown)
        {
            if (_youDot != null) _youDot.Visible = false;
            return;
        }
        if (_youDot == null) BuildYouDot();
        var p = Player.GlobalPosition;
        var at = new Vector3(p.X, Map.GroundHeightOrMean(p.X, p.Z) + YouDotLift, p.Z);
        // a constant share of the screen, whatever the height of the camera
        var r = Mathf.Clamp(FlyCam.GlobalPosition.DistanceTo(at) * YouDotScreenShare, YouDotMin, YouDotMax);
        _youDot!.GlobalPosition = at;
        _youDot.Scale = new Vector3(r, 1f, r);
        _youDot.Visible = true;
    }

    void BuildYouDot()
    {
        _youDot = new Node3D { Name = "YouDot", Visible = false };
        StandardMaterial3D Mat(Color c, int priority) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = priority,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        // two flat discs: a white rim under the red point (unit radius, the node's scale sets the size)
        _youDot.AddChild(new MeshInstance3D { Name = "Rim", Mesh = new CylinderMesh { TopRadius = YouDotRimScale, BottomRadius = YouDotRimScale, Height = 0.02f, RadialSegments = 32, Rings = 1 }, MaterialOverride = Mat(Colors.White, 11) });
        _youDot.AddChild(new MeshInstance3D { Name = "Point", Mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.02f, RadialSegments = 32, Rings = 1 }, MaterialOverride = Mat(EditorTheme.You, 12) });
        MarkerRoot.AddChild(_youDot);
    }
}

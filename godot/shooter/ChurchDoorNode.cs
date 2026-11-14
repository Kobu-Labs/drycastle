using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// What the player sees of the church from outside (client): the interior is drawn only while it can be seen, that is when the doors are open or
/// the camera is inside the church. <see cref="ChurchDoorNode"/> sets it each frame; <see cref="ChurchProps"/> follows it, and so should the
/// views of anything that stands inside (citizens, clergy). It is true on a map without a church.
/// </summary>
public static class ChurchInterior
{
    public static bool Visible { get; private set; } = true;

    /// <summary>Raised when <see cref="Visible"/> changed.</summary>
    public static event Action<bool>? Changed;

    internal static void Set(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        Changed?.Invoke(visible);
    }
}

/// <summary>
/// The double doors of the church (ADR 0019): a box on the World layer in the doorway while closed (solid for players, bullets and citizens; on the
/// server too, so it is made without drawing there) and two dark wooden leaves, code-built. Open: the box is off and the leaves are swung inward by
/// <see cref="OpenAngleDeg"/>, with a short swing on the clients. The server owns the state (<c>ChurchDoors</c> in NetServer); a client sets it from
/// <see cref="EventKind.ChurchDoor"/>. Closed by default. One per map: <see cref="Current"/>.
/// </summary>
public partial class ChurchDoorNode : Node3D
{
    const float OpenAngleDeg = 100f, SwingSeconds = 0.8f, LeafThickness = 0.1f;

    public static ChurchDoorNode? Current { get; private set; }

    public ChurchShell Shell { get; private init; } = null!;
    public bool IsOpen { get; private set; }

    bool _draw;
    StaticBody3D _body = null!;
    Node3D? _left, _right; // pivots at the hinges
    float _angle; // 0 closed .. 1 fully open

    /// <summary>The doors of the church.json in <paramref name="mapDir"/>, or null when the map has no church (or an older one without a doorway).</summary>
    public static ChurchDoorNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "church.json");
        if (!File.Exists(path)) return null;
        var shell = ChurchShell.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"{m}; the church has no doors"));
        if (shell == null) return null;
        var node = new ChurchDoorNode
        {
            Name = "ChurchDoors", Shell = shell, _draw = draw,
            Position = new Vector3(shell.DoorwayPos.X, shell.DoorwayPos.Y, shell.DoorwayPos.Z),
            Rotation = new Vector3(0, shell.DoorwayYaw, 0), // local -Z points out of the church, +X to the right of someone facing in
        };
        node.Build();
        return node;
    }

    void Build()
    {
        var w = Shell.Width; var h = Shell.Height;
        _body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(0, h / 2, 0) };
        _body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(w, h, ChurchShell.DoorDepth) } });
        AddChild(_body);
        if (!_draw) return;
        var wood = new StandardMaterial3D { AlbedoColor = new Color("3b2414"), Roughness = 0.9f };
        var seam = new StandardMaterial3D { AlbedoColor = new Color("24150b"), Roughness = 1f };
        var iron = new StandardMaterial3D { AlbedoColor = new Color("1d1d20"), Roughness = 0.6f, Metallic = 0.6f };
        // the right-hand leaf hinges at +X and reaches to -X, the left-hand one the other way round
        _right = Leaf(+1, w / 2, h, wood, seam, iron);
        _left = Leaf(-1, w / 2, h, wood, seam, iron);
    }

    /// <summary>One leaf on a hinge at <paramref name="side"/> * <paramref name="half"/>: the pivot, with a panel, plank seams and two iron straps.</summary>
    Node3D Leaf(int side, float half, float h, Material wood, Material seam, Material iron)
    {
        var pivot = new Node3D { Position = new Vector3(side * half, 0, 0) };
        var len = half - 0.01f; // a hair of play against the jamb and the other leaf
        var toward = -side; // from the hinge to the middle of the doorway
        void Box(Vector3 size, Vector3 centre, Material m) =>
            pivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = centre });
        Box(new Vector3(len, h - 0.02f, LeafThickness), new Vector3(toward * len / 2, h / 2, 0), wood);
        for (var k = 1; k <= 3; k++) // plank seams
            Box(new Vector3(0.015f, h - 0.1f, LeafThickness + 0.012f), new Vector3(toward * len * k / 4f, h / 2, 0), seam);
        foreach (var y in new[] { 0.45f, h - 0.5f }) // straps from the hinge across the leaf
            Box(new Vector3(len * 0.92f, 0.12f, LeafThickness + 0.03f), new Vector3(toward * len * 0.46f, y, 0), iron);
        AddChild(pivot);
        return pivot;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        if (_draw) ChurchInterior.Set(true);
    }

    /// <summary>Opens or closes the doors. Closed: the box is solid. <paramref name="animate"/>: the leaves swing; else they jump (the server, a joiner).</summary>
    public void SetOpen(bool open, bool animate)
    {
        IsOpen = open;
        _body.CollisionLayer = open ? 0 : CollisionLayers.World;
        if (!animate) { _angle = open ? 1 : 0; Pose(); }
    }

    public override void _Process(double delta)
    {
        if (!_draw) return;
        var target = IsOpen ? 1f : 0f;
        if (_angle != target)
        {
            _angle = Mathf.MoveToward(_angle, target, (float)delta / SwingSeconds);
            Pose();
        }
        // the interior is drawn when the doors are open or the camera is inside the church
        var cam = GetViewport()?.GetCamera3D();
        var inside = cam != null && Shell.InRooms(new System.Numerics.Vector3(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z));
        ChurchInterior.Set(IsOpen || _angle > 0 || inside);
    }

    /// <summary>Swung inward: the right leaf turns towards +Z (inside), the left one the other way. Eased so it starts and ends gently.</summary>
    void Pose()
    {
        if (_left == null || _right == null) return;
        var a = Mathf.DegToRad(OpenAngleDeg) * Mathf.SmoothStep(0, 1, _angle);
        _right.Rotation = new Vector3(0, a, 0);
        _left.Rotation = new Vector3(0, -a, 0);
    }
}

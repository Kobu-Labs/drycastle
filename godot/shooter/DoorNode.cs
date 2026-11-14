using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Double doors in a <see cref="Doorway"/> (the church's, ADR 0019, and the Obecny Urad's): a box on the World layer in the doorway while closed (solid for players, bullets and citizens; on the
/// server too, so it is made without drawing there) and two dark wooden leaves, code-built. Open: the box is off and the leaves are swung inward by <see cref="OpenAngleDeg"/>, with a short swing on
/// the clients. The server owns the state; a client sets it from the door's event. Closed by default. A subclass is one building's door (its <c>Current</c>, its event).
/// </summary>
public abstract partial class DoorNode : Node3D
{
    const float OpenAngleDeg = 100f, SwingSeconds = 0.8f, LeafThickness = 0.1f;

    public Doorway Gate { get; private set; } = null!;
    public bool IsOpen { get; private set; }

    bool _draw;
    StaticBody3D _body = null!;
    Node3D? _left, _right; // pivots at the hinges
    protected float Angle { get; private set; } // 0 closed .. 1 fully open
    protected bool Draws => _draw;

    /// <summary>Sets the node up in <paramref name="gate"/> (the doorway's position and yaw: local -Z points out of the building, +X to the right of someone facing in) and builds the box and the leaves.</summary>
    protected void Init(string name, Doorway gate, bool draw)
    {
        Name = name; Gate = gate; _draw = draw;
        Position = new Vector3(gate.Pos.X, gate.Pos.Y, gate.Pos.Z);
        Rotation = new Vector3(0, gate.Yaw, 0);
        Build();
    }

    void Build()
    {
        var w = Gate.Width; var h = Gate.Height;
        _body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(0, h / 2, 0) };
        _body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(w, h, Doorway.Depth) } });
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

    /// <summary>Opens or closes the doors. Closed: the box is solid. <paramref name="animate"/>: the leaves swing; else they jump (the server, a joiner).</summary>
    public void SetOpen(bool open, bool animate)
    {
        IsOpen = open;
        _body.CollisionLayer = open ? 0 : CollisionLayers.World;
        if (!animate) { Angle = open ? 1 : 0; Pose(); }
    }

    public override void _Process(double delta)
    {
        if (!_draw) return;
        var target = IsOpen ? 1f : 0f;
        if (Angle != target)
        {
            Angle = Mathf.MoveToward(Angle, target, (float)delta / SwingSeconds);
            Pose();
        }
    }

    /// <summary>Swung inward: the right leaf turns towards +Z (inside), the left one the other way. Eased so it starts and ends gently.</summary>
    void Pose()
    {
        if (_left == null || _right == null) return;
        var a = Mathf.DegToRad(OpenAngleDeg) * Mathf.SmoothStep(0, 1, Angle);
        _right.Rotation = new Vector3(0, a, 0);
        _left.Rotation = new Vector3(0, -a, 0);
    }
}

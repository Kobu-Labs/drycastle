using Godot;
using Drycastle.ShooterCore.Buildings;

namespace Drycastle.Shooter.Buildings;

/// <summary>
/// One door of a hollow house (<see cref="DoorNode"/>: the box, the state): a front door is one leaf that swings into the house, a garage door one ribbed panel that tips up
/// under the ceiling. Made and owned by <see cref="HouseDoorsNode"/>.
/// </summary>
public partial class HouseDoorNode : DoorNode
{
    const float Leaf = 0.06f;

    HouseDoor _door = null!;
    Node3D? _pivot; // the hinge: upright at the jamb, or level along the head

    protected override DoorStyle Style => _door.Kind == HouseDoorKind.Garage ? new(OpenAngleDeg: 90f, SwingSeconds: 1.4f) : new(OpenAngleDeg: 95f, SwingSeconds: 0.5f);

    public static HouseDoorNode Create(int index, HouseDoor door, bool draw)
    {
        var node = new HouseDoorNode { _door = door };
        node.Init($"HouseDoor{index}", door.Doorway, draw);
        return node;
    }

    protected override void BuildLeaves(float w, float h)
    {
        var colour = new Color(_door.Colour);
        var panel = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.85f };
        var trim = new StandardMaterial3D { AlbedoColor = colour.Darkened(0.3f), Roughness = 0.85f };
        var garage = _door.Kind == HouseDoorKind.Garage;
        var pivot = new Node3D { Position = garage ? new Vector3(0, h, 0) : new Vector3(w / 2, 0, 0) };
        void Box(Vector3 size, Vector3 centre, Material m) => pivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Position = centre });
        if (garage)
        {
            Box(new Vector3(w - 0.02f, h - 0.02f, Leaf), new Vector3(0, -h / 2, 0), panel);
            for (var k = 1; k < 4; k++) // the joints between its sections
                Box(new Vector3(w - 0.06f, 0.025f, Leaf + 0.012f), new Vector3(0, -h * k / 4, 0), trim);
            Box(new Vector3(0.18f, 0.04f, Leaf + 0.08f), new Vector3(0, -h + 0.45f, 0), trim); // the handle, both faces
        }
        else
        {
            var len = w - 0.02f;
            Box(new Vector3(len, h - 0.02f, Leaf), new Vector3(-len / 2, h / 2, 0), panel);
            Box(new Vector3(len * 0.7f, h * 0.35f, Leaf + 0.012f), new Vector3(-len / 2, h * 0.7f, 0), trim);
            Box(new Vector3(len * 0.7f, h * 0.3f, Leaf + 0.012f), new Vector3(-len / 2, h * 0.27f, 0), trim);
            Box(new Vector3(0.12f, 0.04f, Leaf + 0.1f), new Vector3(-len + 0.14f, 1.05f, 0), trim); // the handle, both faces
        }
        AddChild(pivot);
        _pivot = pivot;
    }

    protected override void Pose()
    {
        if (_pivot == null) return;
        var a = Mathf.DegToRad(Style.OpenAngleDeg) * Mathf.SmoothStep(0, 1, Angle);
        // about the head, the panel's foot goes in and up; about the jamb, the free edge goes in
        _pivot.Rotation = _door.Kind == HouseDoorKind.Garage ? new Vector3(-a, 0, 0) : new Vector3(0, a, 0);
    }
}

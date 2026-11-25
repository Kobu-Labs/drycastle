using Godot;
using Drycastle.ShooterCore.Buildings;

namespace Drycastle.Shooter.Buildings;

/// <summary>
/// The doors of the hollow houses of the map (interiors.json): one <see cref="HouseDoorNode"/> each, in the file's order, shut. The server owns which are open
/// (<c>HouseDoorServer</c>, a player's interact key) and a client sets them from <c>EventKind.HouseDoor</c>. One per map: <see cref="Current"/>.
/// </summary>
public partial class HouseDoorsNode : Node3D
{
    public static HouseDoorsNode? Current { get; private set; }

    /// <summary>The doors as data, with this process's own idea of which are open (the server's is the truth; a client's follows the events).</summary>
    public HouseDoors Doors { get; private set; } = null!;

    readonly List<HouseDoorNode> _nodes = new();

    /// <summary>The doors of the interiors.json in <paramref name="mapDir"/>, or null when the map has none (or a broken file, which is logged).</summary>
    public static HouseDoorsNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "interiors.json");
        if (!File.Exists(path)) return null;
        var doors = HouseDoors.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"{m}; the houses have no doors"));
        if (doors == null || doors.All.Count == 0) return null;
        var node = new HouseDoorsNode { Name = "HouseDoors", Doors = doors };
        for (var i = 0; i < doors.All.Count; i++)
        {
            var door = HouseDoorNode.Create(i, doors.All[i], draw);
            node._nodes.Add(door);
            node.AddChild(door);
        }
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    /// <summary>Whether door <paramref name="door"/> stands open here.</summary>
    public bool IsOpen(int door) => _nodes[door].IsOpen;

    /// <summary>Opens or closes door <paramref name="door"/>'s box and leaf; an index the map has no door for is ignored.</summary>
    public void Show(int door, bool open, bool animate)
    {
        if (door >= 0 && door < _nodes.Count) _nodes[door].SetOpen(open, animate);
    }
}

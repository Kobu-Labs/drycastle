using Godot;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Buildings;

/// <summary>
/// The double doors of the bus garage (<see cref="DoorNode"/>): brown sheet-metal leaves in the <c>doorway</c> of the garage.json next to the map, swinging out into the yard
/// (the bus stands right behind them). Shut by default; the server owns the state (<c>GarageServer.Use</c>, a player's interact key) and a client sets it from
/// <see cref="EventKind.GarageDoor"/>. One per map: <see cref="Current"/>.
/// </summary>
public partial class GarageDoorNode : DoorNode
{
    public static GarageDoorNode? Current { get; private set; }

    public Garage Garage { get; private set; } = null!;

    protected override DoorStyle Style => new(SwingsOut: true, OpenAngleDeg: 105f, SwingSeconds: 1.6f, Panel: "5c3e32", Seam: "45302a", Iron: "2a2422", Seams: 5, Straps: false, Metallic: 0.3f);

    /// <summary>The doors of the garage.json in <paramref name="mapDir"/>, or null when the map has none (or a broken one, which is logged).</summary>
    public static GarageDoorNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "garage.json");
        if (!File.Exists(path)) return null;
        var garage = Garage.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"{m}; the garage has no doors"));
        if (garage == null) return null;
        var node = new GarageDoorNode { Garage = garage };
        node.Init("GarageDoors", garage.Doorway, draw);
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }
}

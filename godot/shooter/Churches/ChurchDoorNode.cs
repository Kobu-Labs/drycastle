using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Churches;

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
/// The double doors of the church (ADR 0019; the doors themselves are <see cref="DoorNode"/>): the server's state is <c>ChurchDoors</c> in NetServer, a client sets it from
/// <see cref="EventKind.ChurchDoor"/>. The client also reads whether the interior may be seen off them (<see cref="ChurchInterior"/>). One per map: <see cref="Current"/>.
/// </summary>
public partial class ChurchDoorNode : DoorNode
{
    public static ChurchDoorNode? Current { get; private set; }

    public ChurchShell Shell { get; private init; } = null!;

    /// <summary>The doors of the church.json in <paramref name="mapDir"/>, or null when the map has no church (or an older one without a doorway).</summary>
    public static ChurchDoorNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "church.json");
        if (!File.Exists(path)) return null;
        var shell = ChurchShell.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"{m}; the church has no doors"));
        if (shell == null) return null;
        var node = new ChurchDoorNode { Shell = shell };
        node.Init("ChurchDoors", shell.Gate, draw);
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        if (Draws) ChurchInterior.Set(true);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (!Draws) return;
        // the interior is drawn when the doors are open or the camera is inside the church
        var cam = GetViewport()?.GetCamera3D();
        var inside = cam != null && Shell.InRooms(new System.Numerics.Vector3(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z));
        ChurchInterior.Set(IsOpen || Angle > 0 || inside);
    }
}

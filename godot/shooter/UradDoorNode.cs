using Godot;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter;

/// <summary>
/// The door of the Obecny Urad (<see cref="DoorNode"/>, the church door's look and behaviour): in the <c>doorway</c> of the urad.json next to the map. Closed while no detour is open; the server
/// owns the state (<c>NetServer.RozhlasDoor</c>) and a client sets it from <see cref="EventKind.UradDoor"/>. One per map: <see cref="Current"/>.
/// </summary>
public partial class UradDoorNode : DoorNode
{
    public static UradDoorNode? Current { get; private set; }

    /// <summary>The door of the urad.json in <paramref name="mapDir"/>, or null when the map has none (or a broken one, which is logged).</summary>
    public static UradDoorNode? Create(string mapDir, bool draw)
    {
        var path = Path.Combine(mapDir, "urad.json");
        if (!File.Exists(path)) return null;
        var urad = Urad.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"{m}; the Urad has no door"));
        if (urad == null) return null;
        var node = new UradDoorNode();
        node.Init("UradDoor", urad.Doorway, draw);
        if (draw) node._glassPrefix = urad.GlassPrefix;
        return node;
    }

    // The panes of the hollow part come from the map as opaque coloured meshes, like every house window; these stand in real openings, so they get the church's
    // clear material (ChurchDecor.ClearGlass does the same). The chunk may load after this node: looked for once a second until found, and a few times after.
    static readonly StandardMaterial3D ClearPane = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.62f, 0.78f, 0.88f, 0.22f),
        Roughness = 0.08f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    string? _glassPrefix;
    float _glassIn;
    int _glassDone;

    public override void _Process(double delta)
    {
        if (_glassPrefix == null || _glassDone >= 3) { SetProcess(false); return; }
        if ((_glassIn -= (float)delta) > 0) return;
        _glassIn = 1f;
        var found = 0;
        foreach (var n in GetTree().Root.FindChildren(_glassPrefix + "*", "MeshInstance3D", true, false))
            if (n is MeshInstance3D m) { m.MaterialOverride = ClearPane; found++; }
        if (found > 0) _glassDone++;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }
}

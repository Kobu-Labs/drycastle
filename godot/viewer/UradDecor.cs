using Godot;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Drycastle.Viewer;

/// <summary>
/// The inside of the Obecny Urad as the game dresses it (ADR 0020), so the viewer and the editor show what a player sees: the <c>props</c> of the map's urad.json as
/// Synty models (the private SIMPLE Office pack under assets-private/synty, <c>atlas</c> names its texture) and the panes named by <c>glass_prefix</c> drawn clear.
/// Looks only: no collision (the game makes the boxes). A map without urad.json, or a machine without the pack, shows the bare shell. The entrance door is the game's
/// (it follows the rozhlas) and is not drawn here: the doorway stands open.
/// </summary>
public partial class UradDecor : Node3D
{
    static readonly StandardMaterial3D ClearPane = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.62f, 0.78f, 0.88f, 0.22f),
        Roughness = 0.08f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    string? _glassPrefix;
    float _glassIn;

    /// <summary>The decor of the urad.json in <paramref name="mapDir"/>, or null when the map has none.</summary>
    public static UradDecor? Create(string mapDir)
    {
        var path = Path.Combine(mapDir, "urad.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var node = new UradDecor { Name = "UradDecor", _glassPrefix = root.TryGetProperty("glass_prefix", out var g) ? g.GetString() : null };
        if (!root.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array) return node;
        var synty = Path.Combine(System.Environment.GetEnvironmentVariable("DRYCASTLE_ASSETS_PRIVATE") is { Length: > 0 } env
            ? env : Path.GetFullPath(ProjectSettings.GlobalizePath("res://") + "../../assets-private"), "synty");
        var atlasPath = Path.Combine(synty, root.TryGetProperty("atlas", out var a) ? a.GetString() ?? "" : "");
        if (!File.Exists(atlasPath)) { GD.Print($"urad: no office pack at {synty}: the Urad's furniture is not shown"); return node; }
        var image = Image.LoadFromFile(atlasPath);
        image.GenerateMipmaps();
        var mat = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(image), Roughness = 0.8f };
        var models = new Dictionary<string, Node3D?>();
        var missing = 0;
        foreach (var p in props.EnumerateArray())
        {
            var model = p.GetProperty("model").GetString() ?? "";
            if (!models.TryGetValue(model, out var template)) models[model] = template = Load(Path.Combine(synty, "glb", model + ".glb"), mat);
            if (template == null) { missing++; continue; }
            var pos = p.GetProperty("pos");
            var copy = (Node3D)template.Duplicate();
            copy.Position = new Vector3(pos[0].GetSingle(), pos[2].GetSingle(), -pos[1].GetSingle());
            copy.Rotation = new Vector3(0, Mathf.Pi - Mathf.DegToRad(p.GetProperty("yaw").GetSingle()), 0); // the models face +Z
            node.AddChild(copy);
        }
        foreach (var t in models.Values) t?.Free();
        GD.Print($"urad: {node.GetChildCount()} props shown" + (missing > 0 ? $", {missing} models missing" : ""));
        return node;
    }

    static Node3D? Load(string path, Material mat)
    {
        if (!File.Exists(path)) return null;
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok || doc.GenerateScene(state) is not Node3D root) return null;
        foreach (var imp in root.FindChildren("*", "ImporterMeshInstance3D", true, false))
        {
            // a scene generated at runtime may keep importer nodes: make them drawable
            var i = (ImporterMeshInstance3D)imp;
            var mi = new MeshInstance3D { Name = i.Name, Mesh = i.Mesh.GetMesh(), Transform = i.Transform };
            i.ReplaceBy(mi);
            i.Free();
        }
        foreach (var m in root.FindChildren("*", "MeshInstance3D", true, false)) ((MeshInstance3D)m).MaterialOverride = mat;
        return root;
    }

    // The panes come from the map as opaque coloured meshes; these stand in real openings. Chunks load late and are replaced when the map is re-exported while the
    // editor runs, so the panes are looked for once a second for as long as the node lives (four nodes by name: cheap).
    public override void _Process(double delta)
    {
        if (_glassPrefix == null) { SetProcess(false); return; }
        if ((_glassIn -= (float)delta) > 0) return;
        _glassIn = 1f;
        foreach (var n in GetTree().Root.FindChildren(_glassPrefix + "*", "MeshInstance3D", true, false))
            if (n is MeshInstance3D m && m.MaterialOverride != ClearPane) m.MaterialOverride = ClearPane;
    }
}

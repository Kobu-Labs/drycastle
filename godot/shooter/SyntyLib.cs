using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Models of the paid Synty packs (assets-private/synty/glb, see <see cref="GamePaths.PrivateAssets"/>). A GLB there carries geometry and UVs only;
/// the pack's shared atlas (glb/atlas/*.png) is put on it here as albedo. Loaded once, copies handed out. Null when headless or the file is missing.
/// </summary>
public static class SyntyLib
{
    static readonly Dictionary<string, Node3D?> Models = new();
    static readonly Dictionary<string, StandardMaterial3D?> Atlases = new();

    static string Dir => Path.Combine(GamePaths.PrivateAssets(), "synty", "glb");

    /// <summary>A copy of <c>glb/{model}.glb</c> wearing <c>glb/atlas/{atlas}.png</c>.</summary>
    public static Node3D? Instance(string model, string atlas)
    {
        if (!ShotFx.Enabled) return null;
        var key = model + "|" + atlas;
        if (!Models.TryGetValue(key, out var t)) Models[key] = t = Load(model, atlas);
        return t?.Duplicate() as Node3D;
    }

    static Node3D? Load(string model, string atlas)
    {
        var path = Path.Combine(Dir, model + ".glb");
        if (!File.Exists(path)) { GD.PrintErr($"synty model not found: {path}"); return null; }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok || doc.GenerateScene(state) is not Node3D root) { GD.PrintErr($"synty model failed to load: {path}"); return null; }
        var mat = Atlas(atlas);
        foreach (var n in root.FindChildren("*", "", true, false).Prepend(root))
        {
            if (n is ImporterMeshInstance3D imp) // a scene generated at runtime may keep importer nodes: make them drawable
            {
                var mi = new MeshInstance3D { Name = imp.Name, Mesh = imp.Mesh.GetMesh(), Transform = imp.Transform, Skin = imp.Skin, Skeleton = imp.SkeletonPath, MaterialOverride = mat };
                imp.ReplaceBy(mi);
                imp.Free();
            }
            else if (n is MeshInstance3D m) m.MaterialOverride = mat;
        }
        return root;
    }

    static StandardMaterial3D? Atlas(string name)
    {
        if (Atlases.TryGetValue(name, out var m)) return m;
        var path = Path.Combine(Dir, "atlas", name + ".png");
        var img = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (img == null) { GD.PrintErr($"synty atlas not found: {path}"); return Atlases[name] = null; }
        img.GenerateMipmaps();
        return Atlases[name] = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img) };
    }
}

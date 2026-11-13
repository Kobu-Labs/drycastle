using System.Text.Json;
using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Models and animation clips of the paid Synty packs (assets-private/synty, see <see cref="GamePaths.PrivateAssets"/>). A model GLB (glb/) carries
/// geometry, skin and UVs only; a pack's atlas texture is put on it here as albedo. A clip (anim/baked/, listed in anim/manifest.json) is baked for
/// the shared character skeleton, with tracks <c>Skeleton3D:Bone</c> relative to the model's root. Loaded once. Null when headless or a file is missing.
/// </summary>
public static class SyntyLib
{
    static readonly Dictionary<string, Node3D?> Models = new();
    static readonly Dictionary<string, StandardMaterial3D?> Atlases = new();
    static readonly Dictionary<string, Animation?> Clips = new();
    static HashSet<string>? _loops;

    static string Dir => Path.Combine(GamePaths.PrivateAssets(), "synty");

    /// <summary>A copy of <c>glb/{model}.glb</c> wearing the texture <paramref name="atlas"/> (a path under synty/).</summary>
    public static Node3D? Instance(string model, string atlas)
    {
        if (!ShotFx.Enabled) return null;
        if (!Models.TryGetValue(model, out var t)) Models[model] = t = Load(model);
        if (t?.Duplicate() is not Node3D copy) return null;
        var mat = Atlas(atlas);
        foreach (var m in copy.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) m.MaterialOverride = mat;
        return copy;
    }

    /// <summary>The clip <c>anim/baked/{name}.glb</c>; it loops when the manifest says so.</summary>
    public static Animation? Clip(string name)
    {
        if (Clips.TryGetValue(name, out var a)) return a;
        var path = Path.Combine(Dir, "anim", "baked", name + ".glb");
        if (Scene(path) is not { } scene) return Clips[name] = null;
        var player = scene.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
        a = player?.GetAnimationList().Select(n => player.GetAnimation(n)).FirstOrDefault()?.Duplicate() as Animation;
        if (a == null) GD.PrintErr($"synty clip has no animation: {path}");
        else a.LoopMode = Loops().Contains(name) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        scene.Free();
        return Clips[name] = a;
    }

    static HashSet<string> Loops()
    {
        if (_loops != null) return _loops;
        _loops = new HashSet<string>();
        var path = Path.Combine(Dir, "anim", "manifest.json");
        if (!File.Exists(path)) return _loops;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var clips = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("clips");
        foreach (var c in clips.EnumerateArray())
            if (c.TryGetProperty("loop", out var l) && l.ValueKind == JsonValueKind.True) _loops.Add(c.GetProperty("name").GetString() ?? "");
        return _loops;
    }

    static Node3D? Scene(string path)
    {
        if (!File.Exists(path)) { GD.PrintErr($"synty file not found: {path}"); return null; }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok || doc.GenerateScene(state) is not Node3D root) { GD.PrintErr($"synty file failed to load: {path}"); return null; }
        return root;
    }

    static Node3D? Load(string model)
    {
        if (Scene(Path.Combine(Dir, "glb", model + ".glb")) is not { } root) return null;
        foreach (var imp in root.FindChildren("*", "ImporterMeshInstance3D", true, false).OfType<ImporterMeshInstance3D>())
        {
            // a scene generated at runtime may keep importer nodes: make them drawable
            var mi = new MeshInstance3D { Name = imp.Name, Mesh = imp.Mesh.GetMesh(), Transform = imp.Transform, Skin = imp.Skin, Skeleton = imp.SkeletonPath };
            imp.ReplaceBy(mi);
            imp.Free();
        }
        return root;
    }

    static StandardMaterial3D? Atlas(string rel)
    {
        if (Atlases.TryGetValue(rel, out var m)) return m;
        var path = Path.Combine(Dir, rel);
        var img = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (img == null) { GD.PrintErr($"synty atlas not found: {path}"); return Atlases[rel] = null; }
        if (img.GetWidth() > 1024) img.Resize(1024, 1024, Image.Interpolation.Bilinear); // flat colour swatches: a small texture is enough
        img.GenerateMipmaps();
        return Atlases[rel] = new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img), Roughness = 0.9f };
    }
}

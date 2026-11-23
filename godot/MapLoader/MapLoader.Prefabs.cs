using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

// Prefabs of the private (paid) packs: loose GLBs under assets-private/ (scripts/packs/private_pack_glb.gd), never res:// resources, so a
// build holds nothing of a pack and the files arrive in the private asset pack. A prefab GLB has the prefab's own collision bodies and
// NO textures: each surface wears an empty material that carries a name, looked up in materials.json beside the GLB. The materials
// are shared by every model of the pack, and each is built (its textures decoded and uploaded) when the first model that wears it is
// loaded: a pack has far more materials than a map uses (the swamp pack's 23 textures are about 400 MB of video memory, for four models).
public sealed partial class MapLoader
{
    /// <summary>Textures of a pack are flat colour atlases and small signs: this is plenty, and a 4096 atlas would cost 64 MB of video memory.</summary>
    const int PrefabTextureMax = 2048;

    /// <summary>The materials of one pack (materials.json), each made on first use; the textures are shared between them.</summary>
    sealed class PrefabMaterialTable
    {
        public string Dir = "";
        public readonly Dictionary<string, JsonElement> Specs = new();
        readonly Dictionary<string, StandardMaterial3D> _made = new();
        readonly Dictionary<string, Texture2D?> _textures = new();

        public bool TryGetValue(string name, out StandardMaterial3D mat)
        {
            if (_made.TryGetValue(name, out mat!)) return true;
            if (!Specs.TryGetValue(name, out var spec)) return false;
            _made[name] = mat = PrefabMaterial(spec, Dir, _textures);
            return true;
        }
    }

    // materials.json path -> the pack's materials by name (a name in no table: the model keeps its empty one)
    readonly Dictionary<string, PrefabMaterialTable> _prefabMaterials = new();
    readonly HashSet<string> _noPrivatePack = new();

    void WarnNoPrivatePack(string root)
    {
        if (_noPrivatePack.Add(root)) Warn($"private pack models are not installed ({root}); their objects are placeholder boxes");
    }

    /// <summary>A prefab GLB as a template: its bodies on the world layer, and (with a display) the pack's materials on its meshes.</summary>
    Node3D? LoadPrefab(string path)
    {
        if (LoadGltf(path) is not { } scene) return null;
        foreach (var imp in Descendants(scene).OfType<ImporterMeshInstance3D>().ToList())
        {
            // a scene generated outside the game loop may keep importer nodes: make them drawable (children, the bodies, move over)
            var mi = new MeshInstance3D { Name = imp.Name, Mesh = imp.Mesh.GetMesh(), Transform = imp.Transform };
            imp.ReplaceBy(mi);
            imp.Free();
        }
        foreach (var body in Descendants(scene).OfType<CollisionObject3D>())
        {
            body.CollisionLayer = Drycastle.ShooterCore.Colliders.CollisionLayers.World;
            body.SetMeta("surface", "metal");
        }
        if (CollisionOnly) return scene;
        var materials = PrefabMaterials(Path.Combine(Path.GetDirectoryName(path) ?? "", "materials.json"));
        foreach (var mi in AllMeshes(scene))
        {
            if (mi.Mesh == null) continue;
            for (var i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                if (mi.GetActiveMaterial(i) is { } blank && materials.TryGetValue(blank.ResourceName, out var mat))
                    mi.SetSurfaceOverrideMaterial(i, mat);
        }
        return scene;
    }

    PrefabMaterialTable PrefabMaterials(string file)
    {
        if (_prefabMaterials.TryGetValue(file, out var known)) return known;
        var made = new PrefabMaterialTable { Dir = Path.GetDirectoryName(file) ?? "" };
        _prefabMaterials[file] = made;
        if (!File.Exists(file)) { Warn($"prefab materials not found: {file}; the pack's models are drawn plain"); return made; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var m in doc.RootElement.GetProperty("materials").EnumerateObject())
                made.Specs[m.Name] = m.Value.Clone();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Warn($"prefab materials unreadable: {file}: {e.Message}; the pack's models are drawn plain");
        }
        return made;
    }

    static StandardMaterial3D PrefabMaterial(JsonElement m, string dir, Dictionary<string, Texture2D?> textures)
    {
        float Num(string key, float fallback) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;
        string? Str(string key) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Vector3 Uv(string key, float fallback)
            => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2
                ? new Vector3(v[0].GetSingle(), v[1].GetSingle(), fallback) : new Vector3(fallback, fallback, fallback);
        Texture2D? Tex(string key)
        {
            if (Str(key) is not { } rel) return null;
            if (textures.TryGetValue(rel, out var t)) return t;
            var path = Path.Combine(dir, rel);
            var img = File.Exists(path) ? Image.LoadFromFile(path) : null;
            if (img == null || img.IsEmpty()) return textures[rel] = null;
            if (img.GetWidth() > PrefabTextureMax || img.GetHeight() > PrefabTextureMax)
            {
                var k = (float)PrefabTextureMax / Math.Max(img.GetWidth(), img.GetHeight());
                img.Resize(Math.Max(1, (int)(img.GetWidth() * k)), Math.Max(1, (int)(img.GetHeight() * k)), Image.Interpolation.Bilinear);
            }
            img.GenerateMipmaps();
            return textures[rel] = ImageTexture.CreateFromImage(img);
        }

        var mat = new StandardMaterial3D
        {
            AlbedoColor = Str("albedo_color") is { } c && Color.HtmlIsValid(c) ? Color.FromHtml(c) : Colors.White,
            AlbedoTexture = Tex("albedo_texture"),
            Roughness = Num("roughness", 0.8f),
            Metallic = Num("metallic", 0f),
            MetallicSpecular = Num("metallic_specular", 0.5f),
            Transparency = (BaseMaterial3D.TransparencyEnum)(int)Num("transparency", 0),
            AlphaScissorThreshold = Num("alpha_scissor_threshold", 0.5f),
            CullMode = (BaseMaterial3D.CullModeEnum)(int)Num("cull_mode", 0),
            ShadingMode = (BaseMaterial3D.ShadingModeEnum)(int)Num("shading_mode", 1),
            Uv1Scale = Uv("uv1_scale", 1f),
            Uv1Offset = Uv("uv1_offset", 0f),
        };
        if (Tex("normal_texture") is { } normal)
        {
            mat.NormalEnabled = true;
            mat.NormalTexture = normal;
            mat.NormalScale = Num("normal_scale", 1f);
        }
        if (Str("emission_color") is { } e && Color.HtmlIsValid(e))
        {
            mat.EmissionEnabled = true;
            mat.Emission = Color.FromHtml(e);
            mat.EmissionEnergyMultiplier = Num("emission_energy", 1f);
            mat.EmissionOperator = (BaseMaterial3D.EmissionOperatorEnum)(int)Num("emission_operator", 0); // 1: the colour times the texture (a white colour with a texture glows only where the texture does)
            mat.EmissionTexture = Tex("emission_texture");
        }
        return mat;
    }
}

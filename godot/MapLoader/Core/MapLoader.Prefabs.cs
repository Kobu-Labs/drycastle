using System.Collections.Concurrent;
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
        var t0 = Core.LoadTimers.Start();
        if (LoadGltf(path) is not { } scene) return null;
        var t1 = Core.LoadTimers.Start();
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
        var t2 = Core.LoadTimers.Start();
        var materials = PrefabMaterials(Path.Combine(Path.GetDirectoryName(path) ?? "", "materials.json"));
        foreach (var mi in AllMeshes(scene))
        {
            if (mi.Mesh == null) continue;
            for (var i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                if (mi.GetActiveMaterial(i) is { } blank && materials.TryGetValue(blank.ResourceName, out var mat))
                    mi.SetSurfaceOverrideMaterial(i, mat);
        }
        if (Core.LoadTimers.Enabled && Core.LoadTimers.Ms(Core.LoadTimers.Start() - t0) > 50) // the join-time report: what a slow prefab spends its time on
            GD.Print($"loadprefab {Core.LoadTimers.Ms(Core.LoadTimers.Start() - t0):F0} ms {Path.GetFileName(path)}: glTF {Core.LoadTimers.Ms(t1 - t0):F0} meshes {Core.LoadTimers.Ms(t2 - t1):F0} materials {Core.LoadTimers.Ms(Core.LoadTimers.Start() - t2):F0}");
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
            foreach (var (name, spec) in ReadMaterialSpecs(file)) made.Specs[name] = spec;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Warn($"prefab materials unreadable: {file}: {e.Message}; the pack's models are drawn plain");
        }
        return made;
    }

    /// <summary>The material specs of a materials.json by name; throws when it is unreadable.</summary>
    static Dictionary<string, JsonElement> ReadMaterialSpecs(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var specs = new Dictionary<string, JsonElement>();
        foreach (var m in doc.RootElement.GetProperty("materials").EnumerateObject()) specs[m.Name] = m.Value.Clone();
        return specs;
    }

    /// <summary>A pack texture as a material wears it: decoded, at most <see cref="PrefabTextureMax"/>, with mipmaps, on the GPU; null when the file is missing or unreadable. Any thread.</summary>
    static Texture2D? DecodePrefabTexture(string path)
    {
        var img = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (img == null || img.IsEmpty()) return null;
        if (img.GetWidth() > PrefabTextureMax || img.GetHeight() > PrefabTextureMax)
        {
            var k = (float)PrefabTextureMax / Math.Max(img.GetWidth(), img.GetHeight());
            img.Resize(Math.Max(1, (int)(img.GetWidth() * k)), Math.Max(1, (int)(img.GetHeight() * k)), Image.Interpolation.Bilinear);
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    // ---- the textures decoded ahead (a pack's 4096 atlas took the main thread 0.3-0.8 s: docs/perf-investigation/join-time.md) ----

    /// <summary>A texture being decoded ahead. State 0: waiting for a slot, 1: a worker decodes it, 2: the main thread took it before a worker started (decodes it itself).</summary>
    sealed class TextureJob
    {
        public int State;
        public System.Threading.Tasks.Task<Texture2D?> Done = null!;
    }

    /// <summary>Textures of prefab materials decoded ahead on worker threads, by full path; each is taken once (<see cref="TryTakePrefabTexture"/>).</summary>
    static readonly ConcurrentDictionary<string, Lazy<TextureJob>> TexturesAhead = new();
    /// <summary>The material specs the glTF workers read, by materials.json path (null: unreadable).</summary>
    static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, JsonElement>?>> SpecsAhead = new();
    /// <summary>Two decodes at a time: the main thread keeps a core, and the texture the load needs next is not queued behind all the others.</summary>
    static readonly System.Threading.SemaphoreSlim TextureSlots = new(2);

    /// <summary>A prefab GLB was parsed on a glTF worker: starts decoding the textures of the pack materials it wears (a GLB with no materials.json beside it wears none).</summary>
    static void PrefetchPrefabTextures(string glbPath, GltfState state)
    {
        var dir = Path.GetDirectoryName(glbPath) ?? "";
        var file = Path.Combine(dir, "materials.json");
        if (!File.Exists(file)) return;
        var specs = SpecsAhead.GetOrAdd(file, f => new Lazy<Dictionary<string, JsonElement>?>(() =>
        {
            try { return ReadMaterialSpecs(f); }
            catch (Exception) { return null; } // the main thread reads it again and warns
        })).Value;
        if (specs == null) return;
        foreach (var blank in state.GetMaterials())
        {
            if (blank == null || !specs.TryGetValue(blank.ResourceName, out var spec)) continue;
            foreach (var key in new[] { "albedo_texture", "normal_texture", "emission_texture" })
                if (spec.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } rel
                    && (key != "emission_texture" || spec.TryGetProperty("emission_color", out _)))
                    PrefetchPrefabTexture(Path.GetFullPath(Path.Combine(dir, rel)));
        }
    }

    static void PrefetchPrefabTexture(string path)
    {
        if (GltfStop) return;
        _ = TexturesAhead.GetOrAdd(path, p => new Lazy<TextureJob>(() =>
        {
            var job = new TextureJob();
            job.Done = System.Threading.Tasks.Task.Run(async () =>
            {
                await TextureSlots.WaitAsync();
                try
                {
                    if (GltfStop || System.Threading.Interlocked.CompareExchange(ref job.State, 1, 0) != 0) return null; // quitting, or the main thread took it
                    var t = Core.LoadTimers.Start();
                    var tex = DecodePrefabTexture(p);
                    Core.LoadTimers.Lap("worker.prefab_textures", t);
                    return tex;
                }
                finally { TextureSlots.Release(); }
            });
            lock (GltfWorkers) { GltfWorkers.RemoveAll(w => w.IsCompleted); GltfWorkers.Add(job.Done); }
            return job;
        })).Value;
    }

    /// <summary>The texture a worker decoded for <paramref name="path"/> (waits while it decodes); false when none was asked for, no worker had started on it, or it failed: the caller decodes it.</summary>
    static bool TryTakePrefabTexture(string path, out Texture2D? tex, out string how)
    {
        tex = null;
        how = "not prefetched";
        if (!TexturesAhead.TryRemove(Path.GetFullPath(path), out var lazy)) return false;
        var job = lazy.Value;
        how = "queued";
        if (System.Threading.Interlocked.CompareExchange(ref job.State, 2, 0) == 0) return false; // still queued: decoding it here is sooner than waiting for the queue
        how = job.Done.IsCompleted ? "ahead" : "waited for the worker";
        ((IAsyncResult)job.Done).AsyncWaitHandle.WaitOne();
        if (!job.Done.IsCompletedSuccessfully) { how = "failed"; return false; }
        tex = job.Done.Result;
        return true;
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
            var t0 = Core.LoadTimers.Start();
            var tex = TryTakePrefabTexture(path, out var ahead, out var how) ? ahead : DecodePrefabTexture(path);
            if (Core.LoadTimers.Enabled && Core.LoadTimers.Ms(Core.LoadTimers.Start() - t0) is var ms and > 30) GD.Print($"loadtexture {ms:F0} ms {rel}: {how}"); // the join-time report
            return textures[rel] = tex;
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

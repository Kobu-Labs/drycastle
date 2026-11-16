using System.Diagnostics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Godot;
using NVector3 = System.Numerics.Vector3;
using NVector2 = System.Numerics.Vector2;

namespace Drycastle.MapLoader;

/// <summary>
/// Pack trees (#287): near the player the baked code-built trees give way to detailed models of the private nature pack
/// (<c>assets-private/synty/glb/nature</c>, found as the other private packs are, see <see cref="PrivateAssetsDir"/>). Trees stay one record
/// each in trees.json; the collision is the code-built one and is not touched. Config: shooter/data/pack_trees.json (res://data), which only the
/// shooter has; <c>DRYCASTLE_PACK_TREES=0</c>, a missing pack (nature/manifest.json) and a map without <c>model.base_uv2</c> in trees.json
/// (the props meshes then have no tree bases, see <see cref="TreeCutShaders"/>) leave everything as it was.
/// <para>The cut is on the GPU. The props meshes that hold baked trees (<c>props</c> of a chunk, <c>parcel_ID_props</c>, also the dynamic ones of a parcel swap)
/// get the <c>props_tree_cut</c> material instead of the flat one, and the pack trees the <c>pack_tree</c> material; both read the global shader parameter
/// <c>tree_cut</c> (focus, radius, fade band) that <see cref="PackTreeStep"/> sets each frame, and use the tree's base point (UV2) to keep a tree whole on
/// one side or the other. Inside the band both are drawn, dithered against one noise so each pixel has exactly one of them. Nothing is cut at runtime.</para>
/// <para>Drawing: the ground is cut into <c>tile_m</c> tiles; each tile within <c>near_m + ahead_m</c> of the focus is built, nearest first, on a worker
/// thread (<see cref="PackTileMesher"/>: every tree's triangles transformed into one mesh per material), and its surfaces are handed to the
/// rendering server a few at a time within <c>budget_ms</c>. A tile costs one draw per material. The model of a tree is chosen from its position and
/// height (<see cref="PackTreeMath.Choose"/>). The radius of the cut never passes a tile that is not built yet, so the baked trees only vanish
/// where the pack trees stand. The same streamer feeds it as the meadow (<see cref="MeadowStreamer"/>).</para>
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Pack trees on or off (<c>DRYCASTLE_PACK_TREES</c>, on by default); set before Load. They also need the pack, the config and a trees.json with base_uv2.</summary>
    public static bool PackTreesEnabled { get; set; } = PackTreeSettings.ParseEnabled(System.Environment.GetEnvironmentVariable(PackTreeSettings.EnvVar));

    const string PackTreeConfigPath = "res://data/pack_trees.json";

    /// <summary>A built (or building) tile: its trees as buckets to hand over, one surface of the mesh node per call.</summary>
    sealed class PackTile
    {
        public required Vector3 Centre;
        public Task<PackTileResult>? Job;
        public readonly Queue<ReadyBucket> Commit = new();
        public MeshInstance3D? Node;
        public PackPlacement[] Placed = Array.Empty<PackPlacement>();

        /// <summary>Everything is in the mesh (a tile with no pack tree is ready at once).</summary>
        public bool Ready => Job == null && Commit.Count == 0;
    }

    sealed record ReadyBucket(int Material, Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I);
    sealed record PackTileResult(List<ReadyBucket> Buckets, PackPlacement[] Placed);

    PackTreeConfig? _packCfg;
    string? _packDir;
    Dictionary<string, string?> _packFiles = new();
    readonly List<PackModelMesh> _packModels = new();
    readonly Dictionary<string, int> _packModelIds = new(); // name -> index into _packModels, -1 = not in the pack
    readonly List<ShaderMaterial> _packMaterials = new();
    readonly Dictionary<string, int> _packMaterialIds = new();
    PackCandidate[] _packCone = Array.Empty<PackCandidate>(), _packBox = Array.Empty<PackCandidate>();
    Dictionary<(int Tx, int Ty), List<int>> _packTreeTiles = new();
    Node3D? _packRoot;
    readonly Dictionary<(int Tx, int Ty), PackTile> _packTiles = new();
    readonly List<(float D, int Tx, int Ty)> _packWanted = new();
    readonly List<(int Tx, int Ty)> _packScratch = new();
    readonly List<float> _packUnbuilt = new();
    Vector2 _packFocus; // world x, z of the last step
    float _packCutR;

    /// <summary>True when pack trees are set up for this map (the pack, the config and the trees are there).</summary>
    public bool PackTreesActive => _packRoot != null;

    /// <summary>What the pack trees cost and draw now: tiles with pack trees in view range, draw calls (tiles times their materials), and the trees and triangles inside the cut radius (the rest of a tile is collapsed in the shader).</summary>
    public readonly record struct PackTreeStats(int Tiles, int Draws, int Instances, long Triangles);

    public PackTreeStats PackTreeDrawn
    {
        get
        {
            int tiles = 0, draws = 0, instances = 0; long tris = 0;
            foreach (var t in _packTiles.Values)
            {
                if (t.Node is not { Visible: true } node) continue;
                tiles++;
                draws += ((ArrayMesh)node.Mesh).GetSurfaceCount();
                foreach (var p in t.Placed)
                {
                    var dx = p.Base.X - _packFocus.X; var dz = p.Base.Z - _packFocus.Y;
                    if (dx * dx + dz * dz >= _packCutR * _packCutR) continue;
                    instances++;
                    tris += _packModels[p.Model].Triangles;
                }
            }
            return new PackTreeStats(tiles, draws, instances, tris);
        }
    }

    /// <summary>The private asset folder (<c>DRYCASTLE_ASSETS_PRIVATE</c>, else <c>assets-private</c> beside the asset library), or null without an asset library.</summary>
    string? PrivateAssetsDir()
    {
        if (System.Environment.GetEnvironmentVariable("DRYCASTLE_ASSETS_PRIVATE") is { Length: > 0 } env) return env;
        return AssetsDir is { } dir ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? "", "assets-private") : null;
    }

    void PackWarn(string key, string message)
    {
        if (_clutterWarned.Add("pack-trees:" + key)) Warn(message);
    }

    // ---- set up and tear down ----

    void FreePackTrees()
    {
        foreach (var key in _packTiles.Keys.ToList()) FreePackTile(key);
        _packRoot?.QueueFree();
        _packRoot = null;
        _packCfg = null;
        _packDir = null;
        _packFiles = new();
        _packModels.Clear(); _packModelIds.Clear();
        _packMaterials.Clear(); _packMaterialIds.Clear();
        _packCone = Array.Empty<PackCandidate>(); _packBox = Array.Empty<PackCandidate>();
        _packTreeTiles = new();
        _packWanted.Clear();
        _packCutR = 0;
        TreeCutShaders.Disable();
    }

    /// <summary>
    /// Decides before the chunks load whether this map gets pack trees (the props meshes then get the cut material): everything needed is there, silently
    /// when the pack or the config is simply not (the viewer has no data folder, a machine without the pack), with one warning for a map that is
    /// not rebuilt for it (no <c>base_uv2</c>).
    /// </summary>
    void PlanPackTrees()
    {
        FreePackTrees();
        if (CollisionOnly || !PackTreesEnabled || string.IsNullOrEmpty(TreesJson)) return;
        if (!Godot.FileAccess.FileExists(PackTreeConfigPath)) return;
        var dir = PrivateAssetsDir() is { } priv ? Path.Combine(priv, "synty", "glb", "nature") : null;
        var manifestPath = dir == null ? null : Path.Combine(dir, "manifest.json");
        if (manifestPath == null || !File.Exists(manifestPath)) return; // no nature pack on this machine

        PackTreeConfig cfg;
        try { cfg = PackTreeConfig.Parse(Godot.FileAccess.Open(PackTreeConfigPath, Godot.FileAccess.ModeFlags.Read).GetAsText()); }
        catch (DataFileException e) { PackWarn("config", $"pack trees: {e.Message}; no pack trees"); return; }
        if (!PackTreeSettings.ReadBaseUv2(TreesJson))
        {
            PackWarn("base-uv2", "pack trees: this map's trees.json has no model.base_uv2 (its props meshes do not carry the base of each tree); pack trees stay off, rebuild the map");
            return;
        }
        _packCfg = cfg;
        _packDir = dir;
        _packFiles = ReadNatureManifest(manifestPath);
    }

    /// <summary>Loads the pack models after the map is loaded; leaves the pack trees off unless some of the configured models are there.</summary>
    void InitPackTrees()
    {
        if (_packCfg is not { } cfg || _packDir == null || _trees.Count == 0) return;
        var missing = new List<string>();
        var cone = BuildPackList(cfg.For(true), missing);
        var box = BuildPackList(cfg.For(false), missing);
        if (missing.Count > 0) PackWarn("models", $"pack trees: {missing.Count} models are not in the nature pack and are skipped: {string.Join(", ", missing)}");
        if (cone.Length == 0 && box.Length == 0) { PackWarn("empty", "pack trees: none of the configured models could be loaded; no pack trees"); _packCfg = null; return; }
        if (cone.Length == 0 || box.Length == 0) PackWarn("shape", $"pack trees: no model for the {(cone.Length == 0 ? "cone" : "box")} shape; the other shape's models stand in");
        _packCone = cone.Length > 0 ? cone : box;
        _packBox = box.Length > 0 ? box : cone;

        _packTreeTiles = PackTreeMath.TileIndex(_trees, cfg.TileM);
        _packRoot = new Node3D { Name = "PackTrees" };
        Root.AddChild(_packRoot);
        EnsureStreamer();
        GD.Print($"pack trees: {_packModels.Count} models ({_packMaterials.Count} materials), near {cfg.NearM} m (+{cfg.FadeM} m fade), tiles {cfg.TileM} m");
    }

    /// <summary>The models of the pack's manifest.json by name: the GLB path (null for a model that is only an LOD or a collision body).</summary>
    static Dictionary<string, string?> ReadNatureManifest(string path)
    {
        var result = new Dictionary<string, string?>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var name = m.GetProperty("name").GetString() ?? "";
                bool Set(string key) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String;
                if (name.Length == 0 || Set("lod_of") || Set("collision_of")) continue;
                result[name] = m.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException) { }
        return result;
    }

    PackCandidate[] BuildPackList(IReadOnlyList<PackTreeEntry> entries, List<string> missing)
    {
        var list = new List<PackCandidate>();
        foreach (var e in entries)
        {
            if (e.Weight <= 0) continue;
            var id = PackModelId(e.Name);
            if (id < 0) { if (!missing.Contains(e.Name)) missing.Add(e.Name); continue; }
            list.Add(new PackCandidate(id, _packModels[id].HeightM, e.Weight, e.Bulk));
        }
        return list.ToArray();
    }

    int PackModelId(string name)
    {
        if (_packModelIds.TryGetValue(name, out var id)) return id;
        var model = _packFiles.TryGetValue(name, out var file) ? LoadPackModel(name, file) : null;
        if (model != null) { _packModels.Add(model); id = _packModels.Count - 1; } else id = -1;
        return _packModelIds[name] = id;
    }

    PackModelMesh? LoadPackModel(string name, string? file)
    {
        var path = file != null && File.Exists(Path.Combine(_packDir!, file)) ? Path.Combine(_packDir!, file) : Path.Combine(_packDir!, name + ".glb");
        if (!File.Exists(path)) return null;
        Node3D? scene = null;
        try
        {
            scene = LoadPrefab(path); // the pack's shared materials on its meshes (nature/materials.json)
            if (scene == null) return null;
            var surfaces = new List<PackSurface>();
            float y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var mi in AllMeshes(scene).ToList())
            {
                if (mi.Mesh is not ArrayMesh src) continue;
                var xf = mi.Transform;
                for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) xf = p.Transform * xf;
                var normalBasis = xf.Basis.Inverse().Transposed();
                for (var s = 0; s < src.GetSurfaceCount(); s++)
                {
                    if (src.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
                    var a = src.SurfaceGetArrays(s);
                    var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (v.Length == 0) continue;
                    var n = a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var uv = a[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    var idx = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Index].AsInt32Array();
                    var verts = new NVector3[v.Length]; var normals = new NVector3[v.Length]; var uvs = new NVector2[v.Length];
                    for (var i = 0; i < v.Length; i++)
                    {
                        var p = xf * v[i];
                        verts[i] = new NVector3(p.X, p.Y, p.Z);
                        y0 = Mathf.Min(y0, p.Y); y1 = Mathf.Max(y1, p.Y);
                        var q = n == null ? Vector3.Up : (normalBasis * n[i]).Normalized();
                        normals[i] = new NVector3(q.X, q.Y, q.Z);
                        uvs[i] = uv == null ? default : new NVector2(uv[i].X, uv[i].Y);
                    }
                    surfaces.Add(new PackSurface(PackMaterialSlot(mi.GetActiveMaterial(s) as StandardMaterial3D), verts, normals, uvs, idx ?? Enumerable.Range(0, v.Length).ToArray()));
                }
            }
            return surfaces.Count == 0 ? null : new PackModelMesh { Name = name, HeightM = y1 - y0, Surfaces = surfaces };
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            PackWarn("load:" + name, $"pack trees: {name}: {e.Message}");
            return null;
        }
        finally { scene?.Free(); } // the meshes and materials are reference counted
    }

    /// <summary>
    /// The slot of the shader material that reproduces a pack material (albedo texture and colour, alpha scissor, cull mode, roughness, metallic, specular):
    /// one slot per distinct look, whatever the material is called, so the atlas trunk and the atlas crown of the models share a draw.
    /// </summary>
    int PackMaterialSlot(StandardMaterial3D? src)
    {
        var scissor = src == null || src.Transparency == BaseMaterial3D.TransparencyEnum.Disabled ? -1f : src.AlphaScissorThreshold; // alpha blending and hash are drawn as a scissor
        var key = src == null ? "plain" : string.Join('|', src.AlbedoTexture?.GetInstanceId() ?? 0, src.AlbedoColor.ToHtml(), scissor, (int)src.CullMode, src.Roughness, src.Metallic, src.MetallicSpecular);
        if (_packMaterialIds.TryGetValue(key, out var slot)) return slot;
        var cull = src?.CullMode switch { BaseMaterial3D.CullModeEnum.Front => "cull_front", BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled", _ => "cull_back" };
        var mat = new ShaderMaterial { Shader = TreeCutShaders.Load("pack_tree.gdshader", cull) };
        if (src != null)
        {
            if (src.AlbedoTexture != null) mat.SetShaderParameter("albedo_tex", src.AlbedoTexture);
            mat.SetShaderParameter("albedo_colour", src.AlbedoColor);
            mat.SetShaderParameter("roughness", src.Roughness);
            mat.SetShaderParameter("metallic", src.Metallic);
            mat.SetShaderParameter("specular", src.MetallicSpecular);
        }
        mat.SetShaderParameter("scissor", scissor);
        _packMaterials.Add(mat);
        return _packMaterialIds[key] = _packMaterials.Count - 1;
    }

    // ---- the baked side: the material of a props mesh ----

    static ShaderMaterial? _propsCutMaterial;

    /// <summary>The material of a props mesh that holds baked trees when pack trees are on: the flat one plus the cut (shared, see <see cref="FlatMaterial"/>).</summary>
    static ShaderMaterial PropsCutMaterial() => _propsCutMaterial ??= new ShaderMaterial { Shader = TreeCutShaders.Load("props_tree_cut.gdshader") };

    /// <summary>Whether a node gets <see cref="PropsCutMaterial"/>: pack trees are on for the map and it is a props mesh (a chunk's or a parcel's, not a textured fence) that carries the tree bases in UV2.</summary>
    bool TreeCutProps(string baseName, Mesh? mesh)
    {
        if (_packCfg == null || mesh == null || mesh.GetSurfaceCount() == 0) return false;
        if (baseName != "props" && PropsParcelId(baseName) == null) return false;
        if (mesh is not ArrayMesh am || ((ulong)am.SurfaceGetFormat(0) & (ulong)Mesh.ArrayFormat.FormatTexUV2) == 0) return false;
        if (_clutterWarned.Add("pack-trees:uv2-checked")) CheckBaseUv2(mesh, baseName);
        return true;
    }

    /// <summary>Warns once when the UV2 the engine hands the shader is not the base point the pipeline wrote (a compressed attribute loses metres in a big map): the first mesh with UV2 is read back.</summary>
    void CheckBaseUv2(Mesh mesh, string name)
    {
        var a = mesh.SurfaceGetArrays(0);
        if (a[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil) return;
        var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uv2 = a[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        for (var i = 0; i < uv2.Length && i < v.Length; i++)
        {
            if (uv2[i].X >= PackTreeMath.NoBase * 0.5f) continue;
            var off = new Vector2(uv2[i].X - v[i].X, uv2[i].Y - v[i].Z).Length();
            if (off > 60f) PackWarn("uv2-range", $"pack trees: UV2 of {name} is {off:F0} m from its vertex; the base points look compressed or wrong, trees will be cut in the wrong places");
            return;
        }
    }

    // ---- streaming (called every frame by the MeadowStreamer with the focus) ----

    /// <summary>
    /// One step around <paramref name="focus"/> (Godot axes): free the far tiles, start the nearest missing ones on the worker threads, hand finished
    /// surfaces to the rendering server within the budget, show the tiles in range and set the cut.
    /// </summary>
    void PackTreeStep(Vector3 focus)
    {
        if (_packCfg is not { } c || _packRoot == null) return;
        float fx = focus.X, fy = -focus.Z; // map metres
        _packFocus = new Vector2(focus.X, focus.Z);
        float DistTo((int Tx, int Ty) k) => PackTreeMath.DistToBox(fx, fy, k.Tx * c.TileM, k.Ty * c.TileM, (k.Tx + 1) * c.TileM, (k.Ty + 1) * c.TileM);

        _packWanted.Clear();
        foreach (var (tx, ty, d) in PackTreeMath.TilesNear(fx, fy, c.NearM + c.AheadM, c.TileM))
            if (_packTreeTiles.ContainsKey((tx, ty))) _packWanted.Add((d, tx, ty));
        _packWanted.Sort((a, b) => a.D.CompareTo(b.D));

        _packScratch.Clear();
        foreach (var key in _packTiles.Keys) if (DistTo(key) > c.NearM + 2 * c.AheadM) _packScratch.Add(key);
        foreach (var key in _packScratch) FreePackTile(key);

        var inFlight = 0;
        foreach (var t in _packTiles.Values) if (t.Job != null) inFlight++;
        foreach (var (_, tx, ty) in _packWanted)
        {
            if (inFlight >= c.MaxInFlight) break;
            if (_packTiles.ContainsKey((tx, ty))) continue;
            StartPackTile(c, (tx, ty));
            inFlight++;
        }

        foreach (var t in _packTiles.Values)
        {
            if (t.Job is not { IsCompleted: true } job) continue;
            t.Job = null;
            if (job.IsFaulted) { PackWarn("tile", $"pack trees: a tile could not be built: {job.Exception?.GetBaseException().Message}"); continue; }
            t.Placed = job.Result.Placed;
            foreach (var b in job.Result.Buckets) t.Commit.Enqueue(b);
        }

        var sw = Stopwatch.StartNew(); // at least one surface per call, then until the budget is spent
        foreach (var (_, tx, ty) in _packWanted)
        {
            if (!_packTiles.TryGetValue((tx, ty), out var t)) continue;
            while (t.Commit.Count > 0)
            {
                CommitPackSurface(t, t.Commit.Dequeue());
                if (sw.Elapsed.TotalMilliseconds >= c.BudgetMs) goto committed;
            }
        }
        committed:

        foreach (var (key, t) in _packTiles)
            if (t.Node != null) t.Node.Visible = t.Ready && DistTo(key) < c.NearM; // tiles beyond the radius have nothing to show

        _packUnbuilt.Clear();
        foreach (var (d, tx, ty) in _packWanted)
        {
            if (d >= c.NearM) break;
            if (!(_packTiles.TryGetValue((tx, ty), out var t) && t.Ready)) _packUnbuilt.Add(d);
        }
        _packCutR = PackTreeMath.CutRadius(c.NearM, _packUnbuilt);
        TreeCutShaders.Set(focus, _packCutR, c.FadeM);
    }

    void StartPackTile(PackTreeConfig c, (int Tx, int Ty) key)
    {
        // everything the worker reads is a copy or immutable: a map reload cannot change it under the task
        var trees = _packTreeTiles[key].Select(i => _trees[i]).ToArray();
        var centre = new NVector3((key.Tx + 0.5f) * c.TileM, 0, -(key.Ty + 0.5f) * c.TileM);
        var model = TreeModel;
        PackCandidate[] cone = _packCone, box = _packBox;
        var models = _packModels.ToArray();
        _packTiles[key] = new PackTile
        {
            Centre = new Vector3(centre.X, 0, centre.Z),
            Job = Task.Run(() => BuildPackTileData(c, trees, model, cone, box, models, centre)),
        };
    }

    /// <summary>The worker's part of a tile: model choice, the merged triangles per material, as the arrays the mesh takes.</summary>
    static PackTileResult BuildPackTileData(PackTreeConfig c, TreeInstance[] trees, TreeModel model, PackCandidate[] cone, PackCandidate[] box, PackModelMesh[] models, NVector3 centre)
    {
        var placed = new List<PackPlacement>(trees.Length);
        foreach (var t in trees)
            if (PackTreeMath.Place(t, model, cone, box, c) is { } p) placed.Add(p);
        var buckets = PackTileMesher.Build(models, placed, centre).Select(b => new ReadyBucket(b.Material, ToArray(b.V), ToArray(b.N), ToArray(b.Uv), ToArray(b.Uv2), b.I.ToArray())).ToList();
        return new PackTileResult(buckets, placed.ToArray());
    }

    static Vector3[] ToArray(List<NVector3> l) { var r = new Vector3[l.Count]; for (var i = 0; i < r.Length; i++) r[i] = new Vector3(l[i].X, l[i].Y, l[i].Z); return r; }
    static Vector2[] ToArray(List<NVector2> l) { var r = new Vector2[l.Count]; for (var i = 0; i < r.Length; i++) r[i] = new Vector2(l[i].X, l[i].Y); return r; }

    void CommitPackSurface(PackTile tile, ReadyBucket b)
    {
        tile.Node ??= NewPackTileNode(tile);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.V;
        arrays[(int)Mesh.ArrayType.Normal] = b.N;
        arrays[(int)Mesh.ArrayType.TexUV] = b.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = b.Uv2; // the base of the tree of each vertex, the shader's cut
        arrays[(int)Mesh.ArrayType.Index] = b.I;
        var mesh = (ArrayMesh)tile.Node.Mesh;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _packMaterials[b.Material]);
    }

    MeshInstance3D NewPackTileNode(PackTile tile)
    {
        var node = new MeshInstance3D
        {
            Name = "pack_tile",
            Mesh = new ArrayMesh(),
            Position = tile.Centre,
            Visible = false, // shown when the whole tile is in
            CastShadow = _packCfg!.Shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled, // UV2 is not a lightmap UV here
        };
        _packRoot!.AddChild(node);
        return node;
    }

    void FreePackTile((int Tx, int Ty) key)
    {
        if (!_packTiles.Remove(key, out var tile)) return; // a job still running is left to finish; its result is dropped with the tile
        if (tile.Node != null && GodotObject.IsInstanceValid(tile.Node)) { tile.Node.GetParent()?.RemoveChild(tile.Node); tile.Node.QueueFree(); }
    }
}

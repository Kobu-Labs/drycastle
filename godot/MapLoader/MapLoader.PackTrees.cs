using System.Diagnostics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace Drycastle.MapLoader;

/// <summary>
/// Pack trees (prototype, #287): near the player the baked code-built trees give way to detailed models of the private nature pack
/// (<c>assets-private/synty/glb/nature</c>, found as the other private packs are, see <see cref="PrivateAssetsDir"/>). Trees stay one record
/// each in trees.json; the collision is the code-built one and is not touched. Config: shooter/data/pack_trees.json (res://data), which only the
/// shooter has; <c>DRYCASTLE_PACK_TREES=0</c> and a missing pack (nature/manifest.json) leave everything as it was.
/// <para>Drawing: the ground is cut into <c>tile_m</c> tiles; each tile whose centre is within <c>near_m</c> of the focus gets one MultiMesh per
/// pack model (the model of a tree is a hash of its position, scaled to the height of the code-built tree, turned by its rot), nearest first
/// within a budget, like the meadow (the same streamer, <see cref="MeadowStreamer"/>).</para>
/// <para>Hiding the baked trees: they are triangles inside the chunk meshes, fences and trees together (<c>props</c> of a chunk for the trees
/// without a parcel, <c>parcel_ID_props</c> for those of a parcel), with nothing to tell a tree from a fence. A props node that a wanted tile
/// touches is therefore cut once (<see cref="SplitPropsNode"/>): <see cref="TreeTriangleOwner"/> finds the tree of each triangle, the
/// triangles go to one mesh per tile of their tree (the rest, the fences, to one more) and the original node is hidden. A tile with pack trees
/// hides its baked mesh. A node whose tiles are all far is put back in one piece (<c>release_m</c>), so the draw calls of cut nodes stay near the player.</para>
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Pack trees on or off (<c>DRYCASTLE_PACK_TREES</c>, on by default); set before Load. They also need the pack, the config and trees.json.</summary>
    public static bool PackTreesEnabled { get; set; } = PackTreeSettings.ParseEnabled(System.Environment.GetEnvironmentVariable(PackTreeSettings.EnvVar));

    const string PackTreeConfigPath = "res://data/pack_trees.json";

    sealed record PackModel(string Name, ArrayMesh Mesh, float HeightM, float ReachM, int Triangles);

    sealed class PackTile
    {
        public readonly List<MultiMeshInstance3D> Nodes = new();
        public int Instances;
        public long Triangles;
    }

    /// <summary>A baked props node (fences and trees), its box in map metres, and the pieces it is cut into while a tile near it has pack trees.</summary>
    sealed class PropsNode
    {
        public required MeshInstance3D Original;
        public required Transform3D World;
        public required float X0, Y0, X1, Y1;
        public bool Split;
        public readonly List<(MeshInstance3D Node, (int Tx, int Ty)? Tile)> Pieces = new();
    }

    readonly List<PropsNode> _propsNodes = new();
    PackTreeConfig? _packCfg;
    List<(PackModel Model, float Weight)> _packCone = new(), _packBox = new();
    Dictionary<(int Tx, int Ty), List<int>> _packTreeTiles = new();
    TreeTriangleOwner? _packOwner;
    Node3D? _packRoot;
    readonly Dictionary<(int Tx, int Ty), PackTile> _packTiles = new();
    readonly Dictionary<(int Tx, int Ty), List<MeshInstance3D>> _packPieces = new();
    readonly List<(float D, int Tx, int Ty)> _packWanted = new();
    int _packCursor;
    Vector2 _packScanned = new(float.NaN, 0);

    /// <summary>True when pack trees are set up for this map (the pack, the config and the trees are there).</summary>
    public bool PackTreesActive => _packCfg != null;

    /// <summary>Pack tree nodes alive now: MultiMeshes (one per model and tile), their instances and the triangles they draw when all are in view (instances times the model's triangles).</summary>
    public (int MultiMeshes, int Instances, long Triangles) PackTreeNodes
    {
        get
        {
            int mm = 0, inst = 0; long tris = 0;
            foreach (var t in _packTiles.Values) { mm += t.Nodes.Count; inst += t.Instances; tris += t.Triangles; }
            return (mm, inst, tris);
        }
    }

    /// <summary>The cost on the baked side: props nodes cut into tile meshes now, and the meshes they were cut into (visible or hidden).</summary>
    public (int SplitNodes, int Pieces, int PiecesHidden) PackTreeBaked
    {
        get
        {
            int split = 0, pieces = 0, hidden = 0;
            foreach (var n in _propsNodes)
            {
                if (!n.Split || n.Pieces.Count == 0) continue;
                split++; pieces += n.Pieces.Count;
                foreach (var (node, _) in n.Pieces) if (!node.Visible) hidden++;
            }
            return (split, pieces, hidden);
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

    // ---- registering the baked props nodes (LoadChunk) ----

    void RegisterPropsNode(MeshInstance3D mi)
    {
        if (mi.Mesh == null) return;
        var world = mi.Transform;
        for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) world = p.Transform * world;
        var box = mi.Mesh.GetAabb();
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (var k = 0; k < 8; k++)
        {
            var c = world * (box.Position + new Vector3((k & 1) * box.Size.X, (k >> 1 & 1) * box.Size.Y, (k >> 2 & 1) * box.Size.Z));
            x0 = Mathf.Min(x0, c.X); x1 = Mathf.Max(x1, c.X);
            y0 = Mathf.Min(y0, -c.Z); y1 = Mathf.Max(y1, -c.Z); // map y = -Godot z
        }
        _propsNodes.Add(new PropsNode { Original = mi, World = world, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
    }

    // ---- set up and tear down ----

    void FreePackTrees()
    {
        foreach (var key in _packTiles.Keys.ToList()) FreePackTile(key);
        foreach (var n in _propsNodes)
            if (n.Split && GodotObject.IsInstanceValid(n.Original)) Unsplit(n);
        _propsNodes.Clear();
        _packPieces.Clear();
        _packRoot?.QueueFree();
        _packRoot = null;
        _packCfg = null;
        _packOwner = null;
        _packCone = new(); _packBox = new();
        _packTreeTiles = new();
        _packWanted.Clear();
        _packCursor = 0;
        _packScanned = new Vector2(float.NaN, 0);
    }

    /// <summary>Sets pack trees up after the map is loaded; does nothing (silently when the pack or the config is simply not there) unless everything needed is.</summary>
    void InitPackTrees()
    {
        if (CollisionOnly || !PackTreesEnabled || _trees.Count == 0 || _propsNodes.Count == 0) return;
        if (!Godot.FileAccess.FileExists(PackTreeConfigPath)) return; // the viewer has no data folder
        var dir = PrivateAssetsDir() is { } priv ? Path.Combine(priv, "synty", "glb", "nature") : null;
        var manifestPath = dir == null ? null : Path.Combine(dir, "manifest.json");
        if (manifestPath == null || !File.Exists(manifestPath)) return; // no nature pack on this machine

        PackTreeConfig cfg;
        try { cfg = PackTreeConfig.Parse(Godot.FileAccess.Open(PackTreeConfigPath, Godot.FileAccess.ModeFlags.Read).GetAsText()); }
        catch (DataFileException e) { PackWarn("config", $"pack trees: {e.Message}; no pack trees"); return; }

        var files = ReadNatureManifest(manifestPath);
        var missing = new List<string>();
        var cone = BuildPackList(cfg.For(true), dir!, files, missing);
        var box = BuildPackList(cfg.For(false), dir!, files, missing);
        if (missing.Count > 0) PackWarn("models", $"pack trees: {missing.Count} models are not in the nature pack and are skipped: {string.Join(", ", missing)}");
        if (cone.Count == 0 && box.Count == 0) { PackWarn("empty", "pack trees: none of the configured models could be loaded; no pack trees"); return; }
        if (cone.Count == 0 || box.Count == 0) PackWarn("shape", $"pack trees: no model for the {(cone.Count == 0 ? "cone" : "box")} shape; the other shape's models stand in");
        _packCone = cone.Count > 0 ? cone : box;
        _packBox = box.Count > 0 ? box : cone;

        _packCfg = cfg;
        _packOwner = new TreeTriangleOwner(_trees, TreeModel);
        _packTreeTiles = PackTreeMath.TileIndex(_trees, cfg.TileM);
        _packRoot = new Node3D { Name = "PackTrees" };
        Root.AddChild(_packRoot);
        EnsureStreamer();
        GD.Print($"pack trees: {cone.Count} cone and {box.Count} box models, near {cfg.NearM} m (+{cfg.FadeM} m fade), tiles {cfg.TileM} m, {_propsNodes.Count} baked props nodes");
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

    readonly Dictionary<string, PackModel?> _packModels = new();

    List<(PackModel, float)> BuildPackList(IReadOnlyList<PackTreeEntry> entries, string dir, Dictionary<string, string?> files, List<string> missing)
    {
        var list = new List<(PackModel, float)>();
        foreach (var e in entries)
        {
            if (e.Weight <= 0) continue;
            if (!_packModels.TryGetValue(e.Name, out var model))
            {
                model = files.TryGetValue(e.Name, out var file) ? LoadPackModel(e.Name, dir, file) : null;
                _packModels[e.Name] = model;
            }
            if (model == null) { if (!missing.Contains(e.Name)) missing.Add(e.Name); continue; }
            list.Add((model, e.Weight));
        }
        return list;
    }

    PackModel? LoadPackModel(string name, string dir, string? file)
    {
        var path = file != null && File.Exists(Path.Combine(dir, file)) ? Path.Combine(dir, file) : Path.Combine(dir, name + ".glb");
        if (!File.Exists(path)) return null;
        Node3D? scene = null;
        try
        {
            scene = LoadPrefab(path); // the pack's shared materials on its meshes (nature/materials.json)
            if (scene == null) return null;
            var mesh = MergePackMesh(scene);
            if (mesh == null) return null;
            var box = mesh.GetAabb();
            var tris = 0;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var a = mesh.SurfaceGetArrays(s);
                var idx = a[(int)Mesh.ArrayType.Index].AsInt32Array();
                tris += idx.Length > 0 ? idx.Length / 3 : a[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;
            }
            return new PackModel(name, mesh, box.Size.Y, box.Size.Length(), tris);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            PackWarn("load:" + name, $"pack trees: {name}: {e.Message}");
            return null;
        }
        finally { scene?.Free(); } // the meshes and materials are reference counted
    }

    /// <summary>All the meshes of a model scene as one mesh (node transforms baked in, each surface with the material it wears), so a model is one MultiMesh.</summary>
    static ArrayMesh? MergePackMesh(Node3D scene)
    {
        var merged = new ArrayMesh();
        foreach (var mi in AllMeshes(scene).ToList())
        {
            if (mi.Mesh is not ArrayMesh src) continue;
            var xf = mi.Transform;
            for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) xf = p.Transform * xf;
            var normalBasis = xf.Basis.Inverse().Transposed();
            for (var s = 0; s < src.GetSurfaceCount(); s++)
            {
                if (src.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
                var arrays = src.SurfaceGetArrays(s);
                var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (var i = 0; i < v.Length; i++) v[i] = xf * v[i];
                arrays[(int)Mesh.ArrayType.Vertex] = v;
                if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
                {
                    var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    for (var i = 0; i < n.Length; i++) n[i] = (normalBasis * n[i]).Normalized();
                    arrays[(int)Mesh.ArrayType.Normal] = n;
                }
                arrays[(int)Mesh.ArrayType.Tangent] = new Variant(); // not worth transforming: pack trees are lit by albedo and normals
                merged.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                merged.SurfaceSetMaterial(merged.GetSurfaceCount() - 1, mi.GetActiveMaterial(s));
            }
        }
        return merged.GetSurfaceCount() > 0 ? merged : null;
    }

    // ---- streaming (called every frame by the MeadowStreamer with the focus) ----

    /// <summary>One step around <paramref name="focus"/> (Godot axes): choose the tiles that should have pack trees, free the far ones, put far baked nodes back together, build the nearest missing tiles within the budget.</summary>
    void PackTreeStep(Vector3 focus)
    {
        if (_packCfg is not { } c) return;
        var fx = focus.X; var fy = -focus.Z; // map metres
        var moved = float.IsNaN(_packScanned.X) || (_packScanned - new Vector2(fx, fy)).LengthSquared() >= c.RescanM * c.RescanM;
        if (moved)
        {
            _packScanned = new Vector2(fx, fy);
            _packWanted.Clear();
            foreach (var (tx, ty, d) in PackTreeMath.TilesWithin(fx, fy, c.NearM, c.TileM))
                if (_packTreeTiles.ContainsKey((tx, ty))) _packWanted.Add((d, tx, ty));
            _packWanted.Sort((a, b) => a.D.CompareTo(b.D));
            // free what fell out of the fade band
            foreach (var key in _packTiles.Keys.ToList())
                if (MathF.Sqrt(MathF.Pow((key.Tx + 0.5f) * c.TileM - fx, 2) + MathF.Pow((key.Ty + 0.5f) * c.TileM - fy, 2)) > c.NearM + c.FadeM) FreePackTile(key);
            foreach (var n in _propsNodes)
                if (n.Split && n.Pieces.Count > 0 && PackTreeMath.DistToBox(fx, fy, n.X0, n.Y0, n.X1, n.Y1) > c.NearM + c.FadeM + c.ReleaseM
                    && !n.Pieces.Any(p => p.Tile is { } t && _packTiles.ContainsKey(t)))
                    Unsplit(n);
            _packCursor = 0;
        }

        var sw = Stopwatch.StartNew();
        var builds = 0;
        while (_packCursor < _packWanted.Count && builds < c.MaxBuildsPerCall && sw.Elapsed.TotalMilliseconds < c.BudgetMs)
        {
            var (_, tx, ty) = _packWanted[_packCursor];
            if (_packTiles.ContainsKey((tx, ty))) { _packCursor++; continue; }
            // the baked trees of the tile come out of their props nodes first: cut each node that touches the tile, one per build
            float x0 = tx * c.TileM, y0 = ty * c.TileM, x1 = x0 + c.TileM, y1 = y0 + c.TileM;
            var uncut = _propsNodes.FirstOrDefault(n => !n.Split && n.X0 <= x1 && n.X1 >= x0 && n.Y0 <= y1 && n.Y1 >= y0);
            builds++;
            if (uncut != null) { SplitPropsNode(uncut); continue; }
            BuildPackTile(c, tx, ty);
            _packCursor++;
        }
    }

    // ---- the baked side: cutting a props node into tiles ----

    /// <summary>Accumulates the triangles of one piece, with the vertices they use renumbered.</summary>
    sealed class PieceBuilder
    {
        readonly Dictionary<int, int> _map = new();
        public readonly List<Vector3> V = new(); public readonly List<Vector3> N = new(); public readonly List<Color> C = new();
        public readonly List<Vector2> UV = new(); public readonly List<Vector2> UV2 = new(); public readonly List<int> I = new();

        public void Add(int vertex, Vector3[] v, Vector3[]? n, Color[]? c, Vector2[]? uv, Vector2[]? uv2)
        {
            if (!_map.TryGetValue(vertex, out var k))
            {
                _map[vertex] = k = V.Count;
                V.Add(v[vertex]);
                if (n != null) N.Add(n[vertex]);
                if (c != null) C.Add(c[vertex]);
                if (uv != null) UV.Add(uv[vertex]);
                if (uv2 != null) UV2.Add(uv2[vertex]);
            }
            I.Add(k);
        }

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
            if (N.Count == V.Count) arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
            if (C.Count == V.Count) arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
            if (UV.Count == V.Count) arrays[(int)Mesh.ArrayType.TexUV] = UV.ToArray();
            if (UV2.Count == V.Count) arrays[(int)Mesh.ArrayType.TexUV2] = UV2.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = I.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }

    /// <summary>Cuts a baked props node: its triangles go to one mesh per tile of the tree they belong to, the others (fences) to one more, and the node itself is hidden.</summary>
    void SplitPropsNode(PropsNode n)
    {
        n.Split = true;
        var src = n.Original.Mesh;
        if (src == null || src.GetSurfaceCount() != 1)
        {
            PackWarn("split:" + n.Original.Name, $"pack trees: {n.Original.Name} is not one surface; its baked trees stay");
            return;
        }
        var a = src.SurfaceGetArrays(0);
        var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[]? nrm = a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Normal].AsVector3Array();
        Color[]? col = a[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Color].AsColorArray();
        Vector2[]? uv = a[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        Vector2[]? uv2 = a[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        var idx = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Index].AsInt32Array();
        var count = idx?.Length ?? v.Length;

        NVector3 Map(int vertex) { var p = n.World * v[vertex]; return new NVector3(p.X, -p.Z, p.Y); } // Godot axes to map metres
        var tile = _packCfg!.TileM;
        var rest = new PieceBuilder();
        var byTile = new Dictionary<(int, int), PieceBuilder>();
        for (var t = 0; t + 2 < count; t += 3)
        {
            int i0 = idx?[t] ?? t, i1 = idx?[t + 1] ?? t + 1, i2 = idx?[t + 2] ?? t + 2;
            var owner = _packOwner!.Find(Map(i0), Map(i1), Map(i2));
            PieceBuilder piece;
            if (owner < 0) piece = rest;
            else
            {
                var tree = _trees[owner];
                var key = (PackTreeMath.TileOf(tree.X, tile), PackTreeMath.TileOf(tree.Y, tile));
                if (!byTile.TryGetValue(key, out piece!)) byTile[key] = piece = new PieceBuilder();
            }
            piece.Add(i0, v, nrm, col, uv, uv2); piece.Add(i1, v, nrm, col, uv, uv2); piece.Add(i2, v, nrm, col, uv, uv2);
        }
        if (byTile.Count == 0) return; // no trees in it: the node stays as it is

        void AddPiece(PieceBuilder b, (int, int)? key)
        {
            if (b.I.Count == 0) return;
            var node = new MeshInstance3D
            {
                Name = key is { } k ? $"{n.Original.Name}_trees_{k.Item1}_{k.Item2}" : $"{n.Original.Name}_rest",
                Mesh = b.ToMesh(),
                Transform = n.Original.Transform,
                MaterialOverride = n.Original.MaterialOverride,
                CastShadow = n.Original.CastShadow,
                Layers = n.Original.Layers,
                Visible = key is not { } tk || !_packTiles.ContainsKey(tk),
            };
            n.Original.GetParent().AddChild(node);
            n.Pieces.Add((node, key));
            if (key is { } pk)
            {
                if (!_packPieces.TryGetValue(pk, out var l)) _packPieces[pk] = l = new();
                l.Add(node);
            }
        }
        AddPiece(rest, null);
        foreach (var (key, b) in byTile) AddPiece(b, key);
        n.Original.Visible = false;
    }

    /// <summary>Puts a cut props node back in one piece: frees its pieces, shows the original.</summary>
    void Unsplit(PropsNode n)
    {
        foreach (var (node, tile) in n.Pieces)
        {
            if (tile is { } t && _packPieces.TryGetValue(t, out var l)) { l.Remove(node); if (l.Count == 0) _packPieces.Remove(t); }
            if (GodotObject.IsInstanceValid(node)) { node.GetParent()?.RemoveChild(node); node.QueueFree(); }
        }
        if (n.Pieces.Count > 0) n.Original.Visible = true;
        n.Pieces.Clear();
        n.Split = false;
    }

    void SetPiecesVisible((int, int) tile, bool visible)
    {
        if (_packPieces.TryGetValue(tile, out var l)) foreach (var p in l) p.Visible = visible;
    }

    // ---- the pack side: one tile ----

    void BuildPackTile(PackTreeConfig c, int tx, int ty)
    {
        var tile = new PackTile();
        _packTiles[(tx, ty)] = tile;
        var byModel = new Dictionary<PackModel, List<(Vector3 P, float Rot, float Scale)>>();
        foreach (var i in _packTreeTiles[(tx, ty)])
        {
            var t = _trees[i];
            var list = t.Cone ? _packCone : _packBox;
            var pick = PackTreeMath.Pick(list.Select(e => e.Weight).ToList(), PackTreeMath.Roll(t.X, t.Y));
            if (pick < 0) continue;
            var model = list[pick].Model;
            if (!byModel.TryGetValue(model, out var l)) byModel[model] = l = new();
            l.Add((ToGodot(t.X, t.Y, t.Z), t.Rot, PackTreeMath.ScaleFor(t, TreeModel, model.HeightM)));
        }
        var centre = new Vector3((tx + 0.5f) * c.TileM, 0, -(ty + 0.5f) * c.TileM);
        foreach (var (model, list) in byModel)
        {
            var y0 = list.Min(e => e.P.Y); var y1 = list.Max(e => e.P.Y);
            var origin = new Vector3(centre.X, (y0 + y1) * 0.5f, centre.Z);
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = model.Mesh, InstanceCount = list.Count };
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue, float.MinValue);
            for (var k = 0; k < list.Count; k++)
            {
                var (p, rot, scale) = list[k];
                var local = p - origin;
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity.Rotated(Vector3.Up, rot).Scaled(new Vector3(scale, scale, scale)), local));
                var r = model.ReachM * scale; // generous: the model's diagonal around its foot
                min = new Vector3(Mathf.Min(min.X, local.X - r), Mathf.Min(min.Y, local.Y - r), Mathf.Min(min.Z, local.Z - r));
                max = new Vector3(Mathf.Max(max.X, local.X + r), Mathf.Max(max.Y, local.Y + r), Mathf.Max(max.Z, local.Z + r));
            }
            mm.CustomAabb = new Aabb(min, max - min);
            var node = new MultiMeshInstance3D
            {
                Name = $"pack_{model.Name}",
                Multimesh = mm,
                Position = origin,
                CastShadow = c.Shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = c.NearM + c.FadeM,
                VisibilityRangeEndMargin = c.FadeM,
                VisibilityRangeFadeMode = c.FadeM > 0 ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            };
            _packRoot!.AddChild(node);
            tile.Nodes.Add(node);
            tile.Instances += list.Count;
            tile.Triangles += (long)list.Count * model.Triangles;
        }
        SetPiecesVisible((tx, ty), false);
    }

    void FreePackTile((int Tx, int Ty) key)
    {
        if (!_packTiles.Remove(key, out var tile)) return;
        foreach (var node in tile.Nodes)
            if (GodotObject.IsInstanceValid(node)) { node.GetParent()?.RemoveChild(node); node.QueueFree(); }
        SetPiecesVisible(key, true);
    }
}

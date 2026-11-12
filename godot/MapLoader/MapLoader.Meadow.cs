using System.Diagnostics;
using Godot;

namespace Suchohrad.MapLoader;

/// <summary>
/// Meadow clutter: tufts, flowers, ferns, pebbles, leaves and sticks on fields and in forests, generated around the player. meadow.bin
/// (manifest <c>files.meadow</c>) is a density raster, <see cref="MeadowGen"/> turns the cells near the focus into instances with a hash of
/// the cell (the same on every client, nothing is sent), and the ground height comes from <see cref="GroundHeight"/>. The ground is cut
/// into 24 m tiles; each tile and layer within the layer's reach (plus a margin) is one MultiMesh per kind, built nearest first within a time
/// budget per call, freed when it falls behind, and pooled. The focus is the current camera, or whatever the caller passes to
/// <see cref="SetClutterFocus"/> each frame. A map without meadow.bin, a headless server (<see cref="CollisionOnly"/>) and
/// <c>SUCHOHRAD_CLUTTER=0</c> do nothing.
/// </summary>
public sealed partial class MapLoader
{
    sealed class MeadowTile
    {
        public readonly List<GeometryInstance3D> Nodes = new();
        public int Instances;
    }

    /// <summary>The arrays of a clutter mesh, read once: merged tiles are built from them.</summary>
    sealed record KindArrays(Vector3[] Vertices, Vector3[]? Normals, Color[]? Colours, int[]? Indices);

    MeadowFile? _meadow;
    Node3D? _meadowRoot;
    MeadowStreamer? _meadowStreamer;
    Dictionary<string, Mesh> _meadowMeshes = new();
    readonly Dictionary<(int Tx, int Ty, int Layer), MeadowTile> _meadowTiles = new();
    readonly Dictionary<string, Stack<MultiMeshInstance3D>> _meadowPool = new();
    readonly Stack<MeshInstance3D> _meadowMergedPool = new();
    readonly Dictionary<string, KindArrays?> _kindArrays = new();
    int[] _meadowLive = Array.Empty<int>();
    Dictionary<(int, int), List<(float X, float Y, float Scale)>>? _meadowTrees;
    Vector3? _meadowFocusSet;
    Vector3 _meadowScanned = new(float.NaN, 0, 0);

    /// <summary>Nodes alive now: MultiMesh nodes (one per kind with many instances in a tile and layer) and merged meshes (one per tile and layer, for the small groups), and the vertices of the merged ones.</summary>
    public (int MultiMeshes, int Merged, int MergedVertices) MeadowNodes
    {
        get
        {
            int mm = 0, merged = 0, verts = 0;
            foreach (var t in _meadowTiles.Values)
                foreach (var n in t.Nodes)
                    if (n is MeshInstance3D { Mesh: ArrayMesh am }) { merged++; verts += am.SurfaceGetArrayLen(0); }
                    else mm++;
            return (mm, merged, verts);
        }
    }

    /// <summary>Tiles and instances alive now (0 when there is no meadow), for tests and the log.</summary>
    public (int Tiles, int Instances) MeadowLive => (_meadowTiles.Count, _meadowLive.Sum());

    /// <summary>The layers of the loaded meadow (empty without one).</summary>
    public IReadOnlyList<MeadowLayer> MeadowLayers => _meadow?.Layers ?? Array.Empty<MeadowLayer>();

    /// <summary>
    /// Where the player is, in Godot axes: the meadow around it is generated (a few tiles per call, so call it every frame). Optional: without
    /// a call the current camera is the focus. Does nothing without a meadow.
    /// </summary>
    public void SetClutterFocus(Vector3 position) => _meadowFocusSet = position;

    void FreeMeadow()
    {
        _meadowStreamer?.QueueFree();
        _meadowStreamer = null;
        _meadowRoot?.QueueFree();
        _meadowRoot = null;
        _meadow = null;
        _meadowTiles.Clear();
        _meadowPool.Clear();
        _meadowMergedPool.Clear();
        _kindArrays.Clear();
        _meadowTrees = null;
        _meadowLive = Array.Empty<int>();
        _meadowScanned = new Vector3(float.NaN, 0, 0);
        _meadowFocusSet = null;
    }

    void InitMeadow(string path, Dictionary<string, Mesh> meshes)
    {
        if (!File.Exists(path)) { ClutterWarn("meadow-file", $"meadow: {Path.GetFileName(path)} is listed in the manifest but missing"); return; }
        try { _meadow = MeadowFile.Parse(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException)
        {
            ClutterWarn("meadow-parse", $"meadow: {e.Message}; no meadow clutter");
            return;
        }
        _meadowMeshes = meshes;
        _meadowLive = new int[_meadow.Layers.Count];
        _meadowRoot = new Node3D { Name = "MeadowClutter" };
        Root.AddChild(_meadowRoot);
        _meadowStreamer = new MeadowStreamer { Name = "MeadowStreamer", Map = this };
        Root.AddChild(_meadowStreamer);
        // trunks of the baked trees: generated instances keep out of them (the raster has no trunks)
        _meadowTrees = new();
        foreach (var t in _trees)
        {
            var key = ((int)MathF.Floor(t.X / TreeBucketM), (int)MathF.Floor(t.Y / TreeBucketM));
            if (!_meadowTrees.TryGetValue(key, out var l)) _meadowTrees[key] = l = new();
            l.Add((t.X, t.Y, t.Scale));
        }
        GD.Print($"meadow: {_meadow.ChunkCount} chunks, {_meadow.Layers.Count} layers, cells {_meadow.CellM} m");
    }

    const float TreeBucketM = 4f;

    bool NearTrunk(float x, float y)
    {
        var reach = _meadow!.TrunkReachM;
        var bx = (int)MathF.Floor(x / TreeBucketM); var by = (int)MathF.Floor(y / TreeBucketM);
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (_meadowTrees!.TryGetValue((bx + dx, by + dy), out var l))
                    foreach (var (tx, ty, ts) in l)
                    {
                        var r = reach * ts;
                        if ((tx - x) * (tx - x) + (ty - y) * (ty - y) <= r * r) return true;
                    }
        return false;
    }

    /// <summary>One streaming step around <paramref name="focus"/> (Godot axes): choose the tiles that should exist, free the far ones, build the nearest missing ones within the budget.</summary>
    void MeadowStep(Vector3 focus)
    {
        if (_meadow is not { } m || ClutterScale <= 0f) return;
        var cellM = m.CellM;
        var tileM = MeadowSettings.TileCells * cellM;
        var fx = focus.X; var fy = -focus.Z; // map metres
        var moved = float.IsNaN(_meadowScanned.X) || (new Vector2(_meadowScanned.X, _meadowScanned.Z) - new Vector2(fx, fy)).LengthSquared() >= MeadowSettings.RescanM * MeadowSettings.RescanM;
        if (moved)
        {
            _meadowScanned = new Vector3(fx, 0, fy);
            _meadowWanted.Clear();
            var allowance = MeadowSettings.TileAllowanceM(cellM);
            for (var li = 0; li < m.Layers.Count; li++)
            {
                // a tile shows from `reach` on; it is built BuildAheadM sooner
                var reach = MeadowSettings.RangeOf(m.Layers[li], ClutterScale) + allowance;
                var span = (int)MathF.Ceiling((reach + MeadowSettings.BuildAheadM) / tileM) + 1;
                int cx = (int)MathF.Floor(fx / tileM), cy = (int)MathF.Floor(fy / tileM);
                for (var ty = cy - span; ty <= cy + span; ty++)
                    for (var tx = cx - span; tx <= cx + span; tx++)
                    {
                        var d = Dist(tx, ty);
                        if (d <= reach + MeadowSettings.BuildAheadM) _meadowWanted.Add((d, tx, ty, li));
                    }
            }
            _meadowWanted.Sort((a, b) => a.D.CompareTo(b.D));
            // free what fell behind
            _meadowFree.Clear();
            foreach (var key in _meadowTiles.Keys)
            {
                var reach = MeadowSettings.RangeOf(m.Layers[key.Layer], ClutterScale) + allowance;
                if (Dist(key.Tx, key.Ty) > reach + MeadowSettings.KeepBehindM) _meadowFree.Add(key);
            }
            foreach (var key in _meadowFree) FreeTile(key);
            _meadowCursor = 0;
        }

        float Dist(int tx, int ty) => MathF.Sqrt(MathF.Pow((tx + 0.5f) * tileM - fx, 2) + MathF.Pow((ty + 0.5f) * tileM - fy, 2));

        var sw = Stopwatch.StartNew();
        var builds = 0;
        while (_meadowCursor < _meadowWanted.Count && builds < MeadowSettings.MaxBuildsPerCall && sw.Elapsed.TotalMilliseconds < MeadowSettings.BudgetMs)
        {
            var (_, tx, ty, li) = _meadowWanted[_meadowCursor++];
            if (_meadowTiles.ContainsKey((tx, ty, li))) continue;
            builds++;
            BuildMeadowTile(m, tx, ty, li, tileM);
        }
    }

    readonly List<(float D, int Tx, int Ty, int Layer)> _meadowWanted = new();
    readonly List<(int Tx, int Ty, int Layer)> _meadowFree = new();
    int _meadowCursor;
    readonly List<MeadowInstance> _meadowScratch = new();

    void BuildMeadowTile(MeadowFile m, int tx, int ty, int li, float tileM)
    {
        var layer = m.Layers[li];
        var tile = new MeadowTile();
        _meadowTiles[(tx, ty, li)] = tile; // also when empty: it is not generated again until it is freed
        _meadowScratch.Clear();
        MeadowGen.Tile(m, li, tx, ty, MeadowSettings.TileCells, _meadowScratch);
        if (_meadowScratch.Count == 0) return;
        var byKind = new Dictionary<string, List<(Vector3 P, float Rot, float Scale, byte Tint)>>();
        foreach (var i in _meadowScratch)
        {
            if (!MeadowSettings.HasRoom(layer.Name, ClutterScale, _meadowLive[li] + tile.Instances)) break;
            if (NearTrunk(i.X, i.Y)) continue;
            var z = GroundHeight(i.X, -i.Y);
            if (float.IsNaN(z)) continue;
            if (!byKind.TryGetValue(i.Kind, out var l)) byKind[i.Kind] = l = new();
            l.Add((new Vector3(i.X, z, -i.Y), i.Rot, i.Scale, i.Tint));
            tile.Instances++;
        }
        var centre = new Vector3((tx + 0.5f) * tileM, 0, -(ty + 0.5f) * tileM);
        var range = MeadowSettings.RangeOf(layer, ClutterScale) + MeadowSettings.TileAllowanceM(m.CellM);
        List<(Mesh Mesh, string Kind, List<(Vector3 P, float Rot, float Scale, byte Tint)> List)>? small = null;
        foreach (var (kind, list) in byKind)
        {
            if (!_meadowMeshes.TryGetValue(kind, out var mesh)) { ClutterWarn("meadow-kind:" + kind, $"meadow: no mesh for kind '{kind}'"); continue; }
            if (!MeadowSettings.UsesMultiMesh(list.Count)) { (small ??= new()).Add((mesh, kind, list)); continue; }
            var node = RentMeadowNode(kind, mesh, list.Count);
            var mm = node.Multimesh;
            var y0 = float.MaxValue; var y1 = float.MinValue;
            foreach (var (p, _, _, _) in list) { y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
            var origin = new Vector3(centre.X, (y0 + y1) * 0.5f, centre.Z);
            for (var k = 0; k < list.Count; k++)
            {
                var (p, rot, scale, tint) = list[k];
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity.Rotated(Vector3.Up, rot).Scaled(new Vector3(scale, scale, scale)), p - origin));
                mm.SetInstanceColor(k, TintColor(tint));
            }
            var half = tileM * 0.5f + 1f;
            var reachY = Mathf.Max(mesh.GetAabb().Size.Length(), 0.1f) * 1.5f;
            mm.CustomAabb = new Aabb(new Vector3(-half, y0 - origin.Y - reachY, -half), new Vector3(2 * half, y1 - y0 + 2 * reachY, 2 * half));
            node.Position = origin;
            node.VisibilityRangeEnd = range;
            node.Visible = true;
            tile.Nodes.Add(node);
        }
        if (small != null && BuildMergedMesh(small, centre, tileM, range, _meadowRoot!, "meadow_merged", _meadowMergedPool) is { } merged) tile.Nodes.Add(merged);
        _meadowLive[li] += tile.Instances;
    }

    KindArrays? ArraysOf(string kind, Mesh mesh)
    {
        if (_kindArrays.TryGetValue(kind, out var cached)) return cached;
        KindArrays? result = null;
        if (mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetArrays(0) is { Count: > 0 } a && a[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil)
        {
            var normals = a[(int)Mesh.ArrayType.Normal];
            var colours = a[(int)Mesh.ArrayType.Color];
            var indices = a[(int)Mesh.ArrayType.Index];
            result = new KindArrays(
                a[(int)Mesh.ArrayType.Vertex].AsVector3Array(),
                normals.VariantType == Variant.Type.Nil ? null : normals.AsVector3Array(),
                colours.VariantType == Variant.Type.Nil ? null : colours.AsColorArray(),
                indices.VariantType == Variant.Type.Nil ? null : indices.AsInt32Array());
        }
        _kindArrays[kind] = result;
        return result;
    }

    /// <summary>The small groups of a tile (or stored cell) and layer as one mesh: every instance's triangles transformed into the tile's frame with the tint multiplied into the vertex colours.</summary>
    MeshInstance3D? BuildMergedMesh(List<(Mesh Mesh, string Kind, List<(Vector3 P, float Rot, float Scale, byte Tint)> List)> groups, Vector3 centre, float tileM, float range,
        Node3D parent, string name, Stack<MeshInstance3D>? pool = null)
    {
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var cols = new List<Color>();
        var y0 = float.MaxValue; var y1 = float.MinValue; var reachY = 0.1f;
        var hasNormals = true;
        foreach (var (mesh, kind, list) in groups)
        {
            if (ArraysOf(kind, mesh) is not { } src) continue;
            hasNormals &= src.Normals != null;
            reachY = MathF.Max(reachY, mesh.GetAabb().Size.Length() * 1.5f);
            var count = src.Indices?.Length ?? src.Vertices.Length;
            foreach (var (p, rot, scale, tint) in list)
            {
                var basis = Basis.Identity.Rotated(Vector3.Up, rot).Scaled(new Vector3(scale, scale, scale));
                var origin = p - new Vector3(centre.X, 0, centre.Z);
                var mul = TintColor(tint);
                y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y);
                for (var v = 0; v < count; v++)
                {
                    var i = src.Indices != null ? src.Indices[v] : v;
                    verts.Add(basis * src.Vertices[i] + origin);
                    if (src.Normals != null) norms.Add((basis * src.Normals[i]).Normalized());
                    var c = src.Colours != null ? src.Colours[i] : Colors.White;
                    cols.Add(new Color(c.R * mul.R, c.G * mul.G, c.B * mul.B, c.A));
                }
            }
        }
        if (verts.Count == 0) return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        if (hasNormals && norms.Count == verts.Count) arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
        var am = new ArrayMesh();
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var half = tileM * 0.5f + 1f;
        am.CustomAabb = new Aabb(new Vector3(-half, y0 - reachY, -half), new Vector3(2 * half, y1 - y0 + 2 * reachY, 2 * half));
        MeshInstance3D node;
        if (pool is { Count: > 0 }) node = pool.Pop();
        else
        {
            node = new MeshInstance3D
            {
                Name = name,
                MaterialOverride = FlatMaterial(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEndMargin = ClutterSettings.FadeMarginM,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            parent.AddChild(node);
        }
        node.Mesh = am;
        node.Position = new Vector3(centre.X, 0, centre.Z);
        node.VisibilityRangeEnd = range;
        node.Visible = true;
        return node;
    }

    MultiMeshInstance3D RentMeadowNode(string kind, Mesh mesh, int count)
    {
        if (!_meadowPool.TryGetValue(kind, out var pool)) _meadowPool[kind] = pool = new();
        MultiMeshInstance3D node;
        if (pool.Count > 0) node = pool.Pop();
        else
        {
            node = new MultiMeshInstance3D
            {
                Name = $"meadow_{kind}",
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh },
                MaterialOverride = FlatMaterial(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEndMargin = ClutterSettings.FadeMarginM,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            node.SetMeta("kind", kind); // Godot renames duplicate sibling names, so the pool key is not read from the name
            _meadowRoot!.AddChild(node);
        }
        node.Multimesh.InstanceCount = count;
        return node;
    }

    void FreeTile((int Tx, int Ty, int Layer) key)
    {
        if (!_meadowTiles.Remove(key, out var tile)) return;
        _meadowLive[key.Layer] -= tile.Instances;
        foreach (var node in tile.Nodes)
        {
            node.Visible = false;
            if (node is MeshInstance3D merged) { merged.Mesh = null; _meadowMergedPool.Push(merged); }
            else _meadowPool[node.GetMeta("kind").AsString()].Push((MultiMeshInstance3D)node);
        }
    }

    /// <summary>Follows the focus: the one set by <see cref="SetClutterFocus"/> this frame, else the current camera.</summary>
    sealed partial class MeadowStreamer : Node
    {
        public MapLoader Map { get; init; } = null!;

        public override void _Process(double delta)
        {
            if (Map._meadowFocusSet is { } f) { Map._meadowFocusSet = null; Map.MeadowStep(f); return; }
            if (GetViewport()?.GetCamera3D() is { } cam) Map.MeadowStep(cam.GlobalPosition);
        }
    }
}

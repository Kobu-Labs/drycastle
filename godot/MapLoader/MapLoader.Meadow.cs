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
        public readonly List<MultiMeshInstance3D> Nodes = new();
        public int Instances;
    }

    MeadowFile? _meadow;
    Node3D? _meadowRoot;
    MeadowStreamer? _meadowStreamer;
    Dictionary<string, Mesh> _meadowMeshes = new();
    readonly Dictionary<(int Tx, int Ty, int Layer), MeadowTile> _meadowTiles = new();
    readonly Dictionary<string, Stack<MultiMeshInstance3D>> _meadowPool = new();
    int[] _meadowLive = Array.Empty<int>();
    Dictionary<(int, int), List<(float X, float Y, float Scale)>>? _meadowTrees;
    Vector3? _meadowFocusSet;
    Vector3 _meadowScanned = new(float.NaN, 0, 0);

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
        var byKind = new Dictionary<string, List<(Vector3 P, float Rot, float Scale)>>();
        foreach (var i in _meadowScratch)
        {
            if (!MeadowSettings.HasRoom(layer.Name, ClutterScale, _meadowLive[li] + tile.Instances)) break;
            if (NearTrunk(i.X, i.Y)) continue;
            var z = GroundHeight(i.X, -i.Y);
            if (float.IsNaN(z)) continue;
            if (!byKind.TryGetValue(i.Kind, out var l)) byKind[i.Kind] = l = new();
            l.Add((new Vector3(i.X, z, -i.Y), i.Rot, i.Scale));
            tile.Instances++;
        }
        var centre = new Vector3((tx + 0.5f) * tileM, 0, -(ty + 0.5f) * tileM);
        var range = MeadowSettings.RangeOf(layer, ClutterScale) + MeadowSettings.TileAllowanceM(m.CellM);
        foreach (var (kind, list) in byKind)
        {
            if (!_meadowMeshes.TryGetValue(kind, out var mesh)) { ClutterWarn("meadow-kind:" + kind, $"meadow: no mesh for kind '{kind}'"); continue; }
            var node = RentMeadowNode(kind, mesh, list.Count);
            var mm = node.Multimesh;
            var y0 = float.MaxValue; var y1 = float.MinValue;
            foreach (var (p, _, _) in list) { y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
            var origin = new Vector3(centre.X, (y0 + y1) * 0.5f, centre.Z);
            for (var k = 0; k < list.Count; k++)
            {
                var (p, rot, scale) = list[k];
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity.Rotated(Vector3.Up, rot).Scaled(new Vector3(scale, scale, scale)), p - origin));
            }
            var half = tileM * 0.5f + 1f;
            var reachY = Mathf.Max(mesh.GetAabb().Size.Length(), 0.1f) * 1.5f;
            mm.CustomAabb = new Aabb(new Vector3(-half, y0 - origin.Y - reachY, -half), new Vector3(2 * half, y1 - y0 + 2 * reachY, 2 * half));
            node.Position = origin;
            node.VisibilityRangeEnd = range;
            node.Visible = true;
            tile.Nodes.Add(node);
        }
        _meadowLive[li] += tile.Instances;
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
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh },
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
            _meadowPool[node.GetMeta("kind").AsString()].Push(node);
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

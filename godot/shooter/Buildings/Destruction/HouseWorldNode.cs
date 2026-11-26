using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using NVec3 = System.Numerics.Vector3;
using MapLoaderT = Drycastle.MapLoader.MapLoader;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>Which collider a carved house gets (the server's must be the same): the greedy box cover of its cells, or one trimesh of their faces.</summary>
public enum HouseColliderKind { Boxes, AllFaces }

/// <summary>
/// The client's destructible houses (docs/house-destruction.md, WP-F). One <see cref="HouseReplica"/> per house of house_voxels.bin follows the server's
/// carve ops (242), the catch-up and resync runs (243) and the settings (244). An undamaged house keeps the map's flat material and its exact shell collider;
/// on its first carve its visual gets the discard shader (an R8 3D texture of the removed cells), a cut-face mesh shows the broken cells, and its collider
/// becomes the voxel one (<see cref="Collider"/>). Texture, mesh and collider are rebuilt at most once per house per frame. Fallen and crumbled cells
/// go to <see cref="Debris"/> (WP-G). A different op seq or hash than the server's is logged ("house desync &lt;id&gt;") and a 249 asked for.
/// One per map, client only: <see cref="Current"/>.
/// </summary>
public partial class HouseWorldNode : Node3D
{
    public const string ShaderPath = "res://shaders/house_destruction.gdshader";
    public static HouseWorldNode? Current { get; private set; }
    /// <summary>The carved houses' collider; the same as the server's (HouseDestructionServer).</summary>
    public static HouseColliderKind Collider = HouseColliderKind.Boxes;
    const double ResyncRetrySeconds = 5;

    IHouseDebris? _debris;
    /// <summary>The debris of fallen parts (WP-G); null = none. Set, it gets the current rubble mode.</summary>
    public IHouseDebris? Debris
    {
        get => _debris;
        set { _debris = value; _debris?.SetRubble(_rubbleStay); }
    }

    /// <summary>Sends 249 HouseResync for a house (set by the net client).</summary>
    public Action<ushort>? RequestResync { get; set; }

    MapLoaderT _map = null!;
    DestructionConfig _cfg = null!;
    House?[] _houses = [];
    readonly List<House> _dirty = new();
    readonly List<House> _awaiting = new();
    bool _rubbleStay, _warmed;
    static Shader? _shader;

    readonly List<Quad> _quads = new();
    readonly List<NVec3> _pos = new(), _nrm = new();
    readonly List<(NVec3 Min, NVec3 Max)> _boxes = new();

    sealed class House
    {
        public House(HouseReplica replica, MapLoaderT.HouseNode? nodes)
        {
            Replica = replica;
            Visual = nodes?.Visual;
            Shape = nodes?.Collision;
        }

        public readonly HouseReplica Replica;
        public readonly MeshInstance3D? Visual;
        /// <summary>The map's collider of the house (its exact shell until the first carve).</summary>
        public readonly CollisionShape3D? Shape;
        public Shape3D? Shell;
        public Material? FlatMaterial;
        public ShaderMaterial? Material;
        public ImageTexture3D? Texture;
        public Godot.Collections.Array<Image>? Layers;
        public byte[][] LayerBytes = [];
        public MeshInstance3D? Cut;
        public ArrayMesh? CutMesh;
        public readonly List<CollisionShape3D> Boxes = new();
        public bool Swapped, Dirty;
        public double ResyncAt;
        public ushort Index => Replica.Grid.Index;
        public string Id => Replica.Grid.Id;
    }

    /// <summary>
    /// The houses of the map's house_voxels.bin matched to the map's house nodes by id, or null when destruction is off, the map has no file or the file is
    /// broken (logged). Loads the discard shader (a loading step, not the first carve).
    /// </summary>
    public static HouseWorldNode? Create(MapLoaderT map, DestructionConfig cfg)
    {
        if (!cfg.Enabled || map.HouseVoxelsPath() is not { } path) return null;
        IReadOnlyList<HouseGrid> grids;
        try
        {
            using var f = File.OpenRead(path);
            grids = HouseVoxelFile.Read(f);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            GD.PrintErr($"client: {path}: {e.Message}; houses are not destroyed");
            return null;
        }
        if (grids.Count == 0) return null;
        _shader ??= GD.Load<Shader>(ShaderPath);
        var node = new HouseWorldNode { Name = "HouseDestruction", _map = map, _cfg = cfg, _houses = new House?[grids.Max(g => g.Index) + 1] };
        var unplaced = 0;
        foreach (var g in grids)
        {
            var nodes = map.HouseNodes.GetValueOrDefault(g.Id);
            if (nodes?.Visual == null) unplaced++;
            node._houses[g.Index] = new House(new HouseReplica(g), nodes);
        }
        if (unplaced > 0) GD.PrintErr($"client: {unplaced} of {grids.Count} destructible houses have no house_<id>_visual node; their state is kept, not drawn");
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    House? Of(ushort index) => index < _houses.Length ? _houses[index] : null;

    /// <summary>242: one op, applied as the server did; a mismatch asks for the house's cells.</summary>
    public void OnCarve(HouseReplicaWire.Carve m)
    {
        if (Of(m.House) is not { } h) return;
        var (outcome, r) = h.Replica.OnCarve(m, _cfg);
        if (r != null)
        {
            MarkDirty(h);
            var g = h.Replica.Grid;
            _debris?.Fall(g, r, g.ToMap(new NVec3(m.Op.Cx, m.Op.Cy, m.Op.Cz) / 4f), r.Hash ^ ((uint)m.House << 16 | m.OpSeq));
        }
        if (outcome == HouseReplica.CarveOutcome.Desync) Desync(h);
    }

    /// <summary>243: one part of a house's removed cells (join catch-up or resync); the house is redrawn when the list is whole.</summary>
    public void OnCells(HouseReplicaWire.CellsPart m)
    {
        if (Of(m.House) is not { } h) return;
        switch (h.Replica.OnCells(m))
        {
            case HouseReplica.CellsOutcome.Replaced:
                _awaiting.Remove(h);
                MarkDirty(h);
                break;
            case HouseReplica.CellsOutcome.ReplacedHashDiffers:
                _awaiting.Remove(h);
                MarkDirty(h);
                GD.PrintErr($"house desync {h.Id} (hash differs after its cells came)");
                break;
        }
    }

    /// <summary>244: the rubble mode, and a round reset when asked.</summary>
    public void OnSettings(HouseReplicaWire.Settings s)
    {
        if (s.Reset) ResetAll();
        _rubbleStay = s.RubbleStay;
        _debris?.SetRubble(s.RubbleStay);
    }

    /// <summary>Every house whole, with its flat material and shell collider back, and no debris (round reset, leaving the server).</summary>
    public void ResetAll()
    {
        foreach (var h in _houses)
        {
            if (h == null) continue;
            h.Replica.Reset();
            Unswap(h);
            h.Dirty = false;
        }
        _dirty.Clear();
        _awaiting.Clear();
        _debris?.ResetAll();
    }

    void Desync(House h)
    {
        GD.Print($"house desync {h.Id}");
        h.Replica.MarkAwaiting();
        if (!_awaiting.Contains(h)) _awaiting.Add(h);
        h.ResyncAt = Time.GetTicksMsec() / 1000.0 + ResyncRetrySeconds;
        RequestResync?.Invoke(h.Index);
    }

    void MarkDirty(House h)
    {
        if (h.Dirty) return;
        h.Dirty = true;
        _dirty.Add(h);
    }

    public override void _Process(double delta)
    {
        if (!_warmed) WarmUp();
        if (_awaiting.Count > 0)
        {
            var now = Time.GetTicksMsec() / 1000.0;
            foreach (var h in _awaiting)
                if (now >= h.ResyncAt) { h.ResyncAt = now + ResyncRetrySeconds; RequestResync?.Invoke(h.Index); }
        }
        if (_dirty.Count == 0) return;
        foreach (var h in _dirty)
        {
            h.Dirty = false;
            Rebuild(h);
        }
        _dirty.Clear();
    }

    void Rebuild(House h)
    {
        if (!h.Replica.Cells.Damaged) { Unswap(h); return; }
        if (!h.Swapped) Swap(h);
        UpdateTexture(h);
        RebuildCut(h);
        RebuildCollider(h);
    }

    /// <summary>Map local (Godot frame) to world: the map root's transform.</summary>
    Transform3D MapToWorld => _map.Root.IsInsideTree() ? _map.Root.GlobalTransform : Transform3D.Identity;

    static Transform3D GridToMap(HouseGrid g)
    {
        var (x, y, z, o) = HouseFrame.Affine(g);
        return new Transform3D(new Basis(V(x), V(y), V(z)), V(o));
    }

    static Vector3 V(NVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The first carve: the shader material with its texture on the visual, the cut-face node, the shell collider set aside.</summary>
    void Swap(House h)
    {
        h.Swapped = true;
        var g = h.Replica.Grid;
        if (h.Visual != null && GodotObject.IsInstanceValid(h.Visual) && _shader != null)
        {
            int nx = g.Nx, ny = g.Ny, nz = g.Nz;
            h.LayerBytes = new byte[nz][];
            h.Layers = new Godot.Collections.Array<Image>();
            for (var z = 0; z < nz; z++)
            {
                h.LayerBytes[z] = new byte[nx * ny];
                h.Layers.Add(Image.CreateFromData(nx, ny, false, Image.Format.R8, h.LayerBytes[z]));
            }
            h.Texture = new ImageTexture3D();
            h.Texture.Create(Image.Format.R8, nx, ny, nz, false, h.Layers);
            h.Material = new ShaderMaterial { Shader = _shader };
            h.Material.SetShaderParameter("removed", h.Texture);
            h.Material.SetShaderParameter("grid_size", new Vector3(nx, ny, nz));
            h.Material.SetShaderParameter("world_to_grid", new Projection((MapToWorld * GridToMap(g)).AffineInverse()));
            h.FlatMaterial = h.Visual.MaterialOverride;
            h.Visual.MaterialOverride = h.Material;
            h.CutMesh = new ArrayMesh();
            h.Cut = new MeshInstance3D { Name = $"house_{g.Id}_cut", Mesh = h.CutMesh, MaterialOverride = MapLoaderT.SharedFlatMaterial };
            _map.Root.AddChild(h.Cut);
        }
        if (h.Shape != null && GodotObject.IsInstanceValid(h.Shape))
        {
            h.Shell = h.Shape.Shape;
            if (Collider == HouseColliderKind.Boxes) h.Shape.Disabled = true;
            else h.Shape.Shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        }
    }

    /// <summary>Back to the undamaged look and the shell collider.</summary>
    void Unswap(House h)
    {
        if (!h.Swapped) return;
        h.Swapped = false;
        if (h.Visual != null && GodotObject.IsInstanceValid(h.Visual)) h.Visual.MaterialOverride = h.FlatMaterial;
        h.Material = null; h.Texture = null; h.Layers = null; h.LayerBytes = []; h.FlatMaterial = null;
        if (h.Cut != null && GodotObject.IsInstanceValid(h.Cut)) h.Cut.QueueFree();
        h.Cut = null; h.CutMesh = null;
        foreach (var b in h.Boxes) if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        h.Boxes.Clear();
        if (h.Shape != null && GodotObject.IsInstanceValid(h.Shape))
        {
            h.Shape.Shape = h.Shell;
            h.Shape.Disabled = false;
            _map.HouseGeometryChanged();
        }
        h.Shell = null;
    }

    void UpdateTexture(House h)
    {
        if (h.Texture == null || h.Layers == null) return;
        var g = h.Replica.Grid;
        for (var z = 0; z < h.LayerBytes.Length; z++)
        {
            h.Replica.FillRemovedLayer(z, h.LayerBytes[z]);
            h.Layers[z].SetData(g.Nx, g.Ny, false, Image.Format.R8, h.LayerBytes[z]);
        }
        h.Texture.Update(h.Layers);
    }

    void RebuildCut(House h)
    {
        if (h.CutMesh == null) return;
        var g = h.Replica.Grid;
        _quads.Clear(); _pos.Clear(); _nrm.Clear();
        HouseMesher.Surface(h.Replica.Cells, MeshMode.CutFaces, _quads);
        HouseFrame.Triangles(g, _quads, _pos, _nrm);
        h.CutMesh.ClearSurfaces();
        if (_pos.Count == 0) return;
        var verts = new Vector3[_pos.Count];
        var normals = new Vector3[_pos.Count];
        var colours = new Color[_pos.Count];
        for (var i = 0; i < verts.Length; i++) { verts[i] = V(_pos[i]); normals[i] = V(_nrm[i]); }
        for (var q = 0; q < _quads.Count; q++)
        {
            var c = HouseFrame.CutColour(g, _quads[q].Palette);
            var col = new Color(c.X, c.Y, c.Z);
            for (var k = 0; k < 6; k++) colours[q * 6 + k] = col;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colours;
        h.CutMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    void RebuildCollider(House h)
    {
        if (h.Shape == null || !GodotObject.IsInstanceValid(h.Shape) || h.Shape.GetParent() is not Node3D body) return;
        var g = h.Replica.Grid;
        var toBody = body.GlobalTransform.AffineInverse() * MapToWorld; // map local to the body's frame (identity for a chunk's collider)
        if (Collider == HouseColliderKind.AllFaces)
        {
            _quads.Clear(); _pos.Clear();
            HouseMesher.Surface(h.Replica.Cells, MeshMode.AllFaces, _quads);
            HouseFrame.Triangles(g, _quads, _pos);
            var faces = new Vector3[_pos.Count];
            for (var i = 0; i < faces.Length; i++) faces[i] = toBody * V(_pos[i]);
            if (h.Shape.Shape is ConcavePolygonShape3D shape) shape.SetFaces(faces);
        }
        else
        {
            _boxes.Clear();
            HouseMesher.Boxes(h.Replica.Cells, _boxes);
            for (var i = 0; i < _boxes.Count; i++)
            {
                if (i == h.Boxes.Count)
                {
                    var cs = new CollisionShape3D { Name = $"Voxels{i}", Shape = new BoxShape3D() };
                    body.AddChild(cs);
                    h.Boxes.Add(cs);
                }
                var (centre, size, yaw) = HouseFrame.Box(g, _boxes[i].Min, _boxes[i].Max);
                var b = h.Boxes[i];
                ((BoxShape3D)b.Shape).Size = V(size);
                b.Transform = toBody * new Transform3D(new Basis(Vector3.Up, yaw), V(centre));
                b.Disabled = false;
            }
            for (var i = _boxes.Count; i < h.Boxes.Count; i++) h.Boxes[i].Disabled = true;
        }
        _map.HouseGeometryChanged();
    }

    /// <summary>Compiles the discard shader before the first carve: a speck with its material in front of the camera for half a second.</summary>
    void WarmUp()
    {
        if (_shader == null) { _warmed = true; return; }
        if (GetViewport()?.GetCamera3D() is not { } cam) return;
        _warmed = true;
        var layers = new Godot.Collections.Array<Image> { Image.CreateFromData(1, 1, false, Image.Format.R8, new byte[1]) };
        var tex = new ImageTexture3D();
        tex.Create(Image.Format.R8, 1, 1, 1, false, layers);
        var mat = new ShaderMaterial { Shader = _shader };
        mat.SetShaderParameter("removed", tex);
        mat.SetShaderParameter("world_to_grid", new Projection(Transform3D.Identity));
        var speck = new MeshInstance3D { Name = "HouseWarm", Mesh = new QuadMesh { Size = new Vector2(0.002f, 0.002f) }, MaterialOverride = mat, Position = new Vector3(0, 0, -0.3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        cam.AddChild(speck);
        GetTree().CreateTimer(0.5).Timeout += () => { if (GodotObject.IsInstanceValid(speck)) speck.QueueFree(); };
    }
}

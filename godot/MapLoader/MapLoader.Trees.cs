using Godot;
using Drycastle.ShooterCore;
using GVector3 = Godot.Vector3;

namespace Drycastle.MapLoader;

/// <summary>
/// Collision of the base map's trees and of placed objects (trees.json, objects.json). A tree drawn as a pack model is solid at its limbs
/// and its leaves stop nothing (<see cref="DressTrees"/>, #375). Otherwise trunks are solid (world layer, players
/// walk around them); crowns and bushes are shot-only (<see cref="CollisionLayers.ShotOnly"/>): bullets, pellets and sight lines
/// stop on them, players and cars move through. The geometry is the pure ShooterCore code (<see cref="TreeColliders"/>,
/// <see cref="ObjectColliders"/>), so the server and the client agree. Each body carries a "surface" meta ("wood" or
/// "foliage") for the impact classifier. Trees are one static body per chunk and role, not one per tree.
/// </summary>
public sealed partial class MapLoader
{
    readonly List<TreeInstance> _trees = new();

    /// <summary>Contents of trees.json (empty when the map has none, for example maps before 0.6.0).</summary>
    public string TreesJson { get; private set; } = "";
    /// <summary>The trees of the map, as read from trees.json (map metres).</summary>
    public IReadOnlyList<TreeInstance> Trees => _trees;
    /// <summary>Trees this says yes to (map metres, x east, y north) get no collision: a tree baked into the map where a building has since been put (the mechanic garage, #320). Set before loading. Their canopies stay in the chunk meshes until the map is built again.</summary>
    public Func<float, float, bool>? TreeFilter { get; set; }
    /// <summary>The tree model numbers (trunk and canopy sizes) that the collision solids are built from.</summary>
    public TreeModel TreeModel { get; private set; } = new(0.35f, 1.8f, 2.6f);

    /// <summary>Map metres (x east, y north, z up) to Godot axes (x east, y up, z -north).</summary>
    static GVector3 ToGodot(float x, float y, float z) => new(x, z, -y);

    /// <summary>Builds the trunk and crown bodies of every tree, per chunk (or on the root for a tree outside any loaded chunk).</summary>
    void BuildTreeColliders() { foreach (var _ in BuildTreeColliderSteps()) { } }

    const string TreeLimbsPath = "res://data/tree_limbs.json";

    /// <summary>
    /// Gives every tree the pack model it is drawn as, so its solids are that model's wood (#375). Only on a map that draws pack trees (trees.json
    /// <c>model.base_uv2</c>) and where the two data files are (not in the viewer): else the trees keep the code-built solids. It does not ask whether
    /// this machine has the pack or has pack trees switched off: the server and every client must build the same solids.
    /// </summary>
    TreeModel DressTrees(TreeModel model)
    {
        if (_trees.Count == 0 || !PackTreeSettings.ReadBaseUv2(TreesJson)) return model;
        if (!Godot.FileAccess.FileExists(TreeLimbsPath) || !Godot.FileAccess.FileExists(PackTreeConfigPath)) return model;
        try
        {
            var limbs = TreeLimbsFile.Parse(Godot.FileAccess.Open(TreeLimbsPath, Godot.FileAccess.ModeFlags.Read).GetAsText());
            var cfg = PackTreeConfig.Parse(Godot.FileAccess.Open(PackTreeConfigPath, Godot.FileAccess.ModeFlags.Read).GetAsText());
            return PackTreeLimbs.Dress(_trees, model, TreesJson, cfg, limbs);
        }
        catch (DataFileException e)
        {
            PackWarn("limbs", $"tree solids: {e.Message}; the trees keep the code-built solids");
            return model;
        }
    }

    /// <summary>Tree colliders in batches (thousands of shapes): yields the fraction done after each batch so a loading screen can redraw.</summary>
    IEnumerable<float> BuildTreeColliderSteps()
    {
        const int Batch = 300;
        _trees.Clear();
        _trees.AddRange(TreeColliders.Parse(TreesJson, out var model));
        model = DressTrees(model);
        TreeModel = model;
        var bodies = new Dictionary<(int, int, PartRole), StaticBody3D>();
        var n = 0;
        foreach (var t in _trees)
        {
            if (n++ % Batch == Batch - 1) yield return 0.5f * n / _trees.Count;
            if (TreeFilter?.Invoke(t.X, t.Y) == true) continue;
            foreach (var part in TreeColliders.Parts(t, model))
            {
                var key = (t.ChunkX, t.ChunkY, part.Role);
                if (!bodies.TryGetValue(key, out var body))
                {
                    body = NewSolidBody(part.Role == PartRole.Trunk ? "TreeTrunks" : "TreeCrowns", part.Layer, part.Group);
                    bodies[key] = body;
                }
                body.AddChild(SolidShape(part));
            }
        }
        // Bodies are filled while detached and attached once each: adding a shape to a body that is already in the tree
        // makes the physics server update it per shape (about 16 s for the village's 12,000 trees).
        var attached = 0;
        foreach (var ((cx, cy, _), body) in bodies)
        {
            var parent = _chunkRoots.TryGetValue($"chunk_{cx}_{cy}", out var cr) ? cr : Root;
            parent.AddChild(body);
            yield return 0.5f + 0.5f * ++attached / bodies.Count;
        }
    }

    /// <summary>The collision of the placed objects of one asset, as children of the object's node (scaled with it).</summary>
    void AddObjectColliders(Node3D node, string asset, bool isSolid = true)
    {
        // a satellite dish, or an object placed with solid false: no collision on the server or the client (AddObject also
        // switches off the bodies a client model brings along)
        if (ObjectColliders.IsPassThrough(asset, isSolid)) return;
        if (_assetManifest == null) ReadAssetManifest();
        var category = _assetManifest!.TryGetValue(asset, out var entry) ? entry.Category : "";
        var size = entry?.Size ?? GVector3.One;
        if (entry is { Prefab: true } && GetAssetTemplate(asset, out _) is { } prefab)
        {
            // a private pack prefab brings its own bodies. With a model they are already in it; a headless load has no
            // model, so it gets a copy of the prefab with the meshes taken out.
            if (!CollisionOnly) return;
            var solid = (Node3D)prefab.Duplicate();
            solid.Name = "Solid";
            foreach (var m in AllMeshes(solid).ToList()) m.Mesh = null; // not Free(): a prefab root is itself a mesh and the bodies hang under the meshes
            node.AddChild(solid);
            return;
        }
        if (entry is { Solids.Count: > 0 })
        {
            // the asset says itself what is solid (a flood wall piece): one body per surface, with a box for each
            foreach (var group in entry.Solids.GroupBy(s => s.Surface))
            {
                var body = NewSolidBody("Solid", CollisionLayers.World, group.Key);
                foreach (var solid in group) body.AddChild(SolidShape(solid.Part));
                node.AddChild(body);
            }
            return;
        }
        foreach (var part in ObjectColliders.ForCategory(category, size.X, size.Y, size.Z))
        {
            var body = NewSolidBody("Solid", part.Layer, part.Group);
            body.AddChild(SolidShape(part));
            node.AddChild(body);
        }
    }

    static StaticBody3D NewSolidBody(string name, uint layer, string surface)
    {
        var body = new StaticBody3D { Name = name, CollisionLayer = layer };
        body.SetMeta("surface", surface);
        return body;
    }

    // Shape resources shared by every solid with the same SolidShapeKeys key (one per distinct size, not one per part).
    readonly Dictionary<SolidShapeKeys.Key, Shape3D> _solidShapes = new();

    /// <summary>The Godot shape of one solid part: a box with the part's yaw, or a convex pyramid. Shapes are shared by key.</summary>
    CollisionShape3D SolidShape(SolidPart p)
    {
        var key = SolidShapeKeys.Of(p);
        if (!_solidShapes.TryGetValue(key, out var shape))
        {
            shape = key.Kind switch { 1 => PyramidShape(key), 2 => FrustumShape(key), 3 => LimbShape(key), _ => BoxShape(key) };
            _solidShapes[key] = shape;
        }
        var basis = Basis.Identity.Rotated(GVector3.Up, p.Yaw);
        if (p.Shape != PartShape.Box)
            // the pyramid and the frustum are built around their base centre, in the solid's yawed frame
            return new CollisionShape3D { Shape = shape, Transform = new Transform3D(basis, ToGodot(p.X, p.Y, p.Z0)) };
        var mid = (p.Z0 + p.Z1) / 2;
        // a rotation about Godot's up axis by the map yaw maps the box's X onto the map u axis (cos, sin)
        return new CollisionShape3D { Shape = shape, Transform = new Transform3D(basis, ToGodot(p.X, p.Y, mid)) };
    }

    static BoxShape3D BoxShape(SolidShapeKeys.Key k)
        => new() { Size = new GVector3(k.A / 1000f, k.B / 1000f, k.C / 1000f) };

    // The taper: the base ring at y = 0 (half-size A) and the top ring at y = height (half-size C), in the pyramid's local frame.
    static ConvexPolygonShape3D FrustumShape(SolidShapeKeys.Key k)
    {
        var (lo, height, hi) = (k.A / 1000f, k.B / 1000f, k.C / 1000f);
        var pts = new[]
        {
            new GVector3(-lo, 0, lo), new GVector3(lo, 0, lo), new GVector3(lo, 0, -lo), new GVector3(-lo, 0, -lo),
            new GVector3(-hi, height, hi), new GVector3(hi, height, hi), new GVector3(hi, height, -hi), new GVector3(-hi, height, -hi),
        };
        return new ConvexPolygonShape3D { Points = pts };
    }

    // A limb: the frustum with its top ring centred D along the local X and E along the map v axis, which is Godot's -Z.
    static ConvexPolygonShape3D LimbShape(SolidShapeKeys.Key k)
    {
        var (lo, height, hi, u, v) = (k.A / 1000f, k.B / 1000f, k.C / 1000f, k.D / 1000f, k.E / 1000f);
        var pts = new[]
        {
            new GVector3(-lo, 0, lo), new GVector3(lo, 0, lo), new GVector3(lo, 0, -lo), new GVector3(-lo, 0, -lo),
            new GVector3(u - hi, height, -v + hi), new GVector3(u + hi, height, -v + hi), new GVector3(u + hi, height, -v - hi), new GVector3(u - hi, height, -v - hi),
        };
        return new ConvexPolygonShape3D { Points = pts };
    }

    // Local points (no yaw, base centre at the origin): the base ring at y = 0 and the apex above it. The
    // CollisionShape3D transform yaws and places it; the ring is the same as PropColliders' ring rotated by the yaw.
    static ConvexPolygonShape3D PyramidShape(SolidShapeKeys.Key k)
    {
        var h = k.A / 1000f;
        var height = k.B / 1000f;
        var pts = new[]
        {
            new GVector3(-h, 0, h), new GVector3(h, 0, h), new GVector3(h, 0, -h), new GVector3(-h, 0, -h),
            new GVector3(0, height, 0),
        };
        return new ConvexPolygonShape3D { Points = pts };
    }
}

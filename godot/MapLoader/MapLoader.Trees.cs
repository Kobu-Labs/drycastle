using Godot;
using Suchohrad.ShooterCore;
using GVector3 = Godot.Vector3;

namespace Suchohrad.MapLoader;

/// <summary>
/// Collision of the base map's trees and of placed objects (trees.json, objects.json). Trunks are solid (world layer, players
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
    /// <summary>The tree model numbers (trunk and canopy sizes) that the collision solids are built from.</summary>
    public TreeModel TreeModel { get; private set; } = new(0.35f, 1.8f, 2.6f);

    /// <summary>Map metres (x east, y north, z up) to Godot axes (x east, y up, z -north).</summary>
    static GVector3 ToGodot(float x, float y, float z) => new(x, z, -y);

    /// <summary>Builds the trunk and crown bodies of every tree, per chunk (or on the root for a tree outside any loaded chunk).</summary>
    void BuildTreeColliders()
    {
        _trees.Clear();
        _trees.AddRange(TreeColliders.Parse(TreesJson, out var model));
        TreeModel = model;
        var bodies = new Dictionary<(int, int, PartRole), StaticBody3D>();
        foreach (var t in _trees)
            foreach (var part in TreeColliders.Parts(t, model))
            {
                var key = (t.ChunkX, t.ChunkY, part.Role);
                if (!bodies.TryGetValue(key, out var body))
                {
                    body = NewSolidBody(part.Role == PartRole.Trunk ? "TreeTrunks" : "TreeCrowns", part.Layer, part.Group);
                    var parent = _chunkRoots.TryGetValue($"chunk_{t.ChunkX}_{t.ChunkY}", out var cr) ? cr : Root;
                    parent.AddChild(body);
                    bodies[key] = body;
                }
                body.AddChild(SolidShape(part));
            }
    }

    /// <summary>The collision of the placed objects of one asset, as children of the object's node (scaled with it).</summary>
    void AddObjectColliders(Node3D node, string asset)
    {
        if (_assetManifest == null) ReadAssetManifest();
        var category = _assetManifest!.TryGetValue(asset, out var entry) ? entry.Category : "";
        var size = entry?.Size ?? GVector3.One;
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

    /// <summary>The Godot shape of one solid part: a box with the part's yaw, or a convex pyramid from its five points.</summary>
    static CollisionShape3D SolidShape(SolidPart p)
    {
        if (p.Shape == PartShape.Pyramid)
        {
            var pts = p.Points().Select(v => ToGodot(v.X, v.Y, v.Z)).ToArray();
            return new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = pts } };
        }
        var mid = (p.Z0 + p.Z1) / 2;
        return new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new GVector3(2 * p.HalfU, p.Z1 - p.Z0, 2 * p.HalfV) },
            // a rotation about Godot's up axis by the map yaw maps the box's X onto the map u axis (cos, sin)
            Transform = new Transform3D(Basis.Identity.Rotated(GVector3.Up, p.Yaw), ToGodot(p.X, p.Y, mid)),
        };
    }
}

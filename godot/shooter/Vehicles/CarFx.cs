using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Scud;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Vehicles;

/// <summary>Client visual of a vehicle: its model (vehicles.json; a private-pack model is the skin the colour index picks, a loose model has its paint material swapped for one of its own colours), or a box (a type without a model, or a build without the private pack).</summary>
public static class CarFx
{
    static readonly Dictionary<string, Node3D?> Templates = new();

    /// <summary>Parses a vehicle model into the template cache ahead of its first use (#170, T2-18).</summary>
    public static void Warm(string model)
    {
        if (ShotFx.Enabled) Load(model);
    }

    /// <summary>Warms the model and every skin of a vehicle type.</summary>
    public static void Warm(VehicleDef def)
    {
        if (def.Model == "") return;
        Warm(def.Model);
        foreach (var skin in def.Skins) Warm(skin);
    }

    /// <summary>Loads a model of the private asset library by its id ("pack:synty-farm/..."), a scene not in the tree; set by the shell once the map (which owns that library) is up. Null: not available, the vehicle is a box.</summary>
    public static Func<string, Node3D?>? PackLoader;
    static readonly HashSet<string> PackWarned = new();

    /// <summary>A model of the private pack by its id ("pack:..."), the cached template (duplicate it before use), or null without the pack. For the models that are not a vehicle's body (the Scud missile).</summary>
    public static Node3D? PackTemplate(string model) => LoadPack(model);

    static Node3D? LoadPack(string model)
    {
        if (Templates.TryGetValue(model, out var t) && t != null) return t;
        if (PackLoader?.Invoke(model) is not { } scene)
        {
            if (PackWarned.Add(model)) GD.PrintErr($"vehicle model {model} is not available (private pack missing, or the map is not up yet); using a box");
            return null; // not cached: it may be there after the map loads
        }
        foreach (var co in scene.FindChildren("*", "CollisionObject3D", true, false).OfType<CollisionObject3D>()) { co.CollisionLayer = 0; co.CollisionMask = 0; } // the vehicle's solid is the server's box, not the prefab's meshes
        return Templates[model] = scene;
    }

    static Node3D? Load(string model)
    {
        if (model.StartsWith("pack:", StringComparison.Ordinal)) return LoadPack(model);
        if (Templates.TryGetValue(model, out var t)) return t;
        Templates[model] = null;
        var path = Path.GetFullPath(Path.Combine(GamePaths.Assets(), model));
        if (!File.Exists(path)) { GD.PrintErr($"vehicle model not found: {path}; using a box"); return null; }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok) { GD.PrintErr($"vehicle model failed to load: {path}"); return null; }
        return Templates[model] = doc.GenerateScene(state) as Node3D;
    }

    /// <summary>A vehicle node (origin at the ground, facing -Z) under <paramref name="world"/> for a snapshot record's type and colour; headless clients get an empty node.</summary>
    public static Node3D Make(Node world, VehicleDef def, byte colour)
    {
        var root = new Node3D { Name = def.Id };
        world.AddChild(root);
        // the visible parts sit under a "Visual" node, so a boat's wave bob and tilt (WaterWaves) move the hull, not the hit-tested root
        var visual = new Node3D { Name = VisualName };
        root.AddChild(visual);
        if (!ShotFx.Enabled) return root;
        if (def.Model != "")
        {
            if (Load(def.SkinModel(colour)) is { } mt)
            {
                var m = (Node3D)mt.Duplicate();
                m.Position = new Vector3(def.ModelOffset[0], def.ModelOffset[1], def.ModelOffset[2]);
                var fit = new Node3D { Scale = Vector3.One * def.ModelScale, RotationDegrees = new Vector3(0, def.ModelYawDeg, 0) };
                fit.AddChild(m);
                visual.AddChild(fit);
                if (def.Tint is { } fixedTint)
                {
                    var c = new Color(fixedTint[0], fixedTint[1], fixedTint[2]);
                    foreach (var n in m.FindChildren("body", "Node3D", true, false)) Paint(n, c);
                }
                else PaintVariant(m, def, colour);
            }
            else visual.AddChild(def.Launcher != null ? ScudFx.GreyTruck(def) : Box(def, new Color(0.6f, 0.6f, 0.6f))); // a launcher truck's grey box has its rack and rocket under the model's node names
            return root;
        }
        visual.AddChild(Box(def, new Color(0.6f, 0.6f, 0.6f))); // no model: a grey box
        return root;
    }

    /// <summary>Name of the node that holds a vehicle's visible parts (see <see cref="Make"/>).</summary>
    public const string VisualName = "Visual";

    static MeshInstance3D Box(VehicleDef def, Color tint) =>
        new() { Mesh = new BoxMesh { Size = new Vector3(def.HalfWidth * 2, def.Height, def.HalfLength * 2) }, Position = new Vector3(0, def.Height / 2, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = tint } };

    /// <summary>Colour variant of a bike: the model's paint material takes the albedo of the variant's own material (Green_Motor to Red_Motor and so on).</summary>
    static void PaintVariant(Node n, VehicleDef def, byte colour)
    {
        if (def.Paints.Count == 0) return;
        var want = def.Paints[colour % def.Paints.Count];
        Color? tint = null;
        foreach (var mi in n.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
            for (var i = 0; i < mi.Mesh?.GetSurfaceCount(); i++)
                if (mi.Mesh!.SurfaceGetMaterial(i) is BaseMaterial3D bm && bm.ResourceName == want) tint = bm.AlbedoColor;
        if (tint == null) { GD.PrintErr($"vehicle {def.Id}: paint material {want} not found"); return; }
        foreach (var mi in n.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
            for (var i = 0; i < mi.Mesh?.GetSurfaceCount(); i++)
                if (mi.Mesh!.SurfaceGetMaterial(i) is BaseMaterial3D bm && bm.ResourceName == def.PaintMaterial && bm.Duplicate() is BaseMaterial3D copy)
                {
                    copy.AlbedoColor = tint.Value;
                    mi.SetSurfaceOverrideMaterial(i, copy);
                }
    }

    static void Paint(Node n, Color tint)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null)
            for (var i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                if (mi.Mesh.SurfaceGetMaterial(i) is BaseMaterial3D bm && bm.Duplicate() is BaseMaterial3D copy)
                {
                    copy.AlbedoColor = tint;
                    mi.SetSurfaceOverrideMaterial(i, copy);
                }
        foreach (var c in n.GetChildren()) Paint(c, tint);
    }
}

using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Client visual of a vehicle: the Kenney Car Kit sedan with its body material tinted by the colour index, or a bike model (vehicles.json) with its paint material swapped for one of the model's own colours.</summary>
public static class CarFx
{
    const string Model = "kenney-car-kit/sedan.glb";
    /// <summary>The Kenney sedan is 1.5 x 2.55 m, small next to the 1.8 m player; scaled to a plausible car and the server's box (2.2 x 4.3 m).</summary>
    const float Scale = 1.7f;

    static readonly Dictionary<string, Node3D?> Templates = new();

    /// <summary>Parses a vehicle model into the template cache ahead of its first use (#170, T2-18).</summary>
    public static void Warm(string model)
    {
        if (ShotFx.Enabled) Load(model);
    }

    static Node3D? Load(string model)
    {
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
        if (!ShotFx.Enabled) return root;
        if (def.Model != "")
        {
            if (Load(def.Model) is { } mt)
            {
                var m = (Node3D)mt.Duplicate();
                m.Position = new Vector3(def.ModelOffset[0], def.ModelOffset[1], def.ModelOffset[2]);
                var fit = new Node3D { Scale = Vector3.One * def.ModelScale, RotationDegrees = new Vector3(0, def.ModelYawDeg, 0) };
                fit.AddChild(m);
                root.AddChild(fit);
                if (def.Tint is { } fixedTint)
                {
                    var c = new Color(fixedTint[0], fixedTint[1], fixedTint[2]);
                    foreach (var n in m.FindChildren("body", "Node3D", true, false)) Paint(n, c);
                }
                else PaintVariant(m, def, colour);
            }
            else root.AddChild(Box(def, new Color(0.6f, 0.6f, 0.6f)));
            return root;
        }
        if (def.Medium == VehicleMedium.Boat)
        {
            // no model file for a boat: a primitive hull and cabin (no licence involved)
            var hull = def.Tint is { } ht ? new Color(ht[0], ht[1], ht[2]) : new Color(0.32f, 0.36f, 0.28f);
            root.AddChild(Hull(def, hull));
            return root;
        }
        if (def.Tint is { } tint0)
        {
            // no model for this type: primitive meshes (a body and four wheels), in its one fixed colour
            root.AddChild(Primitive(def, new Color(tint0[0], tint0[1], tint0[2])));
            return root;
        }
        var pal = VehicleColours.Palette[colour % VehicleColours.Palette.Length];
        var tint = new Color(pal.R, pal.G, pal.B);
        if (Load(Model) is { } t)
        {
            var m = (Node3D)t.Duplicate();
            m.Scale = Vector3.One * Scale;
            m.RotationDegrees = new Vector3(0, 180, 0); // glTF cars face +Z, the game's forward is -Z
            root.AddChild(m);
            foreach (var n in m.FindChildren("body", "Node3D", true, false)) Paint(n, tint);
        }
        else root.AddChild(Box(def, tint));
        return root;
    }

    /// <summary>A vehicle with no model file (the quad bike): the body box with four wheel cylinders at its sides.</summary>
    static Node3D Primitive(VehicleDef def, Color tint)
    {
        var n = new Node3D();
        n.AddChild(Box(def, tint));
        var wheel = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.07f, 0.07f) };
        const float r = 0.3f;
        foreach (var side in new[] { -1f, 1f })
        foreach (var front in new[] { -1f, 1f })
            n.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.2f },
                Position = new Vector3(side * (def.HalfWidth + 0.05f), r, front * def.HalfLength * 0.7f),
                RotationDegrees = new Vector3(0, 0, 90),
                MaterialOverride = wheel,
            });
        return n;
    }

    /// <summary>A boat with no model file: a hull box at the waterline and a cabin box on it.</summary>
    static Node3D Hull(VehicleDef def, Color tint)
    {
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(def.HalfWidth * 2, 0.8f, def.HalfLength * 2) }, Position = new Vector3(0, 0.2f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = tint } });
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(def.HalfWidth * 1.3f, 0.7f, def.HalfLength * 0.8f) }, Position = new Vector3(0, 1.1f, def.HalfLength * 0.2f), MaterialOverride = new StandardMaterial3D { AlbedoColor = tint.Darkened(0.25f) } });
        return n;
    }

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

using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>Client visual of a car: the Kenney Car Kit sedan with its body material tinted by the car's colour index.</summary>
public static class CarFx
{
    const string Model = "kenney-car-kit/sedan.glb";
    /// <summary>The Kenney sedan is 1.5 x 2.55 m, small next to the 1.8 m player; scaled to a plausible car and the server's box (2.2 x 4.3 m).</summary>
    const float Scale = 1.7f;
    static Node3D? _template;
    static bool _tried;

    static Node3D? Template()
    {
        if (_tried) return _template;
        _tried = true;
        var env = OS.GetEnvironment("SUCHOHRAD_ASSETS");
        var dir = string.IsNullOrEmpty(env) ? ProjectSettings.GlobalizePath("res://") + "../../assets" : env;
        var path = Path.GetFullPath(Path.Combine(dir, Model));
        if (!File.Exists(path)) { GD.PrintErr($"car model not found: {path}; using a box"); return null; }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok) { GD.PrintErr($"car model failed to load: {path}"); return null; }
        return _template = doc.GenerateScene(state) as Node3D;
    }

    /// <summary>A car node (origin at the ground, facing -Z) under <paramref name="world"/>; headless clients get an empty node.</summary>
    public static Node3D Make(Node world, byte colour)
    {
        var root = new Node3D { Name = "Car" };
        world.AddChild(root);
        if (!ShotFx.Enabled) return root;
        var pal = VehicleColours.Palette[colour % VehicleColours.Palette.Length];
        var tint = new Color(pal.R, pal.G, pal.B);
        if (Template() is { } t)
        {
            var m = (Node3D)t.Duplicate();
            m.Scale = Vector3.One * Scale;
            m.RotationDegrees = new Vector3(0, 180, 0); // glTF cars face +Z, the game's forward is -Z
            root.AddChild(m);
            foreach (var n in m.FindChildren("body", "Node3D", true, false)) Paint(n, tint);
        }
        else
            root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.2f, 4.3f) }, Position = new Vector3(0, 0.75f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = tint } });
        return root;
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

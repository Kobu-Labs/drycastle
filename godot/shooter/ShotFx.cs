using Godot;

namespace Suchohrad.Shooter;

/// <summary>Placeholder shot effects: a thin tracer line and a small muzzle flash, both gone within 0.1 s. Skipped on headless runs.</summary>
public static class ShotFx
{
    public static bool Enabled => DisplayServer.GetName() != "headless";

    static StandardMaterial3D Glow(Color c) => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c };

    public static void Spawn(Node3D world, Vector3 from, Vector3 to, bool melee)
    {
        if (!Enabled || !GodotObject.IsInstanceValid(world)) return;
        var dir = to - from;
        var len = dir.Length();
        if (len < 0.01f) return;
        if (!melee)
        {
            var tracer = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.02f, len), Material = Glow(new Color(1f, 0.9f, 0.4f)) } };
            world.AddChild(tracer);
            var up = Mathf.Abs(dir.Normalized().Y) > 0.99f ? Vector3.Right : Vector3.Up;
            tracer.LookAtFromPosition(from + dir * 0.5f, to, up);
            Expire(world, tracer, 0.08f);
        }
        var flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f, Material = Glow(new Color(1f, 0.7f, 0.2f)) }, Position = from };
        world.AddChild(flash);
        Expire(world, flash, 0.05f);
    }

    static void Expire(Node3D world, Node n, float seconds) => world.GetTree().CreateTimer(seconds).Timeout += n.QueueFree;
}

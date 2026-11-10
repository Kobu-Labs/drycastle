using Godot;

namespace Suchohrad.Shooter;

/// <summary>Per-frame smoke puff spawner trailing a rocket node; puffs shrink away over 0.5 s.</summary>
public partial class RocketTrail : Node3D
{
    double _acc;
    public override void _Process(double delta)
    {
        _acc += delta;
        if (_acc < 0.03) return;
        _acc = 0;
        var puff = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, Material = RocketFx.Unshaded(new Color(0.75f, 0.75f, 0.75f, 0.7f)) },
            Position = GlobalPosition,
        };
        GetParent().GetParent().AddChild(puff); // the world, not the moving rocket
        var tw = puff.CreateTween();
        tw.TweenProperty(puff, "scale", Vector3.One * 0.1f, 0.5f);
        tw.TweenCallback(Callable.From(puff.QueueFree));
    }
}

/// <summary>Placeholder rocket and explosion effects (capsule with a trail, expanding orange sphere). Skipped on headless runs.</summary>
public static class RocketFx
{
    public static StandardMaterial3D Unshaded(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c,
        Transparency = c.A < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    /// <summary>Builds the rocket node (a capsule lying along -Z) with a trail under the client's world.</summary>
    public static Node3D Make(Node3D world)
    {
        var node = new Node3D { Name = "Rocket" };
        if (ShotFx.Enabled)
        {
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.08f, Height = 0.7f, Material = Unshaded(new Color(0.35f, 0.4f, 0.3f)) },
                RotationDegrees = new Vector3(90, 0, 0),
            });
            node.AddChild(new RocketTrail());
        }
        world.AddChild(node);
        return node;
    }

    public static void Explode(Node3D world, Vector3 at, float radius)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world)) return;
        var ball = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f, Material = Unshaded(new Color(1f, 0.55f, 0.1f, 0.6f)) }, Position = at, Scale = Vector3.One * 0.3f };
        world.AddChild(ball);
        var tw = ball.CreateTween();
        tw.TweenProperty(ball, "scale", Vector3.One * radius * 0.6f, 0.3f);
        tw.TweenCallback(Callable.From(ball.QueueFree));
    }
}

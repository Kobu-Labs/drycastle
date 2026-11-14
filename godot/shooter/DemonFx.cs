using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's fire, cheap and shared with the rocket's kit (<see cref="RocketFx"/>): the flames on its body while it turns and burns out, and the
/// fireball: a glowing ball with a fire trail and a flickering light, bursting like a small blast on impact. All skipped headless.
/// </summary>
public static class DemonFx
{
    sealed class Cache
    {
        public readonly SphereMesh Ball = new() { Radius = 0.22f, Height = 0.44f, RadialSegments = 12, Rings = 6, Material = Glow(new Color(1f, 0.55f, 0.12f)) };
        public readonly SphereMesh Core = new() { Radius = 0.12f, Height = 0.24f, RadialSegments = 8, Rings = 4, Material = Glow(new Color(1f, 0.95f, 0.7f)) };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    static StandardMaterial3D Glow(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 2.5f,
    };

    /// <summary>A flame emitter under <paramref name="parent"/> (hidden until <see cref="ParticleFx.Run"/>): <paramref name="size"/> scales the flames and how far they rise.</summary>
    public static CpuParticles3D? Flames(Node3D parent, int amount, float size, Vector3 at)
    {
        if (!ShotFx.Enabled) return null;
        var rc = RocketFx.C;
        var p = new CpuParticles3D
        {
            Mesh = rc.Fire, ColorRamp = rc.FireRamp, ScaleAmountCurve = rc.Shrink, LocalCoords = false, OneShot = false, Emitting = false, Visible = false,
            Direction = Vector3.Up, Spread = 35, Amount = amount, Lifetime = 0.9, InitialVelocityMin = 0.8f * size, InitialVelocityMax = 2.2f * size,
            Gravity = new Vector3(0, 2.5f * size, 0), ScaleAmountMin = 0.6f * size, ScaleAmountMax = 1.1f * size, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.45f * size, Position = at,
        };
        parent.AddChild(p);
        return p;
    }

    /// <summary>A fireball in flight (a node under the world): a glowing ball with a core, a light and a fire trail named "Trail" (<see cref="RocketFx.Retire"/> leaves it to thin out).</summary>
    public static Node3D MakeFireball(Node3D world)
    {
        var node = new Node3D { Name = "Fireball" };
        if (ShotFx.Enabled)
        {
            var c = C;
            node.AddChild(new MeshInstance3D { Mesh = c.Ball, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            node.AddChild(new MeshInstance3D { Mesh = c.Core, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            node.AddChild(new OmniLight3D { Name = "Light", LightColor = new Color(1f, 0.5f, 0.15f), LightEnergy = 3.5f, OmniRange = 9f, ShadowEnabled = false });
            var rc = RocketFx.C;
            var trail = new CpuParticles3D
            {
                Name = "Trail", Mesh = rc.Fire, ColorRamp = rc.FireRamp, ScaleAmountCurve = rc.Shrink, Amount = 36, Lifetime = 0.55, LocalCoords = false, Visible = false,
                Direction = Vector3.Up, Spread = 40, InitialVelocityMin = 0.2f, InitialVelocityMax = 0.9f, Gravity = new Vector3(0, 1.2f, 0),
                ScaleAmountMin = 0.5f, ScaleAmountMax = 0.9f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(trail);
            ParticleFx.Run(trail, true);
        }
        world.AddChild(node);
        return node;
    }

    /// <summary>
    /// The demon's flame stream: world-space particles shot along the node's -Z (the view turns it at the target each frame), reaching about <paramref name="rangeM"/>,
    /// fatter as they go (<paramref name="size"/> times). Hidden until <see cref="ParticleFx.Run"/>. Null headless.
    /// </summary>
    public static CpuParticles3D? Stream(Node3D parent, float rangeM, float size = 1f)
    {
        if (!ShotFx.Enabled) return null;
        const float life = 0.6f;
        var rc = RocketFx.C;
        var p = new CpuParticles3D
        {
            Name = "FlameStream", Mesh = rc.Fire, ColorRamp = rc.FireRamp, ScaleAmountCurve = rc.Grow, LocalCoords = false, OneShot = false, Emitting = false, Visible = false,
            Direction = Vector3.Forward, Spread = 5, Amount = 110, Lifetime = life, InitialVelocityMin = rangeM / life * 0.85f, InitialVelocityMax = rangeM / life * 1.05f,
            Gravity = new Vector3(0, 1.5f, 0), ScaleAmountMin = 0.45f * size, ScaleAmountMax = 0.8f * size, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.12f * size,
        };
        parent.AddChild(p);
        return p;
    }

    /// <summary>One shock ring of a roar: a glowing ring at <paramref name="at"/> that swells and fades in about 0.8 s, then frees itself.</summary>
    public static void RoarRing(Node3D world, Vector3 at, Vector3 up)
    {
        if (!ShotFx.Enabled || !world.IsInsideTree()) return;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = new Color(1f, 0.45f, 0.12f, 0.7f), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.92f, OuterRadius = 1f, Rings = 24, RingSegments = 6 }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        world.AddChild(ring);
        var forward = MathF.Abs(up.Dot(Vector3.Forward)) > 0.95f ? Vector3.Right : Vector3.Forward;
        ring.GlobalTransform = new Transform3D(Basis.LookingAt(forward, up), at); // the ring lies in the plane across its up: it blasts out along the surface
        ring.Scale = Vector3.One * 0.4f;
        var tw = ring.CreateTween().SetParallel();
        tw.TweenProperty(ring, "scale", Vector3.One * 7f, 0.8);
        tw.TweenProperty(mat, "albedo_color:a", 0f, 0.8);
        tw.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }

    /// <summary>A one-shot spray of blood under <paramref name="parent"/> (hidden until <see cref="BloodBurst"/>): dark red drops thrown along the node's -Z that fall back. Null headless.</summary>
    public static CpuParticles3D? Blood(Node3D parent)
    {
        if (!ShotFx.Enabled) return null;
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.55f, 0.02f, 0.02f) };
        var p = new CpuParticles3D
        {
            Name = "Blood", Mesh = new SphereMesh { Radius = 0.016f, Height = 0.032f, RadialSegments = 6, Rings = 3, Material = mat }, LocalCoords = false, OneShot = true, Emitting = false, Visible = false,
            Explosiveness = 1f, Amount = 28, Lifetime = 0.7, Direction = Vector3.Forward, Spread = 38, InitialVelocityMin = 0.8f, InitialVelocityMax = 2.4f,
            Gravity = new Vector3(0, -6f, 0), ScaleAmountMin = 0.6f, ScaleAmountMax = 1.6f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(p);
        return p;
    }

    /// <summary>One spray from <paramref name="at"/> along <paramref name="dir"/> (unit).</summary>
    public static void BloodBurst(CpuParticles3D p, Vector3 at, Vector3 dir)
    {
        dir = dir.Normalized();
        var up = MathF.Abs(dir.Y) > 0.97f ? Vector3.Forward : Vector3.Up;
        p.GlobalTransform = new Transform3D(Basis.LookingAt(dir, up), at);
        ParticleFx.Fire(p);
    }

    /// <summary>The flicker of a light (a fire's): a base energy with quick random swings, never dark.</summary>
    public static float Flicker(double t, float seed, float low, float high)
    {
        var a = MathF.Sin((float)t * 17.3f + seed) * 0.5f + MathF.Sin((float)t * 31.1f + seed * 2.3f) * 0.3f + MathF.Sin((float)t * 7.7f + seed * 5.1f) * 0.2f;
        return Mathf.Lerp(low, high, a * 0.5f + 0.5f);
    }
}

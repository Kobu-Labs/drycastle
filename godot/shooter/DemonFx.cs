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

    /// <summary>The flicker of a light (a fire's): a base energy with quick random swings, never dark.</summary>
    public static float Flicker(double t, float seed, float low, float high)
    {
        var a = MathF.Sin((float)t * 17.3f + seed) * 0.5f + MathF.Sin((float)t * 31.1f + seed * 2.3f) * 0.3f + MathF.Sin((float)t * 7.7f + seed * 5.1f) * 0.2f;
        return Mathf.Lerp(low, high, a * 0.5f + 0.5f);
    }
}

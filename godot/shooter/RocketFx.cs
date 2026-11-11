using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Rocket and explosion visuals. The rocket is a stubby body with a glowing nose, a flame at the tail, a small light
/// and a world-space smoke trail. The explosion is a flash sphere, an expanding fireball, a shockwave ring, 70 or so
/// fire, spark, smoke and debris particles and a light flash, scaled by the splash radius. All meshes, materials and
/// ramps are built once and shared; skipped on headless runs.
/// </summary>
public static class RocketFx
{
    public static StandardMaterial3D Unshaded(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c,
        Transparency = c.A < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    static StandardMaterial3D Glow(Color c, bool alpha = false)
    {
        var m = Unshaded(c);
        m.EmissionEnabled = true; m.Emission = new Color(c.R, c.G, c.B); m.EmissionEnergyMultiplier = 2f;
        if (alpha) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        return m;
    }

    /// <summary>Particle material: unshaded, takes the per-particle colour (the ramp) as albedo, alpha blended.</summary>
    static StandardMaterial3D Part(Color c, bool emissive = false)
    {
        var m = Unshaded(new Color(c.R, c.G, c.B, 0.99f));
        m.VertexColorUseAsAlbedo = true;
        if (emissive) { m.EmissionEnabled = true; m.Emission = c; m.EmissionEnergyMultiplier = 1.5f; }
        return m;
    }

    static Gradient Ramp(params (float At, Color C)[] stops)
    {
        var g = new Gradient();
        g.Offsets = stops.Select(s => s.At).ToArray();
        g.Colors = stops.Select(s => s.C).ToArray();
        return g;
    }

    static Curve Curve2(float from, float to)
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, from)); c.AddPoint(new Vector2(1, to));
        return c;
    }

    sealed class Cache
    {
        public readonly CapsuleMesh Body = new() { Radius = 0.12f, Height = 1.1f, Material = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.45f, 0.33f), Roughness = 0.6f } };
        public readonly SphereMesh Nose = new() { Radius = 0.14f, Height = 0.28f, Material = Glow(new Color(1f, 0.35f, 0.1f)) };
        public readonly SphereMesh Flame = new() { Radius = 0.16f, Height = 0.32f, Material = Glow(new Color(1f, 0.8f, 0.3f)) };
        public readonly SphereMesh Puff = new() { Radius = 0.25f, Height = 0.5f, RadialSegments = 8, Rings = 4, Material = Part(new Color(1, 1, 1)) };
        public readonly Gradient SmokeTrail = Ramp((0f, new Color(0.95f, 0.95f, 0.95f, 0.75f)), (1f, new Color(0.35f, 0.35f, 0.35f, 0f)));
        public readonly Curve Grow = Curve2(0.5f, 1.8f), Shrink = Curve2(1f, 0.1f);
        // explosion
        public readonly SphereMesh Ball = new() { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 };
        public readonly StandardMaterial3D FlashMat = Glow(new Color(1f, 0.97f, 0.8f));
        public readonly StandardMaterial3D FireballMat = Glow(new Color(1f, 0.5f, 0.1f, 0.8f), true);
        public readonly TorusMesh Ring = new() { InnerRadius = 0.9f, OuterRadius = 1f, Material = Glow(new Color(1f, 0.85f, 0.6f, 0.7f), true) };
        public readonly SphereMesh Fire = new() { Radius = 0.3f, Height = 0.6f, RadialSegments = 8, Rings = 4, Material = Part(new Color(1, 1, 1), true) };
        public readonly Gradient FireRamp = Ramp((0f, new Color(1f, 0.95f, 0.6f, 1f)), (0.35f, new Color(1f, 0.5f, 0.1f, 0.9f)), (1f, new Color(0.3f, 0.1f, 0.05f, 0f)));
        public readonly SphereMesh Spark = new() { Radius = 0.04f, Height = 0.08f, RadialSegments = 6, Rings = 3, Material = Part(new Color(1, 1, 1), true) };
        public readonly Gradient SparkRamp = Ramp((0f, new Color(1f, 1f, 0.7f, 1f)), (0.6f, new Color(1f, 0.55f, 0.15f, 1f)), (1f, new Color(0.6f, 0.2f, 0.05f, 0f)));
        public readonly SphereMesh Smoke = new() { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4, Material = Part(new Color(1, 1, 1)) };
        public readonly Gradient SmokeRamp = Ramp((0f, new Color(0.35f, 0.33f, 0.3f, 0.85f)), (0.5f, new Color(0.2f, 0.2f, 0.2f, 0.7f)), (1f, new Color(0.12f, 0.12f, 0.12f, 0f)));
        public readonly BoxMesh Debris = new() { Size = new Vector3(0.18f, 0.12f, 0.15f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.22f, 0.15f), Roughness = 1f } };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    /// <summary>Builds the rocket node (lying along -Z) under the client's world.</summary>
    public static Node3D Make(Node3D world)
    {
        var node = new Node3D { Name = "Rocket" };
        if (ShotFx.Enabled)
        {
            var c = C;
            node.AddChild(new MeshInstance3D { Mesh = c.Body, RotationDegrees = new Vector3(90, 0, 0) });
            node.AddChild(new MeshInstance3D { Mesh = c.Nose, Position = new Vector3(0, 0, -0.58f), Scale = new Vector3(1, 1, 1.3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            node.AddChild(new MeshInstance3D { Mesh = c.Flame, Position = new Vector3(0, 0, 0.62f), Scale = new Vector3(1, 1, 1.8f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            node.AddChild(new OmniLight3D { LightColor = new Color(1f, 0.6f, 0.2f), LightEnergy = 2f, OmniRange = 9f, Position = new Vector3(0, 0, 0.8f), ShadowEnabled = false });
            node.AddChild(new CpuParticles3D
            {
                Name = "Trail", Mesh = c.Puff, Amount = 60, Lifetime = 1.2, LocalCoords = false, Position = new Vector3(0, 0, 0.7f),
                Direction = Vector3.Zero, Spread = 20f, InitialVelocityMin = 0.2f, InitialVelocityMax = 0.8f, Gravity = new Vector3(0, 0.3f, 0),
                ColorRamp = c.SmokeTrail, ScaleAmountCurve = c.Grow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        world.AddChild(node);
        return node;
    }

    /// <summary>Takes the rocket away: the smoke trail stays in the world for a second so it thins out instead of vanishing.</summary>
    public static void Retire(Node3D world, Node3D rocket)
    {
        if (!GodotObject.IsInstanceValid(rocket)) return;
        if (GodotObject.IsInstanceValid(world) && rocket.GetNodeOrNull<CpuParticles3D>("Trail") is { } trail)
        {
            var at = trail.GlobalPosition;
            rocket.RemoveChild(trail);
            world.AddChild(trail);
            trail.GlobalPosition = at;
            trail.Emitting = false;
            ShotFx.Expire(world, trail, 1.4f);
        }
        rocket.QueueFree();
    }

    static CpuParticles3D Emitter(Node3D root, Mesh mesh, Gradient? ramp, int amount, double life, Vector3 dir, float spread, float vMin, float vMax, Vector3 gravity, float sMin, float sMax, Curve? curve = null)
    {
        var p = new CpuParticles3D
        {
            Mesh = mesh, Amount = amount, Lifetime = life, OneShot = true, Explosiveness = 0.95f, Emitting = true, LocalCoords = false,
            Direction = dir, Spread = spread, InitialVelocityMin = vMin, InitialVelocityMax = vMax, Gravity = gravity,
            ScaleAmountMin = sMin, ScaleAmountMax = sMax, ScaleAmountCurve = curve, ColorRamp = ramp,
            AngularVelocityMin = -180, AngularVelocityMax = 180, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(p);
        return p;
    }

    public static void Explode(Node3D world, Vector3 at, float radius)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var c = C;
        var root = new Node3D { Position = at };
        world.AddChild(root);
        var r = Mathf.Max(1f, radius);
        var off = GeometryInstance3D.ShadowCastingSetting.Off;

        // flash sphere (white-hot, gone in 0.12 s) and fireball (grows 0.35 s, fades)
        var flash = new MeshInstance3D { Mesh = c.Ball, MaterialOverride = c.FlashMat, Scale = Vector3.One * r * 0.15f, CastShadow = off };
        var fireball = new MeshInstance3D { Mesh = c.Ball, MaterialOverride = c.FireballMat, Scale = Vector3.One * r * 0.2f, CastShadow = off };
        var ring = new MeshInstance3D { Mesh = c.Ring, Scale = Vector3.One * r * 0.15f, CastShadow = off, Position = new Vector3(0, 0.15f, 0) };
        root.AddChild(flash); root.AddChild(fireball); root.AddChild(ring);
        var tf = flash.CreateTween().SetParallel();
        tf.TweenProperty(flash, "scale", Vector3.One * r * 0.5f, 0.12f);
        tf.TweenProperty(flash, "transparency", 1f, 0.12f);
        var tb = fireball.CreateTween().SetParallel();
        tb.TweenProperty(fireball, "scale", Vector3.One * r * 0.55f, 0.35f).SetEase(Tween.EaseType.Out);
        tb.TweenProperty(fireball, "transparency", 1f, 0.35f).SetDelay(0.1f);
        var tr = ring.CreateTween().SetParallel();
        tr.TweenProperty(ring, "scale", new Vector3(r * 1.1f, r * 1.1f, r * 1.1f), 0.3f).SetEase(Tween.EaseType.Out);
        tr.TweenProperty(ring, "transparency", 1f, 0.3f);

        var light = new OmniLight3D { LightColor = new Color(1f, 0.65f, 0.25f), LightEnergy = 10f, OmniRange = r * 3.5f, ShadowEnabled = false };
        root.AddChild(light);
        var tl = light.CreateTween();
        tl.TweenProperty(light, "light_energy", 0f, 0.35f);

        // 24 fire + 24 sparks + 16 smoke + 10 debris
        Emitter(root, c.Fire, c.FireRamp, 24, 0.7, Vector3.Up, 180f, 1f, r * 0.9f, new Vector3(0, 1.5f, 0), r * 0.8f, r * 1.4f, c.Grow);
        Emitter(root, c.Spark, c.SparkRamp, 24, 1.0, Vector3.Up, 180f, r * 1.0f, r * 2.4f, new Vector3(0, -9.8f, 0), 0.6f, 1.4f, c.Shrink);
        Emitter(root, c.Smoke, c.SmokeRamp, 16, 1.6, Vector3.Up, 60f, 0.8f, r * 0.45f, new Vector3(0, 1.2f, 0), r * 0.5f, r * 0.9f, c.Grow);
        Emitter(root, c.Debris, null, 10, 1.3, Vector3.Up, 70f, r * 0.8f, r * 1.9f, new Vector3(0, -14f, 0), 0.7f, 1.8f);

        ShotFx.Expire(world, root, 2.2f);
    }
}

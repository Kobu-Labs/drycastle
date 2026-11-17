using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Scud truck's visuals (GLOSSARY "Scud truck", "Scud missile"): the missile in flight with its flame and smoke trail, the launch smoke at the truck, the burnt ground a blast leaves, and
/// the grey box that stands in for the truck's model without the private pack. Built from primitives and the shared particle recipes of <see cref="RocketFx"/>; nothing is built on a
/// headless client (<see cref="ShotFx.Enabled"/>). The real models are the private pack's <c>scud</c> and <c>scud_missile</c>; every node name below is the model's.
/// </summary>
public static class ScudFx
{
    /// <summary>The flying missile's model in the private pack (nose along -Z). Without it a generated one is drawn.</summary>
    public const string MissileModel = "pack:synty-simple-military/scud_missile";

    /// <summary>The generated missile is this long and this wide, metres (the real one is about 11 m).</summary>
    public const float MissileLength = 11f, MissileRadius = 0.45f;

    sealed class Cache
    {
        public readonly CylinderMesh Body = new() { TopRadius = MissileRadius, BottomRadius = MissileRadius, Height = MissileLength * 0.8f, RadialSegments = 16, Material = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.58f, 0.45f), Roughness = 0.6f } };
        public readonly CylinderMesh Nose = new() { TopRadius = 0.04f, BottomRadius = MissileRadius, Height = MissileLength * 0.2f, RadialSegments = 16, Material = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.34f, 0.28f), Roughness = 0.5f } };
        public readonly BoxMesh Fin = new() { Size = new Vector3(0.06f, 1.5f, 1.4f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.32f, 0.26f), Roughness = 0.7f } };
        public readonly SphereMesh Flame = new() { Radius = 0.9f, Height = 1.8f, RadialSegments = 12, Rings = 6, Material = RocketFx.Unshaded(new Color(1f, 0.72f, 0.25f)) };
        public readonly SphereMesh Puff = new() { Radius = 1.2f, Height = 2.4f, RadialSegments = 8, Rings = 4, Material = RocketFx.Part(new Color(1, 1, 1)) };
        public readonly Gradient Trail = RocketFx.Ramp((0f, new Color(1f, 0.85f, 0.55f, 0.9f)), (0.12f, new Color(0.95f, 0.93f, 0.9f, 0.85f)), (1f, new Color(0.6f, 0.6f, 0.6f, 0f)));
        public readonly Gradient Launch = RocketFx.Ramp((0f, new Color(1f, 0.8f, 0.45f, 0.95f)), (0.15f, new Color(0.85f, 0.82f, 0.78f, 0.9f)), (1f, new Color(0.5f, 0.5f, 0.5f, 0f)));
        public readonly Curve Grow = RocketFx.Curve2(0.6f, 3.2f), GrowBig = RocketFx.Curve2(1.4f, 7f);
        public readonly StandardMaterial3D Grey = new() { AlbedoColor = new Color(0.36f, 0.38f, 0.33f), Roughness = 0.85f };
        public readonly StandardMaterial3D Dark = new() { AlbedoColor = new Color(0.08f, 0.08f, 0.08f), Roughness = 0.95f };
        public readonly StandardMaterial3D Rack = new() { AlbedoColor = new Color(0.27f, 0.3f, 0.24f), Roughness = 0.8f };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    // ---- the missile ----

    /// <summary>A rocket lying along -Z, nose first, <see cref="MissileLength"/> long, centred on its middle: a body, a nose cone and four fins.</summary>
    public static Node3D GeneratedMissile()
    {
        var c = C;
        var root = new Node3D { Name = "ScudRocket" };
        root.AddChild(new MeshInstance3D { Mesh = c.Body, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0, MissileLength * 0.1f) });
        root.AddChild(new MeshInstance3D { Mesh = c.Nose, RotationDegrees = new Vector3(-90, 0, 0), Position = new Vector3(0, 0, -MissileLength * 0.4f) });
        for (var i = 0; i < 4; i++)
        {
            var holder = new Node3D { RotationDegrees = new Vector3(0, 0, 45 + 90 * i) };
            holder.AddChild(new MeshInstance3D { Mesh = c.Fin, Position = new Vector3(0, MissileRadius + 0.5f, MissileLength * 0.4f) });
            root.AddChild(holder);
        }
        return root;
    }

    /// <summary>The missile's body: the pack's model when the private pack has it, else the generated one. Always along -Z.</summary>
    public static Node3D MissileBody()
    {
        if (CarFx.PackTemplate(MissileModel) is { } template) return (Node3D)template.Duplicate();
        return GeneratedMissile();
    }

    /// <summary>
    /// The flying missile under <paramref name="world"/>: its body, a bright flame at the tail, a light and a smoke trail that stays in the world. Position it and turn it nose along -Z
    /// (yaw and pitch of <see cref="HitMath.Forward"/>). A headless client gets an empty node.
    /// </summary>
    public static Node3D MakeMissile(Node3D world)
    {
        var node = new Node3D { Name = "ScudMissile" };
        if (ShotFx.Enabled)
        {
            var c = C;
            node.AddChild(MissileBody());
            node.AddChild(new MeshInstance3D { Mesh = c.Flame, Position = new Vector3(0, 0, MissileLength * 0.5f + 0.8f), Scale = new Vector3(1, 1, 2.6f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            node.AddChild(new OmniLight3D { LightColor = new Color(1f, 0.65f, 0.25f), LightEnergy = 6f, OmniRange = 70f, Position = new Vector3(0, 0, MissileLength * 0.5f + 2f), ShadowEnabled = false });
            node.AddChild(new CpuParticles3D
            {
                Name = "Trail", Mesh = c.Puff, Amount = 500, Lifetime = 7.0, LocalCoords = false, Position = new Vector3(0, 0, MissileLength * 0.5f + 1.2f),
                Direction = Vector3.Zero, Spread = 25f, InitialVelocityMin = 0.5f, InitialVelocityMax = 3f, Gravity = new Vector3(0, 0.4f, 0),
                ColorRamp = c.Trail, ScaleAmountCurve = c.Grow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        world.AddChild(node);
        return node;
    }

    /// <summary>The missile is gone (it landed): its smoke trail stays in the world for the rest of its life, thinning out.</summary>
    public static void Retire(Node3D world, Node3D missile)
    {
        if (!GodotObject.IsInstanceValid(missile)) return;
        if (GodotObject.IsInstanceValid(world) && missile.GetNodeOrNull<CpuParticles3D>("Trail") is { } trail)
        {
            var at = trail.GlobalPosition;
            missile.RemoveChild(trail);
            world.AddChild(trail);
            trail.GlobalPosition = at;
            trail.Emitting = false;
            ShotFx.Expire(world, trail, 8f);
        }
        missile.QueueFree();
    }

    // ---- the launch ----

    /// <summary>A continuous exhaust at the rack's foot for the ignition: hot white smoke billowing out and rising. Hidden until <see cref="ParticleFx.Run"/> turns it on (the particle rule).</summary>
    public static CpuParticles3D IgnitionSmoke(Node3D parent, Vector3 local)
    {
        var c = C;
        var p = new CpuParticles3D
        {
            Name = "ScudIgnition", Mesh = c.Puff, Amount = 90, Lifetime = 3.5, Emitting = false, Visible = false, LocalCoords = false, Position = local,
            Direction = new Vector3(0, 0.4f, 1f), Spread = 70f, InitialVelocityMin = 3f, InitialVelocityMax = 11f, Gravity = new Vector3(0, 1.2f, 0),
            ColorRamp = c.Launch, ScaleAmountCurve = c.GrowBig, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(p);
        return p;
    }

    /// <summary>The launch itself: a flash of flame and a great cloud of smoke around the rack's foot that hangs for ten seconds.</summary>
    public static void LaunchBurst(Node3D world, Vector3 at)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var c = C;
        var root = new Node3D { Name = "ScudLaunch" };
        world.AddChild(root);
        root.GlobalPosition = at;
        var cloud = new CpuParticles3D
        {
            Mesh = c.Puff, Amount = 140, Lifetime = 10.0, OneShot = true, Explosiveness = 0.85f, Emitting = true, LocalCoords = false,
            Direction = Vector3.Up, Spread = 85f, InitialVelocityMin = 4f, InitialVelocityMax = 16f, Gravity = new Vector3(0, 0.25f, 0),
            ColorRamp = c.Launch, ScaleAmountCurve = c.GrowBig, ScaleAmountMin = 1f, ScaleAmountMax = 1.8f,
            AngularVelocityMin = -40, AngularVelocityMax = 40, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(cloud);
        var flash = new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 12f, OmniRange = 120f, ShadowEnabled = false };
        root.AddChild(flash);
        var tween = flash.CreateTween();
        tween.TweenProperty(flash, "light_energy", 0f, 1.6f);
        ShotFx.Expire(world, root, 12f);
    }

    // ---- the blast ----

    static readonly Queue<Node3D> Scorches = new();
    const int MaxScorches = 4;

    /// <summary>
    /// The burnt ground of a blast: a dark disc projected onto the terrain (a decal, so it lies on the slope), kept until the round ends or four more blasts have been scorched.
    /// </summary>
    public static void Scorch(Node3D world, Vector3 at, float radius)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var tex = new GradientTexture2D
        {
            Width = 128, Height = 128, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
            Gradient = RocketFx.Ramp((0f, new Color(0.02f, 0.02f, 0.02f, 0.92f)), (0.55f, new Color(0.05f, 0.04f, 0.03f, 0.8f)), (1f, new Color(0.08f, 0.06f, 0.04f, 0f))),
        };
        var decal = new Decal { Name = "ScudScorch", Size = new Vector3(radius * 2f, 60f, radius * 2f), TextureAlbedo = tex, CullMask = 1, UpperFade = 0.3f, LowerFade = 0.3f };
        world.AddChild(decal);
        decal.GlobalPosition = at;
        Scorches.Enqueue(decal);
        while (Scorches.Count > MaxScorches)
        {
            var old = Scorches.Dequeue();
            if (GodotObject.IsInstanceValid(old)) old.QueueFree();
        }
    }

    /// <summary>A new round clears the scorches.</summary>
    public static void ClearScorches()
    {
        while (Scorches.Count > 0)
        {
            var s = Scorches.Dequeue();
            if (GodotObject.IsInstanceValid(s)) s.QueueFree();
        }
    }

    // ---- the truck without its model ----

    /// <summary>
    /// A grey box of the Scud truck for a client without the private pack: two half cabs, the chassis, eight wheels and a rack on the rear hinge with a rocket on it, under the same node names the real
    /// model has (<c>hull</c>, <c>wheel_l1..l4</c>, <c>wheel_r1..r4</c> with the front two axles under <c>wheel_lN_steer</c> / <c>wheel_rN_steer</c>, <c>rack</c> > <c>missile</c>), so the animation works
    /// either way. The numbers are the model's (docs/research/scud-models-facts.md): 3.13 x 11.9 m, axles at z -3.539, -1.38, 1.814 and 3.97, the rack's pivot at [0, 2.62, 3.634], the missile's centre
    /// 0.477 up and 3.27 ahead of the pivot while the rack lies.
    /// </summary>
    public static Node3D GreyTruck(VehicleDef def)
    {
        var c = C;
        var root = new Node3D { Name = "ScudGreyBox" };
        var hull = new Node3D { Name = "hull" };
        root.AddChild(hull);
        var hw = def.HalfWidth; var hl = def.HalfLength;
        hull.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(hw * 1.7f, 0.8f, hl * 2f - 1.6f) }, Position = new Vector3(0, 1.5f, 0.3f), MaterialOverride = c.Grey });
        foreach (var side in new[] { -1f, 1f }) // the two half cabs, the rocket's nose between them
            hull.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.3f, 1.6f, 2.4f) }, Position = new Vector3(side * 0.9f, 2.1f, -4.75f), MaterialOverride = c.Grey, Name = side < 0 ? "cab_l" : "cab_r" });
        var axles = new[] { -3.539f, -1.38f, 1.814f, 3.97f };
        for (var a = 0; a < axles.Length; a++)
            foreach (var (side, tag) in new[] { (-1f, "l"), (1f, "r") })
            {
                var wheel = new MeshInstance3D
                {
                    Name = "wheel_" + tag + (a + 1), Mesh = new CylinderMesh { TopRadius = 0.736f, BottomRadius = 0.736f, Height = 0.44f }, RotationDegrees = new Vector3(0, 0, 90), MaterialOverride = c.Dark,
                };
                var at = new Vector3(side * 1.336f, 0.736f, axles[a]);
                if (a < 2) // the front two axles steer: the wheel hangs under a parent at its centre that turns about Y
                {
                    var steer = new Node3D { Name = "wheel_" + tag + (a + 1) + "_steer", Position = at };
                    steer.AddChild(wheel);
                    root.AddChild(steer);
                }
                else { wheel.Position = at; root.AddChild(wheel); }
            }
        var rack = new Node3D { Name = "rack", Position = new Vector3(0, 2.62f, 3.634f) };
        rack.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.4f, 6.84f) }, Position = new Vector3(0, 0.2f, -3.42f), MaterialOverride = c.Rack });
        var missile = new Node3D { Name = "missile", Position = new Vector3(0, 0.477f, -3.27f) };
        missile.AddChild(GeneratedMissile());
        rack.AddChild(missile);
        hull.AddChild(rack);
        return root;
    }
}

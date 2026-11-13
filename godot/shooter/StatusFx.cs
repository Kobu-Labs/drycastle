using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The looks of the player statuses, cheap: flames on a burning player (one CPU emitter on the body, the rocket blast's fire mesh and
/// ramp, only while burning and near), and the golden burst where a blessing saved a player (a short ring of motes and a light, freed after a
/// second). Both are skipped headless (<see cref="ShotFx.Enabled"/>).
/// </summary>
public static class StatusFx
{
    const float BurstSeconds = 1.2f;

    sealed class Cache
    {
        public readonly Gradient Gold = RocketFx.Ramp((0f, new Color(1f, 0.98f, 0.75f, 1f)), (0.4f, new Color(1f, 0.82f, 0.3f, 0.9f)), (1f, new Color(1f, 0.7f, 0.15f, 0f)));
        public readonly SphereMesh Mote = new() { Radius = 0.05f, Height = 0.1f, RadialSegments = 6, Rings = 3, Material = RocketFx.Part(new Color(1, 1, 1), true) };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    /// <summary>Flames on a body: an emitter at the player's waist, hidden until it runs. Null headless.</summary>
    public static CpuParticles3D? MakeFlames(Node3D body)
    {
        if (!ShotFx.Enabled) return null;
        var rc = RocketFx.C;
        var p = new CpuParticles3D
        {
            Mesh = rc.Fire, ColorRamp = rc.FireRamp, ScaleAmountCurve = rc.Shrink, LocalCoords = false, OneShot = false, Emitting = false,
            Visible = false, Direction = Vector3.Up, Spread = 20, Amount = 14, Lifetime = 0.7, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.4f,
            Gravity = new Vector3(0, 2f, 0), ScaleAmountMin = 0.4f, ScaleAmountMax = 0.7f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, 0.9f, 0),
        };
        body.AddChild(p);
        return p;
    }

    /// <summary>Starts or stops the flames (<see cref="ParticleFx.Run"/>); what is in the air dies out.</summary>
    public static void SetBurning(CpuParticles3D? flames, bool on)
    {
        if (flames != null && GodotObject.IsInstanceValid(flames)) ParticleFx.Run(flames, on);
    }

    /// <summary>
    /// The golden burst of a blessing that saved someone, at their feet: motes rising around the body and a warm light that fades. Under reduce
    /// motion the motes stay (they are not a flash), the light does not pulse (it just holds, then goes).
    /// </summary>
    public static void GoldBurst(Node3D world, Vector3 feet)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var root = new Node3D { Position = feet };
        world.AddChild(root);
        var motes = new CpuParticles3D
        {
            Mesh = C.Mote, ColorRamp = C.Gold, Amount = 36, Lifetime = 0.9, OneShot = true, Explosiveness = 0.9f, Emitting = true, LocalCoords = false,
            Direction = Vector3.Up, Spread = 60, InitialVelocityMin = 1.5f, InitialVelocityMax = 4f, Gravity = new Vector3(0, -1f, 0),
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.5f, Position = new Vector3(0, 0.9f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(motes);
        var light = new OmniLight3D { LightColor = new Color(1f, 0.85f, 0.4f), LightEnergy = 3f, OmniRange = 6f, ShadowEnabled = false, Position = new Vector3(0, 1f, 0) };
        root.AddChild(light);
        var tl = light.CreateTween();
        tl.TweenProperty(light, "light_energy", 0f, BurstSeconds - 0.2f).SetDelay(GameSettings.Current.ReduceMotion ? 0.3f : 0.1f);
        ShotFx.Expire(world, root, BurstSeconds);
    }
}

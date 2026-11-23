using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Status;
using Drycastle.Shooter.Weapons;

namespace Drycastle.Shooter.Wildlife;

/// <summary>
/// The looks of pets: the puff of a pet that died of a hit (it leaves no corpse): a few small grey clouds that swell a little and fade, at the spot, freed after
/// a second. A one-shot emitter like <see cref="StatusFx.GoldBurst"/>, skipped headless (<see cref="ShotFx.Enabled"/>); under reduce motion the clouds appear and
/// fade where they are instead of drifting out.
/// </summary>
public static class PetFx
{
    const float PuffSeconds = 0.7f;
    const int Clouds = 6;

    sealed class Cache
    {
        public readonly Gradient Fade = RocketFx.Ramp((0f, new Color(0.95f, 0.95f, 0.95f, 0.9f)), (1f, new Color(0.78f, 0.78f, 0.8f, 0f)));
        public readonly Curve Swell = RocketFx.Curve2(0.6f, 1.3f);
        public readonly SphereMesh Cloud = new() { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4, Material = RocketFx.Part(new Color(1, 1, 1)) };
    }

    static Cache? _c;
    static Cache C => _c ??= new Cache();

    /// <summary>A puff at <paramref name="at"/> (the middle of the body) for a pet whose body is <paramref name="bodyHeight"/> m tall: bigger clouds for a bigger pet.</summary>
    public static void Puff(Node3D world, Vector3 at, float bodyHeight)
    {
        if (!ShotFx.Enabled || !GodotObject.IsInstanceValid(world) || !world.IsInsideTree()) return;
        var cloud = Mathf.Max(bodyHeight * 0.25f, 0.05f); // a cloud's width: a quarter of the pet's height
        var drift = GameSettings.Current.ReduceMotion ? 0f : 1f;
        var p = new CpuParticles3D
        {
            Mesh = C.Cloud, ColorRamp = C.Fade, ScaleAmountCurve = C.Swell, Amount = Clouds, Lifetime = PuffSeconds, OneShot = true, Explosiveness = 1f,
            Emitting = true, LocalCoords = false, Direction = Vector3.Up, Spread = 180, Gravity = new Vector3(0, 0.3f * drift, 0),
            InitialVelocityMin = cloud * 1.5f * drift, InitialVelocityMax = cloud * 3f * drift, ScaleAmountMin = cloud * 0.8f, ScaleAmountMax = cloud * 1.3f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = cloud * 0.6f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = at,
        };
        world.AddChild(p);
        ShotFx.Expire(world, p, PuffSeconds + 0.3f);
    }
}

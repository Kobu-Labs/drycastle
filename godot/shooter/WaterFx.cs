using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Splashes: a short burst of droplets (one-shot CPU particles, freed after about a second) and the "water.splash" sound
/// at the surface point. Droplets are dropped with reduce motion; the sound stays. Spawned only on events (entering or
/// leaving the water, a bullet or rocket meeting the surface), never per frame.
/// </summary>
public static class WaterFx
{
    const double Lifetime = 1.2;
    static StandardMaterial3D? _dropMat;
    static SphereMesh? _dropMesh; // one droplet mesh for every splash (#169)

    /// <summary>A splash at <paramref name="at"/> (on the surface). <paramref name="size"/> scales the burst: 1 for a body, about 0.4 for a bullet.</summary>
    public static void Splash(Node3D world, Vector3 at, float size = 1f)
    {
        Sfx.Play3D("water.splash", at, size >= 0.8f ? 0f : -6f);
        if (GameSettings.Current.ReduceMotion || !world.IsInsideTree()) return;
        if (Sfx.ListenerDistance(at) > EffectCull.EffectRange) return; // the droplets are too small to see from far off; the sound still plays
        _dropMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.94f, 1f), Roughness = 0.2f };
        _dropMesh ??= new SphereMesh { Radius = 0.04f, Height = 0.08f, RadialSegments = 6, Rings = 3, Material = _dropMat };
        var burst = new CpuParticles3D
        {
            OneShot = true, Explosiveness = 1f, Amount = size >= 0.8f ? 28 : 10, Lifetime = Lifetime,
            Direction = Vector3.Up, Spread = 70f, InitialVelocityMin = 2f * size, InitialVelocityMax = 4.5f * size,
            Gravity = new Vector3(0, -9.8f, 0), Mesh = _dropMesh,
        };
        world.AddChild(burst);
        burst.GlobalPosition = at;
        burst.Emitting = true;
        world.GetTree().CreateTimer(Lifetime + 0.2).Timeout += burst.QueueFree;
    }
}

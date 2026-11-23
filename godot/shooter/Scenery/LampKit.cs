using Godot;

namespace Drycastle.Shooter.Scenery;

/// <summary>
/// The pieces every lamp of the game is made of, so a street lamp, a reflector stand and a vehicle headlamp look alike: a shaded emissive lens (shaded on purpose: an unshaded
/// material drops its emission and would never bloom, ADR 0030) and a real spot beam without shadow. Callers own the numbers (data) and when things are on.
/// </summary>
public static class LampKit
{
    /// <summary>A lit lens of <paramref name="colour"/>; drive <see cref="BaseMaterial3D.EmissionEnergyMultiplier"/> above <see cref="DayNightCycle.GlowThreshold"/> to make it bloom.</summary>
    public static StandardMaterial3D Lens(Color colour, float energy) =>
        new() { AlbedoColor = colour, EmissionEnabled = true, Emission = colour, EmissionEnergyMultiplier = energy };

    /// <summary>A spot light, off until the caller shows it. <paramref name="angleDeg"/> is the half angle; the beam runs along the node's -Z.</summary>
    public static SpotLight3D Beam(Transform3D at, Color colour, float range, float angleDeg, float energy) =>
        new() { Transform = at, SpotRange = range, SpotAngle = angleDeg, SpotAttenuation = 1f, LightColor = colour, LightEnergy = energy, ShadowEnabled = false, Visible = false };
}

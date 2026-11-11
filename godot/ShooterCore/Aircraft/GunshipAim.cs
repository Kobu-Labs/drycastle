namespace Suchohrad.ShooterCore;

/// <summary>Turret aim limits. The pilot's view angles are clamped to the turret's swing about the airframe, then to its pitch range.</summary>
public static class GunshipAim
{
    /// <summary>Wraps an angle to [-pi, pi].</summary>
    public static float Wrap(float a) => MathF.IEEERemainder(a, MathF.PI * 2f);

    /// <summary>World yaw and pitch (radians) the turret takes for a requested view, given the airframe heading.</summary>
    public static (float Yaw, float Pitch) Clamp(float aircraftYaw, float yaw, float pitch, GunshipDef def)
    {
        var rel = Wrap(yaw - aircraftYaw);
        var limit = def.YawLimitDeg * MathF.PI / 180f;
        rel = Math.Clamp(rel, -limit, limit);
        var pMin = def.PitchMinDeg * MathF.PI / 180f;
        var pMax = def.PitchMaxDeg * MathF.PI / 180f;
        return (aircraftYaw + rel, Math.Clamp(pitch, pMin, pMax));
    }
}

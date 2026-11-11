namespace Suchohrad.ShooterCore;

/// <summary>Aim offset a shot is fired with, in radians: pitch positive up, yaw positive left (the <see cref="HitMath.Forward"/> convention).</summary>
public readonly record struct RecoilKick(float PitchRad, float YawRad);

/// <summary>
/// Recoil of one player's trigger hand, shared by server and client. The offset is added on top of the mouse aim: every
/// hitscan shot is fired with the offset accumulated by the shots before it, then the shot adds its own kick.
/// Climb is <c>vertical_per_shot</c> per shot (capped by <c>max_climb</c>); sideways wander is the per-shot <c>pattern</c>
/// (cycled by the shot's index in the burst) or, without a pattern, <c>horizontal_sway</c> scaled by a deterministic value
/// from the player's seed. A burst is the time the trigger is held; releasing it restarts the pattern and the aim settles
/// back to zero at <c>recovery_rate</c> degrees per second. Godot-free and deterministic: time is passed in, and the same
/// seed and the same fire sequence give the same offsets on both sides.
/// </summary>
public sealed class RecoilState
{
    /// <summary>Same limit as the mouse look, so kick cannot flip the view over the pole.</summary>
    public const float MaxPitchRad = 1.55f;

    readonly ulong _seed;
    double _last = double.NaN;
    int _burst;
    float _pitch, _yaw;

    public RecoilState(ulong seed) { _seed = seed; }

    /// <summary>Current accumulated offset (radians), what the camera shows on top of the mouse aim.</summary>
    public float PitchRad => _pitch;
    public float YawRad => _yaw;

    public void Reset()
    {
        _pitch = _yaw = 0;
        _burst = 0;
        _last = double.NaN;
    }

    /// <summary>Advances recovery to <paramref name="now"/> (seconds). While the trigger is held nothing recovers; on release the burst restarts.</summary>
    public void Tick(double now, bool triggerHeld, RecoilDef recoil)
    {
        var dt = double.IsNaN(_last) ? 0 : Math.Max(0, now - _last);
        _last = now;
        if (triggerHeld) return;
        _burst = 0;
        var step = (float)(recoil.RecoveryRate * Deg2Rad * dt);
        if (step <= 0) return;
        _pitch = Settle(_pitch, step);
        _yaw = Settle(_yaw, step);
    }

    /// <summary>
    /// The offset this shot is fired with (before its own kick), then adds the shot's kick. <paramref name="multiplier"/> is
    /// <see cref="Multiplier"/> for the shooter's ADS and crouch state.
    /// </summary>
    public RecoilKick Shot(RecoilDef recoil, float multiplier)
    {
        var fired = new RecoilKick(_pitch, _yaw);
        var m = multiplier * Deg2Rad;

        var climb = recoil.VerticalPerShot * m;
        if (recoil.MaxClimb > 0) climb = Math.Min(climb, Math.Max(0, recoil.MaxClimb * Deg2Rad - _pitch));
        _pitch += Math.Max(0, climb);

        var sway = recoil.Pattern.Count > 0
            ? recoil.Pattern[_burst % recoil.Pattern.Count] * m
            : recoil.HorizontalSway * m * Unit(_seed, _burst);
        _yaw += sway;
        _burst++;
        return fired;
    }

    /// <summary>Recoil multiplier for the shooter's stance: ADS and crouch each scale the kick by their data value (1 = no change).</summary>
    public static float Multiplier(RecoilDef recoil, bool aiming, bool crouching) =>
        (aiming ? recoil.AdsMultiplier : 1f) * (crouching ? recoil.CrouchMultiplier : 1f);

    /// <summary>The final aim pitch (mouse plus offset) limited to the look range.</summary>
    public static float ClampPitch(float rad) => Math.Clamp(rad, -MaxPitchRad, MaxPitchRad);

    const float Deg2Rad = MathF.PI / 180f;

    static float Settle(float v, float step) => v > 0 ? Math.Max(0, v - step) : Math.Min(0, v + step);

    /// <summary>Deterministic value in [-1, 1] for shot <paramref name="n"/> of the stream <paramref name="seed"/> (SplitMix64).</summary>
    static float Unit(ulong seed, int n)
    {
        var z = seed + ((ulong)n + 1) * 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (float)((z >> 11) * (1.0 / (1UL << 53)) * 2 - 1);
    }
}

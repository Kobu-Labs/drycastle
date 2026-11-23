namespace Drycastle.ShooterCore;

/// <summary>
/// The ear ringing of a blast (#481): how loud it starts from the distance to the blast, and how long it takes to fade. Pure rules; <see cref="EarRing"/> holds the running level.
/// Distance is judged against the blast's damage radius (capped at <see cref="MaxRadiusM"/>): full level out to <see cref="FullFraction"/> of it, falling linearly to nothing at it.
/// </summary>
public static class EarRingRules
{
    /// <summary>Share of the damage radius inside which the ring is at full level.</summary>
    public const float FullFraction = 0.75f;
    /// <summary>A huge blast (the Scud's 150 m) rings only this far, so the effect stays a close-range one.</summary>
    public const float MaxRadiusM = 30f;
    /// <summary>Seconds from silence to the peak.</summary>
    public const float AttackSeconds = 0.1f;
    /// <summary>Fade length at full level, seconds.</summary>
    public const float FadeMaxSeconds = 4f;
    /// <summary>Fade length of the faintest audible ring, seconds.</summary>
    public const float FadeMinSeconds = 1f;
    /// <summary>Below this level nothing plays.</summary>
    public const float Floor = 0.02f;

    /// <summary>Ring level 0 to 1 at <paramref name="distance"/> metres from a blast of <paramref name="damageRadius"/> metres.</summary>
    public static float Level(float distance, float damageRadius)
    {
        var r = MathF.Min(damageRadius, MaxRadiusM);
        if (r <= 0 || distance >= r) return 0f;
        var full = r * FullFraction;
        if (distance <= full) return 1f;
        return 1f - (distance - full) / (r - full);
    }

    /// <summary>Seconds a ring of <paramref name="level"/> takes to fade out: the louder, the longer.</summary>
    public static float FadeSeconds(float level) => FadeMinSeconds + (FadeMaxSeconds - FadeMinSeconds) * Math.Clamp(level, 0f, 1f);

    /// <summary>Volume offset in dB for a ring level (0 dB at full, silent under <see cref="Floor"/>).</summary>
    public static float Db(float level) => level < Floor ? -80f : 20f * MathF.Log10(level);
}

/// <summary>
/// The running ring of the local player: it rises to a blast's level at once, then fades out. A blast that is not louder than the peak of the ring still going does nothing (no stack, no replay,
/// even after it has faded some); a louder one raises it again. Value type; the owner calls <see cref="Tick"/> every frame and sets the voice's volume from <see cref="Level"/>.
/// </summary>
public struct EarRing
{
    float _target, _rate, _decay, _peak;

    /// <summary>Current level 0 to 1; 0 when silent.</summary>
    public float Level { get; private set; }

    public readonly bool Active => Level > 0 || _target > 0;

    /// <summary>A blast of <paramref name="level"/> (see <see cref="EarRingRules.Level"/>). True when it started or raised the ring.</summary>
    public bool Hit(float level)
    {
        if (level < EarRingRules.Floor || level <= _peak) return false;
        _target = level; _peak = level;
        _rate = 1f / EarRingRules.AttackSeconds;
        _decay = level / EarRingRules.FadeSeconds(level);
        return true;
    }

    public void Tick(float dt)
    {
        if (!Active) return;
        Level = Ramp.MoveToward(Level, _target, _rate * dt);
        if (_target > 0 && Level >= _target) { _target = 0; _rate = _decay; }
        if (!Active) _peak = 0;
    }

    public void Clear() { Level = 0; _target = 0; _rate = 0; _decay = 0; _peak = 0; }
}

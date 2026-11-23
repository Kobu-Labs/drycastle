namespace Drycastle.ShooterCore.Wildlife;

/// <summary>How a client draws a flying animal (the Phoenix): which clip a state plays and how far it banks in a turn. Pure, so it is tested without a model.</summary>
public static class FlyerLook
{
    /// <summary>The largest bank angle (radians, about 23 degrees) and radians of bank per radian/s of turn: the Phoenix's slow circle (about 0.04 rad/s) reads as a lazy lean.</summary>
    public const float MaxBank = 0.4f, BankPerTurnRate = 7f;

    /// <summary>
    /// The clip of a flyer's state: Flying glides (<see cref="AnimalClip.Cruise"/>, or the hover <see cref="AnimalClip.Idle"/> while <paramref name="hover"/>), Falling tumbles, Crashed and Burning sit
    /// (<see cref="AnimalClip.Perched"/>), and so does the Dead one (it lies as ash). Null for a state a flyer is never in (the ground animals' states).
    /// </summary>
    public static AnimalClip? ClipOf(AnimalState state, bool hover) => state switch
    {
        AnimalState.Flying => hover ? AnimalClip.Idle : AnimalClip.Cruise,
        AnimalState.Falling => AnimalClip.Fall,
        AnimalState.Crashed or AnimalState.Burning or AnimalState.Dead => AnimalClip.Perched,
        _ => null,
    };

    /// <summary>Whether a state is one of the Phoenix's air-borne ones (its position is the body centre, not the ground).</summary>
    public static bool Airborne(AnimalState state) => state is AnimalState.Flying or AnimalState.Falling;

    /// <summary>The roll (radians, positive = left wing down, into a left turn: yaw grows to the left) of a turn at <paramref name="yawRate"/> radians/s.</summary>
    public static float Bank(float yawRate) => Math.Clamp(yawRate * BankPerTurnRate, -MaxBank, MaxBank);

    /// <summary>The shortest signed difference (radians) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static float AngleDelta(float from, float to)
    {
        var d = (to - from) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        else if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    /// <summary>One step of an exponential approach of <paramref name="current"/> to <paramref name="target"/> at <paramref name="rate"/> per second (frame-rate independent).</summary>
    public static float Approach(float current, float target, float rate, float dt) => current + (target - current) * (1f - MathF.Exp(-rate * dt));
}

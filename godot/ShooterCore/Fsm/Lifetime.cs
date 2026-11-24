namespace Drycastle.ShooterCore.Fsm;

/// <summary>
/// "Lives this long, then expires": the <c>_age += dt; if (_age &gt;= limit) Ended = Expired</c> pair that every world entity repeats. A value type holding only
/// the age; the limit is passed to <see cref="Tick"/> because it comes from data or a constructor, not from here. Exactly the hand-written arithmetic: the
/// age is a running float sum of the dts and expiry is <c>age &gt;= limit</c>, so a conversion changes no float result.
/// <code>
/// Lifetime _life;
/// if (_life.Tick(dt, _def.LifetimeSeconds)) Ended = WorldDespawnReason.Expired;
/// </code>
/// </summary>
public struct Lifetime
{
    /// <summary>Seconds lived: the sum of every dt given to <see cref="Tick"/>.</summary>
    public float Age { get; private set; }

    /// <summary>Adds <paramref name="dt"/> and says whether the age has reached <paramref name="limit"/> (and stays true on later ticks).</summary>
    public bool Tick(float dt, float limit)
    {
        Age += dt;
        return Age >= limit;
    }

    /// <summary>Starts the age over (a placement that begins the clock).</summary>
    public void Reset() => Age = 0;
}

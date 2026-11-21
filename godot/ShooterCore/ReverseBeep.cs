namespace Drycastle.ShooterCore;

/// <summary>
/// The clock of a vehicle's reversing beep (#432; vehicles.json "reverse_beep"): the backup alarm of the bigger vehicles. It beeps the moment the vehicle
/// backs up and again every <see cref="Cycle"/> s while it does (the sound is 0.55 s long, so beep, gap, beep). It runs out its cycle even when the vehicle
/// stops, so rocking back and forth cannot make it chatter. Client side only, from the drawn signed speed: nothing on the wire.
/// </summary>
public struct ReverseBeep
{
    /// <summary>Seconds from one beep to the next.</summary>
    public const float Cycle = 1.4f;
    /// <summary>Slower than this backwards (m/s) is not reversing: a roll back on a slope or a creep while braking does not beep.</summary>
    public const float MinSpeed = 1f;

    Countdown _cycle;

    /// <summary>True when a beep is due this tick. <paramref name="speed"/> is signed (m/s, negative backwards); <paramref name="active"/> false (nobody drives it, destroyed, out of earshot) holds the clock back.</summary>
    public bool Step(float speed, float dt, bool active)
    {
        _cycle.Tick(dt);
        if (!active || speed > -MinSpeed) return false;
        return _cycle.StartIfReady(Cycle);
    }
}

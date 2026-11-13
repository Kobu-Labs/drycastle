namespace Drycastle.ShooterCore;

/// <summary>
/// The render clock of remote entities (A-06): maps the local clock to the server's tick clock and gives the fractional tick to draw at,
/// <c>delay</c> ticks behind the server's present.
/// <para>
/// The server's present is estimated from every arrival, not re-anchored to the last one: each snapshot gives a sample of the clock offset
/// (its tick's time minus its arrival time, which carries the one-way delay), and the offset moves by <see cref="Gain"/> of the difference.
/// That filters the arrival jitter (the old clock re-anchored at every arrival, so each one stepped the drawn time by up to a third of a tick).
/// </para>
/// <para>
/// The drawn tick never moves backwards. A snapshot that arrives late moves the estimate back, and the drawn tick holds until the estimate
/// passes it again, instead of snapping back by the lateness (the old clock drew the entity two ticks earlier at each late arrival). A hold
/// is the freeze the buffer's end causes anyway; it is counted in <see cref="HoldEvents"/> and <see cref="MaxHold"/>.
/// </para>
/// </summary>
public sealed class RenderClock
{
    /// <summary>Weight of one arrival's sample in the offset (1 = the arrival-anchored clock of before this type).</summary>
    public const double Gain = 0.125;

    double _offset;
    double _drawn = double.NegativeInfinity;
    bool _synced;

    /// <summary>How many times the drawn tick held still because the estimate moved back (a late arrival).</summary>
    public int HoldEvents { get; private set; }
    /// <summary>The largest distance the estimate moved back behind the drawn tick, in ticks (the size of the worst hold, for the telemetry).</summary>
    public double MaxHold { get; private set; }

    bool _holding;

    /// <summary>Records that a snapshot of server tick <paramref name="tick"/> arrived at local time <paramref name="arrivalSec"/>.</summary>
    public void OnSnapshot(uint tick, double arrivalSec)
    {
        var sample = tick / (double)Wire.TickRate - arrivalSec;
        if (!_synced) { _offset = sample; _synced = true; return; }
        _offset += (sample - _offset) * Gain;
    }

    /// <summary>The fractional server tick to draw at local time <paramref name="nowSec"/>, <paramref name="delayTicks"/> behind the estimated present.</summary>
    public double RenderTick(double nowSec, double delayTicks)
    {
        var target = (nowSec + _offset) * Wire.TickRate - delayTicks;
        if (!_synced) return target;
        if (target >= _drawn)
        {
            _drawn = target;
            _holding = false;
            return target;
        }
        // the estimate moved back: hold the drawn tick, and count the hold once per episode
        if (!_holding) { HoldEvents++; _holding = true; }
        MaxHold = Math.Max(MaxHold, _drawn - target);
        return _drawn;
    }
}

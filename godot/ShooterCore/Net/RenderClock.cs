using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Net;

/// <summary>Where the render clock is: waiting for its first snapshot, following the estimate, or holding the drawn tick through a late arrival.</summary>
public enum RenderClockState : byte { Unsynced, Tracking, Holding }

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
    public static readonly StateMachineSpec<RenderClockState> Spec = StateMachineSpec<RenderClockState>
        .Define("Net.RenderClock", "The render clock of remote entities: unsynced until the first snapshot, then tracking the estimated server present, holding the drawn tick while a late snapshot has moved the estimate behind it.", RenderClockState.Unsynced)
        .Allow(RenderClockState.Unsynced, RenderClockState.Tracking)
        .Allow(RenderClockState.Tracking, RenderClockState.Holding)
        .Allow(RenderClockState.Holding, RenderClockState.Tracking)
        .Note(RenderClockState.Unsynced, "No snapshot yet: RenderTick returns the raw target and the drawn tick is not recorded.")
        .Note(RenderClockState.Tracking, "The drawn tick follows the estimate (it never moves backwards).")
        .Note(RenderClockState.Holding, "The estimate moved behind the drawn tick: the drawn tick stays put until the estimate passes it again. One episode counts once in HoldEvents.")
        .Build();

    /// <summary>Weight of one arrival's sample in the offset (1 = the arrival-anchored clock of before this type).</summary>
    public const double Gain = 0.125;

    double _offset;
    double _drawn = double.NegativeInfinity;
    readonly StateMachine<RenderClockState> _fsm = new(Spec);

    public RenderClockState State => _fsm.Current;

    /// <summary>How many times the drawn tick held still because the estimate moved back (a late arrival).</summary>
    public int HoldEvents { get; private set; }
    /// <summary>The largest distance the estimate moved back behind the drawn tick, in ticks (the size of the worst hold, for the telemetry).</summary>
    public double MaxHold { get; private set; }

    /// <summary>Records that a snapshot of server tick <paramref name="tick"/> arrived at local time <paramref name="arrivalSec"/>.</summary>
    public void OnSnapshot(uint tick, double arrivalSec)
    {
        var sample = tick / (double)Wire.TickRate - arrivalSec;
        if (_fsm.Is(RenderClockState.Unsynced)) { _offset = sample; _fsm.Enter(RenderClockState.Tracking); return; }
        _offset += (sample - _offset) * Gain;
    }

    /// <summary>The fractional server tick to draw at local time <paramref name="nowSec"/>, <paramref name="delayTicks"/> behind the estimated present.</summary>
    public double RenderTick(double nowSec, double delayTicks)
    {
        var target = (nowSec + _offset) * Wire.TickRate - delayTicks;
        if (_fsm.Is(RenderClockState.Unsynced)) return target;
        if (target >= _drawn)
        {
            _drawn = target;
            _fsm.Enter(RenderClockState.Tracking);
            return target;
        }
        // the estimate moved back: hold the drawn tick, and count the hold once per episode
        if (_fsm.Enter(RenderClockState.Holding)) HoldEvents++;
        MaxHold = Math.Max(MaxHold, _drawn - target);
        return _drawn;
    }
}

namespace Drycastle.ShooterCore.Weapons;

/// <summary>What the sniper hears of its own breath this tick. <see cref="BeatDb"/> is the heartbeat's level offset (louder as the breath runs out).</summary>
public readonly record struct BreathCue(bool Inhale, bool Exhale, bool ForcedExhale, bool Beat, float BeatDb);

/// <summary>
/// The sound of the breath hold, in place of a meter: an inhale when the hold begins, a heartbeat once less than <see cref="HeartbeatFrom"/> of the
/// breath is left (faster and louder to the end), a soft exhale on an early release and a loud one when the breath is forced out. Client only and
/// cosmetic: it reads <see cref="BreathState"/> after its tick and never changes it. Godot-free: time is passed in, seconds.
/// </summary>
public sealed class BreathCues
{
    /// <summary>The heartbeat starts when this fraction of the breath is left.</summary>
    public const float HeartbeatFrom = 0.4f;
    /// <summary>Seconds between beats when the heartbeat starts, and when the breath is empty.</summary>
    public const float SlowBeatSeconds = 0.8f, FastBeatSeconds = 0.42f;
    /// <summary>Level offset of the first beats, dB; the last are at 0.</summary>
    public const float QuietBeatDb = -9f;

    BreathState.BreathPhase _phase = BreathState.BreathPhase.Ready;
    double _nextBeat = double.NegativeInfinity;

    public void Reset() { _phase = BreathState.BreathPhase.Ready; _nextBeat = double.NegativeInfinity; }

    /// <summary>The cues of this tick, from the phase and meter <see cref="BreathState.Tick"/> left.</summary>
    public BreathCue Tick(double now, BreathState.BreathPhase phase, float meter)
    {
        var was = _phase;
        _phase = phase;
        var inhale = phase == BreathState.BreathPhase.Holding && was != BreathState.BreathPhase.Holding;
        var forced = phase == BreathState.BreathPhase.Exhaled && was != BreathState.BreathPhase.Exhaled;
        var exhale = was == BreathState.BreathPhase.Holding && phase == BreathState.BreathPhase.Ready;
        if (phase != BreathState.BreathPhase.Holding || meter >= HeartbeatFrom)
        {
            _nextBeat = double.NegativeInfinity; // the next hold's first beat comes the moment the breath is low
            return new BreathCue(inhale, exhale, forced, false, 0);
        }
        if (now < _nextBeat) return new BreathCue(inhale, exhale, forced, false, 0);
        var k = 1f - Math.Clamp(meter / HeartbeatFrom, 0f, 1f); // 0 at the first beat, 1 with no breath left
        _nextBeat = now + SlowBeatSeconds + (FastBeatSeconds - SlowBeatSeconds) * k;
        return new BreathCue(inhale, exhale, forced, true, QuietBeatDb * (1f - k));
    }
}

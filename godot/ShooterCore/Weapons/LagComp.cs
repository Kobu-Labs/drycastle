namespace Drycastle.ShooterCore;

/// <summary>
/// One player's hit target as it stood at the end of a tick. <see cref="Valid"/> is false when the player was not a target then (dead, in a seat,
/// or not yet recorded), so a rewind falls back to the present for them only while they are a target now.
/// </summary>
public readonly record struct PoseSample(uint Tick, HitTarget Target, bool Valid);

/// <summary>
/// The last <see cref="Size"/> ticks of one player's poses, indexed by tick. A fixed array, so it allocates once and never again. Size is a power of two
/// and well above the rewind cap (<see cref="LagComp.CapTicks"/>), so a sample is never overwritten while a shot can still ask for it.
/// </summary>
public sealed class PoseRing
{
    public const int Size = 32;
    const uint Mask = Size - 1;
    readonly PoseSample[] _samples = new PoseSample[Size];

    /// <summary>Records the pose of the player at the end of <paramref name="tick"/>.</summary>
    public void Record(uint tick, bool valid, HitTarget target) => _samples[tick & Mask] = new PoseSample(tick, target, valid);

    /// <summary>The pose at <paramref name="tick"/>, if it was recorded as a valid target pose (a slot holding another tick does not count).</summary>
    public bool TryGet(uint tick, out HitTarget target)
    {
        var s = _samples[tick & Mask];
        target = s.Target;
        return s.Valid && s.Tick == tick;
    }
}

/// <summary>
/// Lag compensation for players (ADR 0013): which tick a shooter's screen showed, and which recorded pose of a target that tick is. Pure arithmetic, no
/// Godot. A shooter sees the world <c>RTT/2</c> late plus the client's interpolation delay, so the server rewinds targets by that much, capped.
/// </summary>
public static class LagComp
{
    /// <summary>The largest rewind, from the present, in milliseconds. A 250 ms window bounds how long a target can hide behind cover that it has left.</summary>
    public const int CapMs = 250;

    /// <summary>The cap in whole ticks at <paramref name="tickRate"/> (250 ms at 30 Hz is 7 ticks, 233 ms: the window never exceeds the cap).</summary>
    public static int CapTicks(int tickRate) => CapMs * tickRate / 1000;

    /// <summary>
    /// How many ticks back the shooter's screen is: half the round trip in ticks (rounded) plus the interpolation delay, capped at
    /// <see cref="CapTicks"/>. A shooter with no round trip yet (a bot) sees the present: 0.
    /// </summary>
    public static int BackTicks(float rttMs, int tickRate, int interpDelayTicks)
    {
        if (rttMs <= 0) return 0;
        var oneWayTicks = rttMs / 2f * tickRate / 1000f;
        var back = (int)MathF.Round(oneWayTicks) + interpDelayTicks;
        return Math.Min(back, CapTicks(tickRate));
    }

    /// <summary>The tick the shooter's screen showed: <paramref name="now"/> minus <paramref name="backTicks"/>, never before the shooter's life began.</summary>
    public static uint ViewTick(uint now, int backTicks, uint lifeStart)
    {
        var tick = now >= (uint)backTicks ? now - (uint)backTicks : 0u;
        return Clamp(tick, lifeStart);
    }

    /// <summary>A target's life starts at its last spawn or teleport: nothing before it is a place it can be shot at.</summary>
    public static uint Clamp(uint tick, uint lifeStart) => tick < lifeStart ? lifeStart : tick;

    /// <summary>
    /// The target's pose at the view tick, clamped to the start of its current life. False when no valid pose was recorded there (the caller then
    /// resolves the target at its present pose, which is the pre-lag-compensation behaviour).
    /// </summary>
    public static bool TryTargetAt(PoseRing ring, uint viewTick, uint lifeStart, out HitTarget target) => ring.TryGet(Clamp(viewTick, lifeStart), out target);
}

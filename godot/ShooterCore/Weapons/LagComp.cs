using System.Numerics;

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
/// A vehicle's pose as a shot resolves it (ADR 0013, B-02): the feet on the ground under the centre, the yaw the hit box and the glass frame turn
/// with, the attitude the seats of a helicopter or a driven vehicle lie in (zero for a car), and a flak truck's turret yaw.
/// </summary>
public readonly record struct VehiclePose(Vector3 Feet, float Yaw, float Pitch, float Roll, float TurretYaw);

/// <summary>One vehicle's pose at the end of a tick (<see cref="Valid"/> false while it is destroyed: it is not a target then).</summary>
public readonly record struct VehiclePoseSample(uint Tick, VehiclePose Pose, bool Valid);

/// <summary>
/// The last <see cref="Size"/> ticks of one vehicle's poses. A sleeping vehicle (<c>ServerCar.Asleep</c>) has a constant pose, so its ticks are
/// not written one by one: <see cref="RecordRest"/> keeps a run (its first tick, its end and its pose), and <see cref="TryGet"/> answers from the
/// run for any tick in it. A vehicle can sleep, be shot awake and sleep again within the cap, so <see cref="RestSlots"/> runs are kept (two runs
/// fit in any window of the cap, since a sleep needs <see cref="ParkedRules.RestTicksToSleep"/> ticks). <see cref="Clear"/> starts a new life
/// (a reset or a repair): nothing before it is a place the vehicle can be shot at.
/// </summary>
public sealed class VehiclePoseRing
{
    public const int Size = 32;
    /// <summary>Sleeping runs kept, the newest ones (round robin).</summary>
    public const int RestSlots = 4;
    const uint Mask = Size - 1;
    readonly VehiclePoseSample[] _samples = new VehiclePoseSample[Size];
    readonly RestRun[] _rest = new RestRun[RestSlots];
    int _restNext;

    /// <summary>A sleep: the ticks [From, To) the vehicle stood still at <see cref="Pose"/>. To is 0 for an unused slot.</summary>
    struct RestRun { public uint From, To; public VehiclePose Pose; }

    /// <summary>Records the pose of an awake vehicle at the end of <paramref name="tick"/>.</summary>
    public void Record(uint tick, bool valid, VehiclePose pose) => _samples[tick & Mask] = new VehiclePoseSample(tick, pose, valid);

    /// <summary>Records a sleeping vehicle's pose at the end of <paramref name="tick"/>: it extends the run it is in, or starts a new one.</summary>
    public void RecordRest(uint tick, VehiclePose pose)
    {
        var last = (_restNext + RestSlots - 1) % RestSlots;
        if (_rest[last].To != 0 && _rest[last].To == tick) { _rest[last].To = tick + 1; _rest[last].Pose = pose; return; }
        _rest[_restNext] = new RestRun { From = tick, To = tick + 1, Pose = pose };
        _restNext = (_restNext + 1) % RestSlots;
    }

    /// <summary>The pose at <paramref name="tick"/>: its sample if it was recorded awake, else the pose of a sleeping run that holds the tick.</summary>
    public bool TryGet(uint tick, out VehiclePose pose)
    {
        var s = _samples[tick & Mask];
        if (s.Valid && s.Tick == tick) { pose = s.Pose; return true; }
        for (var i = 0; i < RestSlots; i++)
        {
            var r = _rest[i];
            if (r.To != 0 && tick >= r.From && tick < r.To) { pose = r.Pose; return true; }
        }
        pose = default;
        return false;
    }

    /// <summary>Forgets every pose: a new life.</summary>
    public void Clear()
    {
        Array.Clear(_samples);
        Array.Clear(_rest);
        _restNext = 0;
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
    /// <see cref="CapTicks"/>. A shooter with no connection (a bot) is resolved at the present by the caller, not here.
    /// </summary>
    public static int BackTicks(float rttMs, int tickRate, int interpDelayTicks)
    {
        if (rttMs < 0) rttMs = 0;
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

    /// <summary>
    /// A vehicle's pose at the view tick, clamped to the start of its current life (a reset or a repair). False when no pose was recorded there
    /// (the caller then resolves the vehicle at its present pose, as for a player).
    /// </summary>
    public static bool TryVehicleAt(VehiclePoseRing ring, uint viewTick, uint lifeStart, out VehiclePose pose) => ring.TryGet(Clamp(viewTick, lifeStart), out pose);
}

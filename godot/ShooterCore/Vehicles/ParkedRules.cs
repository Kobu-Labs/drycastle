namespace Drycastle.ShooterCore;

/// <summary>
/// When a driverless car can stop being stepped (perf audit T2-04, D-01, G-05). A driverless step is a function of the car's state, its place and the
/// world. When one such step leaves the place and the state where they were, and the car rests on the ground (or afloat), the next driverless step
/// does the same, so the car is parked until something disturbs it: a rider, a hit, a bump, a blast, a reset (the server wakes it at each of those).
/// </summary>
public static class ParkedRules
{
    /// <summary>Movement per tick below which a step counts as standing still (m). A car creeping down a 1 degree slope moves about 0.6 mm a tick, so it stays awake; float jitter on a floor is far below this.</summary>
    public const float RestEpsilon = 1e-4f;
    /// <summary>Consecutive resting steps before a car is put to sleep (hysteresis: one quiet tick is not enough).</summary>
    public const int RestTicksToSleep = 3;

    /// <param name="moved">How far the step moved the car's position (m).</param>
    /// <param name="speed">Speed after the step, m/s.</param>
    /// <param name="yawRate">Yaw rate after the step, rad/s.</param>
    /// <param name="supported">The car stands on the ground or floats after the step (in the air it falls, so it is not at rest).</param>
    public static bool IsAtRest(float moved, float speed, float yawRate, bool supported) =>
        moved < RestEpsilon && MathF.Abs(speed) < RestEpsilon && MathF.Abs(yawRate) < RestEpsilon && supported;

    /// <summary>The count of consecutive resting steps after this one.</summary>
    public static int NextRestCount(int count, bool atRest) => atRest ? count + 1 : 0;

    public static bool Sleeps(int restCount) => restCount >= RestTicksToSleep;
}

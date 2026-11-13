namespace Drycastle.ShooterCore;

/// <summary>
/// When a driverless car can stop being stepped (perf audit T2-04, D-01, G-05). A driverless step is a function of the car's state, its place and the
/// world. When one such step leaves the place and the state where they were, and the car rests on the ground (or afloat), the next driverless step
/// does the same, so the car is parked until something disturbs it: a rider, a hit, a bump, a blast, a reset (the server wakes it at each of those).
/// </summary>
public static class ParkedRules
{
    /// <param name="placeMoved">The step changed the car's position at all (bitwise, so a car creeping on a slope is not at rest).</param>
    /// <param name="speed">Speed after the step, m/s.</param>
    /// <param name="yawRate">Yaw rate after the step, rad/s (eases to exactly zero, so a turning car is not at rest).</param>
    /// <param name="supported">The car stands on the ground or floats after the step (in the air it falls, so it is not at rest).</param>
    public static bool RestsAfterStep(bool placeMoved, float speed, float yawRate, bool supported) =>
        !placeMoved && speed == 0f && yawRate == 0f && supported;
}

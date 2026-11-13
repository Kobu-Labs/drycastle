using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// When a driverless flying vehicle can stop being stepped (perf, the same rest-and-wake rule as <see cref="ParkedRules"/> for the ground cars).
/// A driverless flight step is a function of the state, the world and nobody's controls. A helicopter that stands on its skids, with its rotor
/// stopped and the keys and rates all at rest, is a fixed point of that step: the step changes nothing, so it is skipped until something wakes
/// it (an occupant, a hit, a bump, a blast, a reset). A hovering or descending helicopter is airborne, so it keeps stepping until it lands.
/// </summary>
public static class FlightRest
{
    /// <summary>True when the driverless step that took <paramref name="before"/> to <paramref name="after"/> left the helicopter standing still on the ground.</summary>
    public static bool IsAtRest(in FlightState before, in FlightState after) =>
        after.Grounded && !after.InWater && !after.AtEdge && after.Impact == 0
        && MathF.Abs(after.Rotor) < ParkedRules.RestEpsilon
        && MathF.Abs(after.InputX) < ParkedRules.RestEpsilon && MathF.Abs(after.InputY) < ParkedRules.RestEpsilon
        && after.Velocity.Length() < ParkedRules.RestEpsilon
        && MathF.Abs(after.YawRate) < ParkedRules.RestEpsilon && MathF.Abs(after.PitchRate) < ParkedRules.RestEpsilon && MathF.Abs(after.RollRate) < ParkedRules.RestEpsilon
        && MathF.Abs(after.Pitch) < ParkedRules.RestEpsilon && MathF.Abs(after.Roll) < ParkedRules.RestEpsilon
        && Vector3.Distance(before.Position, after.Position) < ParkedRules.RestEpsilon
        && MathF.Abs(FlightMath.Wrap(after.Yaw - before.Yaw)) < ParkedRules.RestEpsilon;
}

/// <summary>
/// When a driverless driven vehicle (the amphibious transport) can stop being stepped. Same idea as <see cref="FlightRest"/>: on its wheels or
/// afloat, at a standstill with the wheel centred and nothing sinking, climbing out or falling, it is a fixed point of the hands-off step.
/// Afloat, a drift (a move from where it floats) keeps it awake, so a boat that is carried away is stepped every tick.
/// </summary>
public static class DriveRest
{
    /// <summary>True when the hands-off step that took <paramref name="before"/> to <paramref name="after"/> left the vehicle at rest, on the ground or afloat.</summary>
    public static bool IsAtRest(in DriveState before, in DriveState after) =>
        !after.Airborne && !after.Emerging && !after.EnteredWater && !after.LeftWater && after.Impact == 0
        && MathF.Abs(after.Speed) < ParkedRules.RestEpsilon && MathF.Abs(after.Slip) < ParkedRules.RestEpsilon
        && MathF.Abs(after.YawRate) < ParkedRules.RestEpsilon && MathF.Abs(after.VerticalSpeed) < ParkedRules.RestEpsilon
        && MathF.Abs(after.Throttle) < ParkedRules.RestEpsilon && MathF.Abs(after.Steer) < ParkedRules.RestEpsilon
        && MathF.Abs(after.Pitch - before.Pitch) < ParkedRules.RestEpsilon && MathF.Abs(after.Roll - before.Roll) < ParkedRules.RestEpsilon // parked on a slope the lie is the slope's: only a change is motion
        && Vector3.Distance(before.Position, after.Position) < ParkedRules.RestEpsilon
        && MathF.Abs(FlightMath.Wrap(after.Yaw - before.Yaw)) < ParkedRules.RestEpsilon;
}

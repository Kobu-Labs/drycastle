using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>When a gunship round detonates: now (instant, as a cannon round), or later at the time its tracer arrives.</summary>
public readonly record struct ShellTiming(bool Deferred, double DueAt);

/// <summary>
/// The flight of a shell (the AC-130's 105 mm). A shell is a slow round, so its damage and blast land when its tracer arrives:
/// the detonation is at <c>fire time + distance / tracer_speed_mps</c>, with the tracer's speed read from killstreaks.json (one source
/// of truth for the visual and the timing). A flight no longer than one server tick is resolved at once. The point is fixed at fire
/// time: a shell does not home on a target that moves.
/// </summary>
public static class ShellFlight
{
    /// <summary>Seconds for a round to fly from <paramref name="from"/> to <paramref name="to"/> at <paramref name="speedMps"/>; zero when no speed is set.</summary>
    public static float Seconds(Vector3 from, Vector3 to, float speedMps) => speedMps > 0 ? Vector3.Distance(from, to) / speedMps : 0f;

    /// <summary>The shell's detonation: deferred when its flight is longer than <paramref name="tickS"/>, due at <paramref name="now"/> plus the flight.</summary>
    public static ShellTiming Plan(Vector3 from, Vector3 to, float speedMps, double now, double tickS)
    {
        var flight = Seconds(from, to, speedMps);
        return flight > tickS ? new ShellTiming(true, now + flight) : new ShellTiming(false, now);
    }
}

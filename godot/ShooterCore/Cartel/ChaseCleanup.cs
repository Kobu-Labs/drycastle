using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What is left of one chase car in the world.</summary>
/// <param name="HasCar">The vehicle is still in the world.</param>
/// <param name="CarDestroyed">The vehicle is destroyed (a wreck).</param>
/// <param name="CarOccupied">A player sits in the vehicle.</param>
/// <param name="HasCrew">The crew (the Rival crew of its Gunmen and driver) is still in the world.</param>
/// <param name="CrewCount">Members of the crew left in the world, the dead whose bodies are still lying included.</param>
public readonly record struct ChaseLeft(bool HasCar, bool CarDestroyed, bool CarOccupied, bool HasCrew, int CrewCount);

/// <summary>What is due to leave the world now: the vehicle, the crew.</summary>
public readonly record struct ChaseDue(bool Car, bool Crew);

/// <summary>
/// When one chase car and its crew leave the world (docs/cartel.md "Chase cars"), pure: all of it at once when the car gave up, its Heist ended or its spawn was never completed (a crew that did not all find a seat);
/// when it is Gone (or its car is already out of the world and the crew did not get out), the car unless a player sits in it (they keep it until they leave) and the crew once nothing of it is left but its bodies' time;
/// a destroyed car's wreck lies <see cref="ChaseData.WreckSeconds"/> first (before the type's return time brings the car back). The server asks it and does what it says.
/// </summary>
public sealed class ChaseCleanup
{
    readonly ChaseData _d;
    Countdown _wreck;

    public ChaseCleanup(ChaseData d) { _d = d; }

    /// <summary>The car blew up: its wreck is given its time.</summary>
    public void Exploded() => _wreck.Start(_d.WreckSeconds);

    /// <summary>One tick: true exactly once, the tick a destroyed car's wreck has lain its time (the car is due).</summary>
    public bool WreckOver(bool carDestroyed, float dt) => carDestroyed && _wreck.Tick(dt);

    /// <summary>
    /// What is due to leave in <paramref name="state"/> with <paramref name="left"/> still in the world. <paramref name="ended"/>: the Heist ended, or the spawn was left half seated (everything goes now).
    /// </summary>
    public static ChaseDue Due(ChaseState state, in ChaseLeft left, bool ended = false)
    {
        if (ended || state == ChaseState.GivingUp) return new ChaseDue(left.HasCar, left.HasCrew);
        var winding = state == ChaseState.Gone || (!left.HasCar && state != ChaseState.Out);
        if (!winding) return default;
        return new ChaseDue(left is { HasCar: true, CarDestroyed: false, CarOccupied: false }, left is { HasCrew: true, CrewCount: 0 });
    }
}

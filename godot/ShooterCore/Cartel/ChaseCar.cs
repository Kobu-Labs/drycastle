using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where one Rival chase car is (machine <c>Chase.Car</c>, GLOSSARY "Chase car").</summary>
public enum ChaseState : byte { Spawned, Chasing, Stopping, Out, GivingUp, Gone }

/// <summary>What the server reads of one chase car each tick.</summary>
/// <param name="GapM">Ground distance from the car to the Cartel van.</param>
/// <param name="VanSpeed">The van's speed, metres a second.</param>
/// <param name="VanOnRoad">A road lies within off_road_m of the van (else the car drives to the road point nearest it).</param>
/// <param name="RoadPointM">While the van is off the road: the distance from the car to the road point nearest the van; infinite otherwise.</param>
/// <param name="CarSpeed">The car's speed, metres a second.</param>
/// <param name="DriverAlive">The driver of the car lives.</param>
/// <param name="CarDestroyed">The car is destroyed.</param>
/// <param name="HasRoute">The car has a way over the roads to where it is going.</param>
public readonly record struct ChaseFacts(float GapM, float VanSpeed, bool VanOnRoad, float RoadPointM, float CarSpeed, bool DriverAlive, bool CarDestroyed, bool HasRoute);

/// <summary>What one step of a chase car asks the server to do: the crew gets out now (once), or the car and its crew are removed now (once).</summary>
public readonly record struct ChaseStep(bool GetOut, bool Remove);

/// <summary>
/// One Rival chase car (GLOSSARY "Chase car"; docs/cartel.md "Chase cars"), pure: it follows the Cartel van, stops and lets the crew out, or gives up. The server drives the real vehicle from it
/// (<see cref="ChasePilot"/>) and seats the crew. It follows the van while it is Chasing, stops when the van is slow and near, its driver dies, it is stuck or the van is off the road and it reached the
/// road point nearest it (Stopping), lets the crew out once it stands (Out: they fight on foot as a Rival crew), gives up when it stays too far from the van (GivingUp) and is Gone when it, or its crew, is over.
/// </summary>
public sealed class ChaseCar
{
    public static readonly StateMachineSpec<ChaseState> Spec = StateMachineSpec<ChaseState>
        .Define("Chase.Car", "One Rival chase car of a Heist: it follows the Cartel van on the roads with its crew in the windows, stops and lets the crew out when the van is slow and near (or off the road, or its driver died), or gives up when it stays far behind.", ChaseState.Spawned)
        .Allow(ChaseState.Spawned, ChaseState.Chasing)
        .Allow(ChaseState.Chasing, ChaseState.Stopping, ChaseState.GivingUp)
        .Allow(ChaseState.Stopping, ChaseState.Out, ChaseState.GivingUp)
        .Allow(ChaseState.Out, ChaseState.Gone)
        .Allow(ChaseState.GivingUp, ChaseState.Gone)
        .FromAnywhere(ChaseState.Gone)
        .Note(ChaseState.Spawned, "Just put down on a road behind the van with its crew seated: nothing driven yet. The next step is Chasing.")
        .Note(ChaseState.Chasing, "Drives after the van on the roads, keeping follow_m from it; the Gunmen shoot from the windows while it is within shoot_range_m of the van, with a worse aim while it moves. Ends when the van is slower than stop_speed_mps within stop_within_m, the driver is dead, the car is stuck (stalled_seconds), or the van is off the road and the car reached the road point nearest it. Gives up (clock: give_up_seconds) when it stays more than give_up_m from the van; a destroyed car is Gone.")
        .Note(ChaseState.Stopping, "Brakes to a stop (a dead driver: it rolls) (clock: stop_seconds at most), the Gunmen still shooting while near. Standing, the crew gets out. Gives up as Chasing does; a destroyed car is Gone.")
        .Note(ChaseState.Out, "The crew is out on foot and fights as a Rival crew (the Job's party on sight); the car stands, free to take. Gone once the crew is gone.")
        .Note(ChaseState.GivingUp, "Stayed more than give_up_m from the van for give_up_seconds: the server removes the car and its crew at once, then it is Gone.")
        .Note(ChaseState.Gone, "Removed from the world (given up, spent, destroyed and its wreck gone, or its Heist ended). Never left.")
        .Build();

    readonly StateMachine<ChaseState> _fsm = new(Spec);
    readonly ChaseData _d;
    Countdown _far;     // the car has stayed more than give_up_m from the van
    Countdown _blocked; // the car is stuck: no way, or no 1 m/s while it wants to get on
    float _gap = float.PositiveInfinity;

    public ChaseCar(ChaseData d) { _d = d; }

    public ChaseState State => _fsm.Current;

    /// <summary>The state changed (from, to).</summary>
    public event Action<ChaseState, ChaseState>? Changed
    {
        add => _fsm.Changed += value;
        remove => _fsm.Changed -= value;
    }

    /// <summary>The Gunmen may shoot now: the car is within shoot_range_m of the van (Chasing, Stopping) or the crew is out.</summary>
    public bool Shoots => _fsm.Is(ChaseState.Out) || (_fsm.IsAny(ChaseState.Chasing, ChaseState.Stopping) && _gap <= _d.ShootRangeM);

    /// <summary>The car still counts towards max_alive: its crew sits in it (not yet out, not given up), or a member of the crew lives.</summary>
    public bool InFight(int crewAlive) => _fsm.IsAny(ChaseState.Spawned, ChaseState.Chasing, ChaseState.Stopping) || (_fsm.Is(ChaseState.Out) && crewAlive > 0);

    /// <summary>The crew sits in the car (not yet out).</summary>
    public bool CrewInside => _fsm.IsAny(ChaseState.Spawned, ChaseState.Chasing, ChaseState.Stopping);

    /// <summary>Seconds of stopping left (infinite outside Stopping).</summary>
    public float StopLeft => _fsm.Is(ChaseState.Stopping) ? _fsm.Left : float.PositiveInfinity;

    /// <summary>The car, its crew or its Heist is over: from any state it is Gone.</summary>
    public void End() => _fsm.Enter(ChaseState.Gone);

    public ChaseStep Step(in ChaseFacts f, float dt)
    {
        _gap = f.GapM;
        switch (_fsm.Current)
        {
            case ChaseState.Spawned:
                if (f.CarDestroyed) _fsm.Enter(ChaseState.Gone);
                else _fsm.Enter(ChaseState.Chasing);
                return default;
            case ChaseState.Chasing:
            case ChaseState.Stopping:
                if (f.CarDestroyed) { _fsm.Enter(ChaseState.Gone); return default; } // its crew died with it (the server kills it)
                if (GivesUp(f, dt)) { _fsm.Enter(ChaseState.GivingUp); return new ChaseStep(false, true); }
                if (_fsm.Is(ChaseState.Chasing))
                {
                    if (StopsNow(f, dt)) _fsm.Enter(ChaseState.Stopping, _d.StopSeconds);
                    return default;
                }
                _fsm.Tick(dt);
                if (f.CarSpeed < 0.5f || _fsm.Expired) { _fsm.Enter(ChaseState.Out); return new ChaseStep(true, false); }
                return default;
            default:
                return default;
        }
    }

    /// <summary>More than give_up_m from the van for give_up_seconds.</summary>
    bool GivesUp(in ChaseFacts f, float dt)
    {
        if (f.GapM <= _d.GiveUpM) { _far.Cancel(); return false; }
        _far.StartIfReady(_d.GiveUpSeconds);
        return _far.Tick(dt);
    }

    /// <summary>The van is slow and near, the driver is dead, the car is stuck, or the van is off the road and the car is at the road point nearest it.</summary>
    bool StopsNow(in ChaseFacts f, float dt)
    {
        if (!f.DriverAlive) return true;
        if (f.VanSpeed < _d.StopSpeedMps && f.GapM <= _d.StopWithinM) return true;
        if (!f.VanOnRoad && f.RoadPointM <= _d.ArriveM) return true;
        var stuck = !f.HasRoute || (f.CarSpeed < 1f && f.GapM > _d.FollowM);
        if (!stuck) { _blocked.Cancel(); return false; }
        _blocked.StartIfReady(_d.StalledSeconds);
        return _blocked.Tick(dt);
    }
}

/// <summary>Where the spawn clock of a Heist's chase cars is (machine <c>Heist.Chases</c>).</summary>
public enum ChaseClockState : byte { Idle, Running, Over }

/// <summary>
/// When the next Rival chase car of a Heist is due (docs/cartel.md "Chase cars"): nothing before the van left the objective with the minimum loaded, then the first after first_after_seconds and one every
/// interval_seconds while fewer than max_alive are in the fight, none once the van reached the drop (or the Heist ended). A due car that cannot be put down (no spot yet) asks again after the retry time; one
/// that the cap holds back comes the moment a car is out of the fight.
/// </summary>
public sealed class ChaseClock
{
    public static readonly StateMachineSpec<ChaseClockState> Spec = StateMachineSpec<ChaseClockState>
        .Define("Heist.Chases", "When a Heist's next Rival chase car is due: from the van leaving the objective with the minimum loaded (the first after a delay, then every interval, at most max_alive in the fight), until the van reaches the drop.", ChaseClockState.Idle)
        .Allow(ChaseClockState.Idle, ChaseClockState.Running)
        .FromAnywhere(ChaseClockState.Over)
        .Note(ChaseClockState.Idle, "The van has not left the objective with the minimum yet; no chase car.")
        .Note(ChaseClockState.Running, "The van is on its way to the drop (a clock: the first_after_seconds, then the interval after each car put down, the retry after one that found no spot). At its end a car is due, if fewer than max_alive are in the fight.")
        .Note(ChaseClockState.Over, "The van reached the drop, or the Heist ended: no new car. The ones out fight on. Never left.")
        .Build();

    readonly StateMachine<ChaseClockState> _fsm = new(Spec);
    readonly ChaseData _d;
    Countdown _wait;

    public ChaseClock(ChaseData d) { _d = d; }

    public ChaseClockState State => _fsm.Current;

    /// <summary>Seconds to the next car (0 while one is due; infinite while idle or over).</summary>
    public float Left => _fsm.Is(ChaseClockState.Running) ? _wait.Left : float.PositiveInfinity;

    /// <summary>The van left the objective with the minimum loaded: the clock starts (the first car after first_after_seconds). A later leave of the same drive, or one after the end, changes nothing. True when it started.</summary>
    public bool Start()
    {
        if (!_fsm.Is(ChaseClockState.Idle)) return false;
        _fsm.Enter(ChaseClockState.Running);
        _wait.Start(_d.FirstAfterSeconds);
        return true;
    }

    /// <summary>The van reached the drop, or the Heist ended: no new car ever.</summary>
    public void Stop() => _fsm.Enter(ChaseClockState.Over);

    /// <summary>One tick with <paramref name="alive"/> cars in the fight. True while a car is due and fewer than max_alive are alive: the caller puts one down (then <see cref="Sent"/>) or finds no spot (<see cref="NoSpot"/>).</summary>
    public bool Tick(float dt, int alive)
    {
        if (!_fsm.Is(ChaseClockState.Running)) return false;
        _wait.Tick(dt);
        return _wait.Ready && alive < _d.MaxAlive;
    }

    /// <summary>A car was put down: the next is due interval_seconds from now.</summary>
    public void Sent() => _wait.Start(_d.IntervalSeconds);

    /// <summary>The due car found no spot: it asks again after spawn_retry_seconds.</summary>
    public void NoSpot() => _wait.Start(_d.SpawnRetrySeconds);
}

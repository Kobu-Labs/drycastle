using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where one Rival chase car is (machine <c>Chase.Car</c>, GLOSSARY "Chase car").</summary>
public enum ChaseState : byte { Spawned, Chasing, Stopping, Out, GivingUp, Gone, Reversing, Remounting, Waiting }

/// <summary>What the server reads of one chase car each tick.</summary>
/// <param name="GapM">Ground distance from the car to the Cartel van.</param>
/// <param name="VanSpeed">The van's speed, metres a second.</param>
/// <param name="CarSpeed">The car's speed, metres a second.</param>
/// <param name="DriverAlive">The driver of the car lives.</param>
/// <param name="CarDestroyed">The car is destroyed.</param>
/// <param name="HasRoute">The car has a way (over the roads and open ground) to where it is going.</param>
/// <param name="CrewGapM">Ground distance from the nearest living member of the crew to the van (infinite when none lives).</param>
/// <param name="CrewAlive">Members of the crew that live.</param>
/// <param name="CarFree">The car stands in the world and no player sits in it: the crew may get back in.</param>
/// <param name="Aboard">Every living member of the crew sits in the car again (a Remounting crew).</param>
/// <param name="Passed">The van has passed the car: the car lies behind the van along the van's heading (<see cref="ChaseRules.Passed"/>).</param>
/// <param name="HeadOn">The van comes at the car head-on: fast, near, ahead of its nose, the headings more than 120 degrees apart (<see cref="ChaseRules.HeadOn"/>).</param>
public readonly record struct ChaseFacts(float GapM, float VanSpeed, float CarSpeed, bool DriverAlive, bool CarDestroyed, bool HasRoute,
    float CrewGapM = float.PositiveInfinity, int CrewAlive = 0, bool CarFree = false, bool Aboard = false, bool Passed = false, bool HeadOn = false);

/// <summary>What one step of a chase car asks the server to do: the crew gets out now (once), the crew walks back to the car (once), the crew is back in and drives on (once), the walk back is over without it (once), or the car and its crew are removed now (once).</summary>
public readonly record struct ChaseStep(bool GetOut, bool Remove, bool Remount = false, bool Mounted = false, bool AbortRemount = false);

/// <summary>
/// One Rival chase car (GLOSSARY "Chase car"; docs/cartel.md "Chase cars"), pure: it follows the Cartel van, stops and lets the crew out, or gives up. The server drives the real vehicle from it
/// (<see cref="ChasePilot"/>) and seats the crew. It follows the van while it is Chasing (backing out for a moment when it is wedged: Reversing), stops when the van is slow and near, its driver dies or it is stuck
/// for good (Stopping), lets the crew out once it stands (Out: they fight on foot as a Rival crew), calls them back when the van is more than remount_m away and pulling away (Remounting: they walk to the car and
/// get in, and the chase goes on), stands aside and waits when the van is about to pass it (Waiting: an ambush from beside the van's way, or a head-on meeting), gives up when it, or its crew, stays too far from the van (GivingUp) and is Gone when it, or its crew, is over.
/// </summary>
public sealed class ChaseCar
{
    public static readonly StateMachineSpec<ChaseState> Spec = StateMachineSpec<ChaseState>
        .Define("Chase.Car", "One Rival car of a Heist, a chase car or an Assault van (the same driver): it follows the Cartel van over the roads and open ground with its crew in the windows, stands aside and waits while the van drives up to it (a car put down ahead of the van, or a van coming head-on), backs out when it is wedged, stops and lets the crew out when the van is slow and near (or its driver died, or it is stuck for good), calls the crew back to the car when the van pulls away, or gives up when it stays far behind.", ChaseState.Spawned)
        .Allow(ChaseState.Spawned, ChaseState.Chasing, ChaseState.Waiting)
        .Allow(ChaseState.Chasing, ChaseState.Stopping, ChaseState.Reversing, ChaseState.GivingUp, ChaseState.Waiting)
        .Allow(ChaseState.Waiting, ChaseState.Chasing, ChaseState.Stopping)
        .Allow(ChaseState.Reversing, ChaseState.Chasing, ChaseState.Stopping, ChaseState.GivingUp)
        .Allow(ChaseState.Stopping, ChaseState.Out, ChaseState.GivingUp)
        .Allow(ChaseState.Out, ChaseState.Remounting, ChaseState.GivingUp)
        .Allow(ChaseState.Remounting, ChaseState.Chasing, ChaseState.Out, ChaseState.GivingUp)
        .Allow(ChaseState.Out, ChaseState.Gone)
        .Allow(ChaseState.GivingUp, ChaseState.Gone)
        .FromAnywhere(ChaseState.Gone)
        .Note(ChaseState.Spawned, "Just put down on open ground (a chase car ahead of or behind the van, an Assault van 250 to 400 m of way from it) with its crew seated: nothing driven yet. The next step is Chasing, or Waiting for a car put down ahead of the van (an ambush).")
        .Note(ChaseState.Chasing, "Drives after the van, over the roads where they bring it closer and across open ground where that is shorter (never over buildings, trees, fences or water), keeping follow_m from it; the Gunmen shoot from the windows while it is within shoot_range_m of the van, with a worse aim while it moves. When the way is behind it and it is slow it backs round (a K-turn: the pilot). The van coming at it head-on (faster than stop_speed_mps, within meet_m, the headings more than 120 degrees apart, ahead of its nose): Waiting. Wedged (under 1 m/s for unstick_after_seconds with the van beyond follow_m): Reversing. Ends when the van is slower than stop_speed_mps within stop_within_m, the driver is dead, or the car stays stuck for stalled_seconds. Gives up (clock: give_up_seconds) when it stays more than give_up_m from the van; a destroyed car is Gone.")
        .Note(ChaseState.Waiting, "Stands beside the van's way (an ambush: put down ahead of the van, facing along its way, ambush_side_m off it) or pulls aside with the brake on and the wheel to the right (a head-on meeting) and waits for the van to drive past; the Gunmen shoot from the windows within shoot_range_m of the van (a standing aim). The van passed it (it lies behind the van along the van's heading), wait_max_seconds out, or the van more than give_up_m away for give_up_seconds: Chasing. The van slow and near (or its driver dead): Stopping, as from Chasing.")
        .Note(ChaseState.Reversing, "Backs out of where it is wedged (clock: reverse_seconds), steering to turn its nose to its way, then Chasing again; the tries are counted (max_unsticks). A dead driver stops it; gives up as Chasing does; a destroyed car is Gone.")
        .Note(ChaseState.Stopping, "Brakes to a stop (a dead driver: it rolls) (clock: stop_seconds at most), the Gunmen still shooting while near. Standing, the crew gets out. Gives up as Chasing does; a destroyed car is Gone.")
        .Note(ChaseState.Out, "The crew is out on foot and fights as a Rival crew (the Job's party on sight); the car stands, free to take. When the van is more than remount_m from the crew and pulling away (confirmed for remount_confirm_seconds) and the car is whole, free and its driver lives: Remounting. Gives up when the crew stays more than give_up_m from the van for give_up_seconds. Gone once the crew is gone.")
        .Note(ChaseState.Remounting, "The crew walks back to the car's doors over the navigation mesh and sits down in its seats (clock: remount_seconds). Every living member aboard: Chasing. The car wrecked or taken, the driver dead, nobody left, or the clock out: Out (the crew on foot; it tries again after remount_retry_seconds). Gives up as Out does.")
        .Note(ChaseState.GivingUp, "Stayed more than give_up_m from the van for give_up_seconds: the server removes the car and its crew at once, then it is Gone.")
        .Note(ChaseState.Gone, "Removed from the world (given up, spent, destroyed and its wreck gone, or its Heist ended). Never left.")
        .Build();

    /// <summary>The gap rate is smoothed over about this long (seconds).</summary>
    const float PullSmoothS = 0.5f;

    readonly StateMachine<ChaseState> _fsm = new(Spec);
    readonly ChaseData _d;
    readonly bool _ambush; // put down ahead of the van: it waits for it first
    Countdown _far;     // the car (or its crew on foot) has stayed more than give_up_m from the van
    Countdown _blocked; // the car is stuck: no way, or no 1 m/s while it wants to get on
    Countdown _wedged;  // under 1 m/s with the van beyond follow_m: time to back out
    Countdown _moving;  // driving on at 3 m/s: the tries to get free are forgotten
    Countdown _confirm; // the van pulls away from the crew on foot
    Countdown _retry;   // a walk back to the car that failed: not again at once
    int _unsticks;
    float _gap = float.PositiveInfinity;
    float _lastCrewGap = float.NaN;
    float _pull;        // how fast the van gets away from the crew on foot, metres a second, smoothed

    /// <param name="d">The numbers.</param>
    /// <param name="ambush">Put down ahead of the van, beside its way: it waits there (Waiting) until the van has passed it.</param>
    public ChaseCar(ChaseData d, bool ambush = false) { _d = d; _ambush = ambush; }

    public ChaseState State => _fsm.Current;

    /// <summary>The state changed (from, to).</summary>
    public event Action<ChaseState, ChaseState>? Changed
    {
        add => _fsm.Changed += value;
        remove => _fsm.Changed -= value;
    }

    /// <summary>The Gunmen may shoot now: the car is within shoot_range_m of the van (driving) or the crew is out.</summary>
    public bool Shoots => _fsm.Is(ChaseState.Out) || (_fsm.IsAny(ChaseState.Chasing, ChaseState.Reversing, ChaseState.Stopping, ChaseState.Waiting) && _gap <= _d.ShootRangeM);

    /// <summary>The car still counts towards max_alive: its crew sits in it or walks back to it (not out, not given up), or a member of the crew lives.</summary>
    public bool InFight(int crewAlive) => _fsm.IsAny(ChaseState.Spawned, ChaseState.Chasing, ChaseState.Reversing, ChaseState.Stopping, ChaseState.Remounting, ChaseState.Waiting) || (_fsm.Is(ChaseState.Out) && crewAlive > 0);

    /// <summary>The crew sits in the car (not out).</summary>
    public bool CrewInside => _fsm.IsAny(ChaseState.Spawned, ChaseState.Chasing, ChaseState.Reversing, ChaseState.Stopping, ChaseState.Waiting);

    /// <summary>The crew walks back to the car, those that arrived sit in their seats already.</summary>
    public bool Remounting => _fsm.Is(ChaseState.Remounting);

    /// <summary>The car, its crew or its Heist is over: from any state it is Gone.</summary>
    public void End() => _fsm.Enter(ChaseState.Gone);

    public ChaseStep Step(in ChaseFacts f, float dt)
    {
        _gap = f.GapM;
        switch (_fsm.Current)
        {
            case ChaseState.Spawned:
                if (f.CarDestroyed) _fsm.Enter(ChaseState.Gone);
                else if (_ambush) _fsm.Enter(ChaseState.Waiting, _d.WaitMaxSeconds);
                else _fsm.Enter(ChaseState.Chasing);
                return default;
            case ChaseState.Waiting:
            {
                if (f.CarDestroyed) { _fsm.Enter(ChaseState.Gone); return default; }
                // the car stands: no stuck or wedged rule, only the stop rule, the van's pass and the safeties
                var timeUp = _fsm.Tick(dt);
                var far = GivesUp(f.GapM, dt);
                if (Halts(f)) _fsm.Enter(ChaseState.Stopping, _d.StopSeconds);
                else if (f.Passed || timeUp || far) { _fsm.Enter(ChaseState.Chasing); _blocked.Cancel(); _wedged.Cancel(); }
                return default;
            }
            case ChaseState.Chasing:
            case ChaseState.Reversing:
            case ChaseState.Stopping:
                if (f.CarDestroyed) { _fsm.Enter(ChaseState.Gone); return default; } // its crew died with it (the server kills it)
                if (GivesUp(f.GapM, dt)) { _fsm.Enter(ChaseState.GivingUp); return new ChaseStep(false, true); }
                if (_fsm.Is(ChaseState.Chasing))
                {
                    Moving(f, dt);
                    if (StopsNow(f, dt)) _fsm.Enter(ChaseState.Stopping, _d.StopSeconds);
                    else if (f.HeadOn) _fsm.Enter(ChaseState.Waiting, _d.WaitMaxSeconds); // the van comes at it: aside, and wait
                    else if (Wedged(f, dt)) { _fsm.Enter(ChaseState.Reversing, _d.ReverseSeconds); _blocked.Cancel(); } // backing out is progress: the stuck clock starts over
                    return default;
                }
                if (_fsm.Is(ChaseState.Reversing))
                {
                    if (!f.DriverAlive) _fsm.Enter(ChaseState.Stopping, _d.StopSeconds);
                    else if (_fsm.Tick(dt)) _fsm.Enter(ChaseState.Chasing);
                    return default;
                }
                _fsm.Tick(dt);
                if (f.CarSpeed < 0.5f || _fsm.Expired) { _fsm.Enter(ChaseState.Out); ForgetPull(); return new ChaseStep(true, false); }
                return default;
            case ChaseState.Out:
                _retry.Tick(dt);
                if (GivesUp(f.CrewGapM, dt)) { _fsm.Enter(ChaseState.GivingUp); return new ChaseStep(false, true); }
                if (WantsBack(f, dt)) { _fsm.Enter(ChaseState.Remounting, _d.RemountSeconds); _far.Cancel(); return new ChaseStep(false, false, Remount: true); }
                return default;
            case ChaseState.Remounting:
                if (GivesUp(f.CrewGapM, dt)) { _fsm.Enter(ChaseState.GivingUp); return new ChaseStep(false, true); }
                if (f.CarDestroyed || !f.CarFree || !f.DriverAlive || f.CrewAlive == 0) return Fail();
                if (f.Aboard) { _fsm.Enter(ChaseState.Chasing); _unsticks = 0; _blocked.Cancel(); _wedged.Cancel(); return new ChaseStep(false, false, Mounted: true); }
                if (_fsm.Tick(dt)) return Fail();
                return default;
            default:
                return default;
        }
    }

    /// <summary>The walk back is over without the crew in the car: they stand on foot again and may try again after remount_retry_seconds.</summary>
    ChaseStep Fail()
    {
        _fsm.Enter(ChaseState.Out);
        ForgetPull();
        _retry.Start(_d.RemountRetrySeconds);
        return new ChaseStep(false, false, AbortRemount: true);
    }

    void ForgetPull() { _lastCrewGap = float.NaN; _pull = 0; _confirm.Cancel(); }

    /// <summary>More than give_up_m from the van for give_up_seconds.</summary>
    bool GivesUp(float gapM, float dt)
    {
        if (gapM <= _d.GiveUpM) { _far.Cancel(); return false; }
        _far.StartIfReady(_d.GiveUpSeconds);
        return _far.Tick(dt);
    }

    /// <summary>The van is slow and near, the driver is dead, or the car is stuck.</summary>
    bool StopsNow(in ChaseFacts f, float dt)
    {
        if (Halts(f)) return true;
        var stuck = !f.HasRoute || (f.CarSpeed < 1f && f.GapM > _d.FollowM);
        if (!stuck) { _blocked.Cancel(); return false; }
        _blocked.StartIfReady(_d.StalledSeconds);
        return _blocked.Tick(dt);
    }

    /// <summary>The driver is dead, or the van is slower than stop_speed_mps within stop_within_m.</summary>
    bool Halts(in ChaseFacts f) => !f.DriverAlive || (f.VanSpeed < _d.StopSpeedMps && f.GapM <= _d.StopWithinM);

    /// <summary>Under 1 m/s with the van beyond follow_m (and a way to it) for unstick_after_seconds, and still tries left: back out.</summary>
    bool Wedged(in ChaseFacts f, float dt)
    {
        if (!(f.HasRoute && f.CarSpeed < 1f && f.GapM > _d.FollowM)) { _wedged.Cancel(); return false; }
        _wedged.StartIfReady(_d.UnstickAfterSeconds);
        if (!_wedged.Tick(dt) || _unsticks >= _d.MaxUnsticks) return false;
        _unsticks++;
        return true;
    }

    /// <summary>Driving on at 3 m/s for 3 s: the tries to get free are forgotten.</summary>
    void Moving(in ChaseFacts f, float dt)
    {
        if (f.CarSpeed < 3f) { _moving.Cancel(); return; }
        _moving.StartIfReady(3f);
        if (_moving.Tick(dt)) _unsticks = 0;
    }

    /// <summary>The crew on foot should go back to the car: the van is more than remount_m from it and pulling away (confirmed), the car is whole and free, its driver lives and a member is left.</summary>
    bool WantsBack(in ChaseFacts f, float dt)
    {
        if (!float.IsNaN(_lastCrewGap) && dt > 0 && float.IsFinite(f.CrewGapM)) _pull += ((f.CrewGapM - _lastCrewGap) / dt - _pull) * Math.Min(1f, dt / PullSmoothS);
        _lastCrewGap = f.CrewGapM;
        var possible = f.CrewAlive > 0 && f.CarFree && !f.CarDestroyed && f.DriverAlive && _retry.Ready;
        if (!(possible && f.CrewGapM > _d.RemountM && _pull > _d.PullMps)) { _confirm.Cancel(); return false; }
        _confirm.StartIfReady(_d.RemountConfirmSeconds);
        return _confirm.Tick(dt);
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

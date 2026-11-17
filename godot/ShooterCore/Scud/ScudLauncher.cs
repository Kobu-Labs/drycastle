using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Where a Scud truck's launcher is (GLOSSARY "Launcher"). The values are the wire byte of <see cref="ScudStateMsg"/>; do not renumber.
/// The machine is <see cref="ScudLauncher.Spec"/>, server-owned: clients only draw it (the rack, the missile on it, the screen's status line).
/// </summary>
public enum ScudLauncherState : byte
{
    /// <summary>A rocket on the rack, the rack down. The truck drives.</summary>
    Stowed = 0,
    /// <summary>The operator has the targeting screen open.</summary>
    Targeting = 1,
    /// <summary>The operator locked a point; every occupied seat must confirm.</summary>
    Proposed = 2,
    /// <summary>The rack rises; the truck is immobilised.</summary>
    Erecting = 3,
    /// <summary>The rack is up and the rocket is igniting.</summary>
    Launching = 4,
    /// <summary>The rocket left: the rack is up and empty.</summary>
    Spent = 5,
    /// <summary>The empty rack lowers.</summary>
    Lowering = 6,
    /// <summary>The rack is down with no rocket: the truck needs a reload.</summary>
    Empty = 7,
    /// <summary>A rocket is being loaded (the rocket cart's job, or an admin's command).</summary>
    Reloading = 8,
    /// <summary>The operator aborted the erect: the rack lowers again with its rocket.</summary>
    Aborting = 9,
}

/// <summary>Why a proposal was dropped (for the log and the crew's message).</summary>
public enum ScudCancel : byte { Retarget, CrewChanged, Withdrawn, Moved, Timeout }

/// <summary>What a command to the launcher got.</summary>
public enum ScudAnswer : byte
{
    Ok = 0,
    /// <summary>The launcher is not in a state this command works in.</summary>
    WrongState,
    /// <summary>Only the player in the operator seat may do this.</summary>
    NotOperator,
    /// <summary>The player is not in a seat of the truck.</summary>
    NotAboard,
    /// <summary>The point is nearer than the minimum range.</summary>
    TooClose,
    /// <summary>The point is off the map.</summary>
    OffMap,
    /// <summary>The truck is still moving: it must stand still to erect.</summary>
    Moving,
}

/// <summary>The occupants of a truck's seats, by seat index (0 = free), and which seat is the operator's.</summary>
public sealed record ScudCrew(IReadOnlyList<ushort> Seats, int OperatorSeat)
{
    /// <summary>The player in the operator seat; 0 when it is free.</summary>
    public ushort Operator => OperatorSeat >= 0 && OperatorSeat < Seats.Count ? Seats[OperatorSeat] : (ushort)0;

    /// <summary>Bit i set when seat i is taken.</summary>
    public int OccupiedMask
    {
        get
        {
            var m = 0;
            for (var i = 0; i < Seats.Count; i++) if (Seats[i] != 0) m |= 1 << i;
            return m;
        }
    }

    /// <summary>The seat of a player, or -1.</summary>
    public int SeatOf(ushort player)
    {
        if (player == 0) return -1;
        for (var i = 0; i < Seats.Count; i++) if (Seats[i] == player) return i;
        return -1;
    }

    /// <summary>The same players in the same seats.</summary>
    public bool SameAs(ScudCrew other)
    {
        if (other.Seats.Count != Seats.Count) return false;
        for (var i = 0; i < Seats.Count; i++) if (Seats[i] != other.Seats[i]) return false;
        return true;
    }
}

/// <summary>What the host must do after a <see cref="ScudLauncher.Tick"/>: spawn the missile when the ignition ends.</summary>
public readonly record struct ScudTick(bool Launch, Vector3 Target, ushort Placer);

/// <summary>
/// The launcher of one Scud truck: the machine of <see cref="ScudLauncherState"/> with its clock, and what rides with it (who is the operator, the locked point, who has confirmed).
/// Pure, so the tests run the server's own rules. The host (<c>NetServer.Scud.cs</c>) feeds it commands and a tick with the crew and the truck's place, and acts on what it answers.
/// <para>
/// A proposal needs every occupied seat's confirmation, the operator's included (the lock is the proposal, not the order); a solo player confirms from the operator seat alone.
/// It is dropped when the crew changes (anyone leaves, joins or changes seat), a confirmer withdraws, the truck moves, the operator locks again or closes the screen, or it times out.
/// </para>
/// </summary>
public sealed class ScudLauncher
{
    public static readonly StateMachineSpec<ScudLauncherState> Spec = StateMachineSpec<ScudLauncherState>.Define(
            "Scud.Launcher", "A Scud truck's launcher: the rocket on its rack, the operator's lock, the crew's confirmations, the rack rising, the launch, the empty rack and the reload.", ScudLauncherState.Stowed)
        .Allow(ScudLauncherState.Stowed, ScudLauncherState.Targeting)
        .Allow(ScudLauncherState.Targeting, ScudLauncherState.Stowed, ScudLauncherState.Proposed)
        .Allow(ScudLauncherState.Proposed, ScudLauncherState.Targeting, ScudLauncherState.Stowed, ScudLauncherState.Erecting)
        .Allow(ScudLauncherState.Erecting, ScudLauncherState.Launching, ScudLauncherState.Aborting)
        .Allow(ScudLauncherState.Launching, ScudLauncherState.Spent)
        .Allow(ScudLauncherState.Spent, ScudLauncherState.Lowering)
        .Allow(ScudLauncherState.Lowering, ScudLauncherState.Empty)
        .Allow(ScudLauncherState.Empty, ScudLauncherState.Reloading, ScudLauncherState.Stowed)
        .Allow(ScudLauncherState.Reloading, ScudLauncherState.Stowed)
        .Allow(ScudLauncherState.Aborting, ScudLauncherState.Stowed)
        .Note(ScudLauncherState.Stowed, "A rocket on the rack, the rack down, the truck drives. Where a launcher starts and where every cancel ends when nobody has the screen open.")
        .Note(ScudLauncherState.Targeting, "The operator (the player in the operator seat) has the targeting screen open (clock: none). Closing it, or leaving the seat, goes back to Stowed.")
        .Note(ScudLauncherState.Proposed, "A point is locked (the placer is the operator). Every occupied seat must confirm (clock: the proposal timeout). Any cancel goes back to Targeting while the operator is still at the screen, else Stowed.")
        .Note(ScudLauncherState.Erecting, "Everybody confirmed. The truck is immobilised (brakes on, the driver's input ignored) and the rack rises (clock: erect seconds). The operator may abort until the ignition.")
        .Note(ScudLauncherState.Launching, "The rack is up and the rocket ignites (clock: ignition seconds). When it runs out the missile entity spawns and the rack is empty. Cannot be aborted.")
        .Note(ScudLauncherState.Spent, "The rack is up and empty for a moment (clock: spent hold seconds), the launch smoke clears, then the rack lowers.")
        .Note(ScudLauncherState.Lowering, "The empty rack lowers (clock: lower seconds). The truck is still immobilised.")
        .Note(ScudLauncherState.Empty, "The rack is down with no rocket. The truck drives again but cannot fire until a reload: the rocket cart's TryReloadScud, or an admin's instant reload (Empty to Stowed).")
        .Note(ScudLauncherState.Reloading, "A rocket is being loaded (clock: reload seconds). The truck is immobilised; the rocket shows on the rack for the second half.")
        .Note(ScudLauncherState.Aborting, "The operator aborted the erect: the rack lowers again from where it got to, at the lowering rate, and the rocket is kept. Then Stowed.")
        .Build();

    readonly ScudLauncherData _t;
    readonly StateMachine<ScudLauncherState> _fsm = new(Spec);
    ScudCrew? _crewAtLock;
    Vector3 _lockedFrom;
    int _confirmed;

    public ScudLauncher(ScudLauncherData tuning) => _t = tuning;

    public ScudLauncherState State => _fsm.Current;
    /// <summary>Seconds since the state was entered.</summary>
    public float Elapsed => _fsm.Elapsed;
    /// <summary>Seconds of the state's countdown left (infinity: none).</summary>
    public float Left => _fsm.Left;
    /// <summary>A proposal was dropped (and why); the state has already gone back to Targeting or Stowed.</summary>
    public event Action<ScudCancel>? ProposalDropped;

    /// <summary>The state changed (from, to), for the host to send it on.</summary>
    public event Action<ScudLauncherState, ScudLauncherState>? Changed { add => _fsm.Changed += value; remove => _fsm.Changed -= value; }

    /// <summary>The player who has the screen open (or locked the point): the one the kills are credited to. 0 when none.</summary>
    public ushort Operator { get; private set; }
    /// <summary>The locked point (ground), in Proposed and from there to the launch.</summary>
    public Vector3? Lock { get; private set; }
    /// <summary>Seats that have confirmed (bit i is seat i).</summary>
    public int Confirmed => _confirmed;
    /// <summary>Seats that must confirm: those occupied when the point was locked.</summary>
    public int Need => _crewAtLock?.OccupiedMask ?? 0;
    /// <summary>The rack's angle when the state was entered, radians (what <see cref="ScudLauncherState.Aborting"/> lowers from).</summary>
    public float RackFrom { get; private set; }

    /// <summary>The rack's angle now, radians.</summary>
    public float RackAngle => ScudRules.RackAngle(State, Elapsed, RackFrom, _t);

    /// <summary>The state clears the truck's driver: Erecting and everything until the rack is down and loaded again.</summary>
    public bool Immobilised => ScudRules.Immobilises(State);

    // ---- commands ----

    /// <summary>The operator opens the targeting screen.</summary>
    public ScudAnswer Open(ScudCrew crew, ushort player)
    {
        if (crew.SeatOf(player) < 0) return ScudAnswer.NotAboard;
        if (crew.Operator != player) return ScudAnswer.NotOperator;
        if (State != ScudLauncherState.Stowed) return ScudAnswer.WrongState;
        Operator = player;
        _fsm.Enter(ScudLauncherState.Targeting);
        return ScudAnswer.Ok;
    }

    /// <summary>The operator closes the screen: the lock, if any, is dropped.</summary>
    public ScudAnswer Close(ScudCrew crew, ushort player)
    {
        if (crew.SeatOf(player) < 0) return ScudAnswer.NotAboard;
        if (crew.Operator != player) return ScudAnswer.NotOperator;
        if (State is not (ScudLauncherState.Targeting or ScudLauncherState.Proposed)) return ScudAnswer.WrongState;
        ToStowed();
        return ScudAnswer.Ok;
    }

    /// <summary>
    /// The operator locks <paramref name="point"/> (from Targeting, or again in Proposed, which drops the confirmations and proposes the new point). Nearer than
    /// the minimum range, or off the map, it is refused and nothing changes.
    /// </summary>
    public ScudAnswer PlaceLock(ScudCrew crew, ushort player, Vector3 point, Vector3 truckPos, bool onMap)
    {
        if (crew.SeatOf(player) < 0) return ScudAnswer.NotAboard;
        if (crew.Operator != player) return ScudAnswer.NotOperator;
        if (State is not (ScudLauncherState.Targeting or ScudLauncherState.Proposed)) return ScudAnswer.WrongState;
        if (!onMap) return ScudAnswer.OffMap;
        if (ScudRules.Range(truckPos, point) < _t.MinRangeM) return ScudAnswer.TooClose;
        if (State == ScudLauncherState.Proposed) Cancel(ScudCancel.Retarget, crew); // back to Targeting; the new point proposes at once
        Lock = point;
        Operator = player;
        _crewAtLock = crew;
        _lockedFrom = truckPos;
        _confirmed = 0;
        _fsm.Enter(ScudLauncherState.Proposed, (float)_t.ProposalTimeoutSeconds);
        return ScudAnswer.Ok;
    }

    /// <summary>
    /// A seated player confirms the proposal. The last one in starts erecting, if the truck stands still (<paramref name="truckSpeed"/>); a moving truck refuses
    /// and the confirmation is not counted.
    /// </summary>
    public ScudAnswer Confirm(ScudCrew crew, ushort player, float truckSpeed)
    {
        var seat = crew.SeatOf(player);
        if (seat < 0) return ScudAnswer.NotAboard;
        if (State != ScudLauncherState.Proposed) return ScudAnswer.WrongState;
        if ((Need & (1 << seat)) == 0) return ScudAnswer.WrongState; // sat down after the lock: the crew changed, the tick drops the proposal
        if (MathF.Abs(truckSpeed) > _t.MaxSpeedMps) return ScudAnswer.Moving;
        _confirmed |= 1 << seat;
        if ((_confirmed & Need) == Need)
        {
            RackFrom = 0;
            _fsm.Enter(ScudLauncherState.Erecting, (float)_t.ErectSeconds);
        }
        return ScudAnswer.Ok;
    }

    /// <summary>A player who confirmed takes it back: the whole proposal is dropped (see <see cref="ScudCancel.Withdrawn"/>).</summary>
    public ScudAnswer Withdraw(ScudCrew crew, ushort player)
    {
        var seat = crew.SeatOf(player);
        if (seat < 0) return ScudAnswer.NotAboard;
        if (State != ScudLauncherState.Proposed || (_confirmed & (1 << seat)) == 0) return ScudAnswer.WrongState;
        Cancel(ScudCancel.Withdrawn, crew);
        return ScudAnswer.Ok;
    }

    /// <summary>The operator aborts while the rack is rising: it lowers again from where it got to and the rocket is kept.</summary>
    public ScudAnswer Abort(ScudCrew crew, ushort player)
    {
        if (crew.SeatOf(player) < 0) return ScudAnswer.NotAboard;
        if (crew.Operator != player) return ScudAnswer.NotOperator;
        if (State != ScudLauncherState.Erecting) return ScudAnswer.WrongState;
        var from = RackAngle;
        RackFrom = from;
        ClearLock();
        _fsm.Enter(ScudLauncherState.Aborting, ScudRules.LowerSeconds(from, _t));
        return ScudAnswer.Ok;
    }

    /// <summary>Empty to Reloading (the rocket cart's job, <c>TryReloadScud</c>). False in any other state.</summary>
    public bool BeginReload()
    {
        if (State != ScudLauncherState.Empty) return false;
        _fsm.Enter(ScudLauncherState.Reloading, (float)_t.ReloadSeconds);
        return true;
    }

    /// <summary>An admin's instant reload: Empty, or a reload under way, is Stowed at once. False in any other state.</summary>
    public bool ReloadNow()
    {
        if (State is not (ScudLauncherState.Empty or ScudLauncherState.Reloading)) return false;
        ToStowed();
        return true;
    }

    // ---- the clock ----

    /// <summary>
    /// One server tick. Drops a proposal the crew or the truck broke, steps the countdowns, and tells the host to spawn the missile when the ignition ran out.
    /// </summary>
    public ScudTick Tick(float dt, ScudCrew crew, Vector3 truckPos)
    {
        switch (State)
        {
            case ScudLauncherState.Targeting:
                if (crew.Operator != Operator || Operator == 0) ToStowed();
                break;
            case ScudLauncherState.Proposed:
                _fsm.Tick(dt);
                if (_crewAtLock != null && !crew.SameAs(_crewAtLock)) Cancel(ScudCancel.CrewChanged, crew);
                else if (ScudRules.Range(truckPos, _lockedFrom) > _t.MovedCancelM) Cancel(ScudCancel.Moved, crew);
                else if (_fsm.Expired) Cancel(ScudCancel.Timeout, crew);
                break;
            case ScudLauncherState.Erecting:
                if (_fsm.Tick(dt)) _fsm.Enter(ScudLauncherState.Launching, (float)_t.IgnitionSeconds);
                break;
            case ScudLauncherState.Launching:
                if (_fsm.Tick(dt))
                {
                    var order = new ScudTick(true, Lock ?? Vector3.Zero, Operator);
                    ClearLock();
                    Operator = 0;
                    _fsm.Enter(ScudLauncherState.Spent, (float)_t.SpentHoldSeconds);
                    return order;
                }
                break;
            case ScudLauncherState.Spent:
                if (_fsm.Tick(dt)) _fsm.Enter(ScudLauncherState.Lowering, (float)_t.LowerSeconds);
                break;
            case ScudLauncherState.Lowering:
                if (_fsm.Tick(dt)) _fsm.Enter(ScudLauncherState.Empty);
                break;
            case ScudLauncherState.Aborting:
            case ScudLauncherState.Reloading:
                if (_fsm.Tick(dt)) ToStowed();
                break;
        }
        return default;
    }

    // ---- ends ----

    void Cancel(ScudCancel why, ScudCrew crew)
    {
        var atScreen = crew.Operator == Operator && Operator != 0;
        ClearLock();
        if (atScreen) _fsm.Enter(ScudLauncherState.Targeting);
        else ToStowed();
        ProposalDropped?.Invoke(why);
    }

    void ToStowed()
    {
        ClearLock();
        Operator = 0;
        _fsm.Enter(ScudLauncherState.Stowed);
    }

    void ClearLock()
    {
        Lock = null; _crewAtLock = null; _confirmed = 0;
    }
}

/// <summary>The launcher's pure rules: the rack's angle, what it immobilises, the range, the grid-free helpers the host and the views share.</summary>
public static class ScudRules
{
    /// <summary>The rack stands this far from flat when raised: straight up.</summary>
    public const float RackMax = MathF.PI / 2f;

    /// <summary>States in which the truck's brakes are on and its driver's input is ignored.</summary>
    public static bool Immobilises(ScudLauncherState s) =>
        s is ScudLauncherState.Erecting or ScudLauncherState.Launching or ScudLauncherState.Spent or ScudLauncherState.Lowering or ScudLauncherState.Aborting or ScudLauncherState.Reloading;

    /// <summary>Horizontal distance between two points, metres.</summary>
    public static float Range(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Seconds an abort from <paramref name="fromRad"/> takes: the lowering rate of <see cref="ScudLauncherData.LowerSeconds"/>.</summary>
    public static float LowerSeconds(float fromRad, ScudLauncherData t) => MathF.Max(0.25f, (float)t.LowerSeconds * Math.Clamp(fromRad / RackMax, 0f, 1f));

    /// <summary>
    /// The rack's angle (radians up from flat, 0 to <see cref="RackMax"/>) in a state <paramref name="elapsed"/> seconds after it began. <paramref name="fromRad"/> is the angle an
    /// <see cref="ScudLauncherState.Aborting"/> lowers from (the wire carries it). Smooth at both ends. Clients animate the model's <c>rack</c> node with it.
    /// </summary>
    public static float RackAngle(ScudLauncherState s, float elapsed, float fromRad, ScudLauncherData t) => s switch
    {
        ScudLauncherState.Erecting => RackMax * Smooth(elapsed / (float)t.ErectSeconds),
        ScudLauncherState.Launching or ScudLauncherState.Spent => RackMax,
        ScudLauncherState.Lowering => RackMax * (1f - Smooth(elapsed / (float)t.LowerSeconds)),
        ScudLauncherState.Aborting => fromRad * (1f - Smooth(elapsed / LowerSeconds(fromRad, t))),
        _ => 0f,
    };

    /// <summary>Whether the rocket is drawn on the rack (the model's <c>missile</c> node): from the stowed rocket to the ignition, an aborted one, and a reload's second half.</summary>
    public static bool RocketOnRack(ScudLauncherState s, float elapsed, ScudLauncherData t) => s switch
    {
        ScudLauncherState.Stowed or ScudLauncherState.Targeting or ScudLauncherState.Proposed or ScudLauncherState.Erecting or ScudLauncherState.Launching or ScudLauncherState.Aborting => true,
        ScudLauncherState.Reloading => elapsed >= (float)t.ReloadSeconds * 0.5f,
        _ => false,
    };

    /// <summary>
    /// The angle a steered axle's wheels take, radians (positive: to the left, as Godot's Y turn): the truck turns about a point on the line of its fixed axles, so a wheel
    /// <paramref name="arm"/> metres ahead of that line turns by atan(arm x curvature). <paramref name="curvature"/> is the path's, 1 / radius, positive to the left, negative reversing into
    /// the same wheel angle (the caller signs it by the direction of travel). Held to <paramref name="max"/>.
    /// </summary>
    public static float WheelAngle(float arm, float curvature, float max = 0.6f) => Math.Clamp(MathF.Atan(arm * curvature), -max, max);

    /// <summary>Whether the screen may be opened in this state (the operator's key, the driver's feed).</summary>
    public static bool ScreenAvailable(ScudLauncherState s) => s is ScudLauncherState.Stowed or ScudLauncherState.Targeting or ScudLauncherState.Proposed;

    /// <summary>Whether the operator's camera and lock are shown to the crew (the feed).</summary>
    public static bool FeedLive(ScudLauncherState s) => s is ScudLauncherState.Targeting or ScudLauncherState.Proposed;
}

using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>Where a Gang member's progress check is (machine <c>Thieves.Stuck</c>).</summary>
public enum StuckPhase : byte { Off, Watching, Rerouted }

/// <summary>What a stuck member must do now.</summary>
public enum StuckAction : byte { None, Reroute, Snap }

/// <summary>
/// The progress check of a Gang member that is trying to get somewhere (thieves.json <c>gang.stuck</c>): under <c>stuck_m</c> covered in <c>stuck_seconds</c> is stuck and the member drops its route and takes
/// another spot round its target or a fresh route (<see cref="StuckAction.Reroute"/>); still no <c>stuck_m</c> of progress at <c>snap_seconds</c> from the first stand-still, it is moved to the nearest walkable
/// point of the navigation mesh (<see cref="StuckAction.Snap"/>). Progress is the flat distance from where the check last saw it move, so a member pacing back and forth in place counts as stuck. Pure, one clock.
/// </summary>
public sealed class StuckWatch
{
    public static readonly StateMachineSpec<StuckPhase> Spec = StateMachineSpec<StuckPhase>
        .Define("Thieves.Stuck", "The progress check of a Gang member trying to get somewhere: watching while it moves, rerouted once it made no stuck_m in stuck_seconds, then moved to the nearest walkable point if it still has not by snap_seconds.", StuckPhase.Off)
        .Allow(StuckPhase.Off, StuckPhase.Watching)
        .Allow(StuckPhase.Watching, StuckPhase.Rerouted, StuckPhase.Off)
        .Allow(StuckPhase.Rerouted, StuckPhase.Watching, StuckPhase.Off)
        .Note(StuckPhase.Off, "The member is not trying to get anywhere (standing, fighting, working): no check.")
        .Note(StuckPhase.Watching, "Trying to get somewhere (clock: stuck_seconds from where it last made stuck_m of progress). Moving on resets the clock; at its end the member is stuck: Reroute.")
        .Note(StuckPhase.Rerouted, "Its route was dropped for another spot or a fresh route (clock: snap_seconds less stuck_seconds). Progress goes back to Watching; at the clock's end it is moved to the nearest walkable point (Snap) and watched again.")
        .Build();

    readonly StateMachine<StuckPhase> _fsm = new(Spec);
    readonly GangStuck _d;
    Vector3 _anchor;

    public StuckWatch(GangStuck cfg) => _d = cfg;

    public StuckPhase Phase => _fsm.Current;

    /// <summary>One tick: where the member stands and whether it is trying to get somewhere now. What it must do, if anything.</summary>
    public StuckAction Step(float dt, Vector3 position, bool trying)
    {
        if (!trying)
        {
            _fsm.Enter(StuckPhase.Off);
            return StuckAction.None;
        }
        if (_fsm.Is(StuckPhase.Off))
        {
            _fsm.Enter(StuckPhase.Watching, _d.StuckSeconds);
            _anchor = position;
            return StuckAction.None;
        }
        if (Flat(position, _anchor) >= _d.StuckM)
        {
            _anchor = position;
            if (!_fsm.Enter(StuckPhase.Watching, _d.StuckSeconds)) _fsm.Restart(_d.StuckSeconds);
            return StuckAction.None;
        }
        if (!_fsm.Tick(dt)) return StuckAction.None;
        if (_fsm.Is(StuckPhase.Watching))
        {
            _fsm.Enter(StuckPhase.Rerouted, _d.SnapSeconds - _d.StuckSeconds);
            return StuckAction.Reroute;
        }
        _anchor = position; // snapped: where it stands now is where progress is measured from
        _fsm.Enter(StuckPhase.Watching, _d.StuckSeconds);
        return StuckAction.Snap;
    }

    /// <summary>The member was moved by something else (the snap, a teleport): progress is measured from where it stands now.</summary>
    public void Moved(Vector3 position) => _anchor = position;
}

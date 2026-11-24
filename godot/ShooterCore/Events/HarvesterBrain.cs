using System.Numerics;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Events;

/// <summary>Where the AI harvester is in its job. The values travel in the harvester state event (<see cref="HarvesterCodec"/>), so they never change.</summary>
public enum HarvesterPhase : byte
{
    /// <summary>Drives from where it appeared to the start of the first pass.</summary>
    Approach = 0,
    /// <summary>Cuts a pass (header down, reel turning).</summary>
    Cut = 1,
    /// <summary>Turns on the headland to the next pass.</summary>
    Turn = 2,
    /// <summary>Done cutting: along the edge out of the field.</summary>
    Leave = 3,
    /// <summary>Left the field: the machine is taken away and the cooldown starts.</summary>
    Gone = 4,
    /// <summary>Lost its driver (killed) or was cancelled: nobody drives it any more; the vehicle stays for whoever takes the seat.</summary>
    Stopped = 5,
}

/// <summary>One tick of the AI's driving: what the driver would press, and what the header is doing.</summary>
public readonly record struct HarvesterCommand(float Throttle, float Steer, bool Cutting, HarvestProgress Progress);

/// <summary>
/// The AI driver of the harvester (#331): follows the legs of a <see cref="HarvestPlan"/> (a pass, a turn, the way out) by steering a car model, as a person at the wheel would.
/// Pure: the host gives it where the machine is (position, yaw, speed on the XZ plane) each tick and presses the returned throttle and steer. It does not know the world: a leg
/// it cannot finish (stuck on a ditch, a tree) is given up after <see cref="HarvesterConfig.LegTimeoutS"/> and the next leg is driven.
/// </summary>
public sealed class HarvesterBrain
{
    /// <summary>The AI's machine. <see cref="Gone"/> and <see cref="Stopped"/> are its ends.</summary>
    public static readonly StateMachineSpec<HarvesterPhase> Spec = StateMachineSpec<HarvesterPhase>
        .Define("Harvester.Ai", "The AI harvester's job: drive to the field's first pass, cut the passes with turns between, leave along the edge; stops when its driver is killed.", HarvesterPhase.Approach)
        .Allow(HarvesterPhase.Approach, HarvesterPhase.Cut, HarvesterPhase.Leave)
        .Allow(HarvesterPhase.Cut, HarvesterPhase.Turn, HarvesterPhase.Leave)
        .Allow(HarvesterPhase.Turn, HarvesterPhase.Cut, HarvesterPhase.Leave)
        .Allow(HarvesterPhase.Leave, HarvesterPhase.Gone)
        .FromAnywhere(HarvesterPhase.Stopped)
        .Note(HarvesterPhase.Approach, "Appeared just inside the field's outline, facing the first pass.")
        .Note(HarvesterPhase.Cut, "Driving a pass at cutting speed; the cut progress (pass, metres) is what the clients draw the stubble from.")
        .Note(HarvesterPhase.Turn, "A half circle to a lane several lanes over (the lanes between are cut on the way back), or a bulb turn of three arcs for the last lanes: never tighter than the machine's turning radius.")
        .Note(HarvesterPhase.Leave, "The edge route to where it came in, then past it. A plan whose passes were all given up goes straight here.")
        .Note(HarvesterPhase.Gone, "Reached the exit: the event is over (cooldown starts).")
        .Note(HarvesterPhase.Stopped, "Its driver died, or an admin ended the event: from any phase. The brain gives no more commands.")
        .Build();

    readonly HarvestPlan _plan;
    readonly HarvesterConfig _cfg;
    readonly StateMachine<HarvesterPhase> _fsm = new(Spec);
    int _leg;
    float _legTime;
    HarvestProgress _progress = HarvestProgress.None;

    public HarvesterBrain(HarvestPlan plan, HarvesterConfig cfg)
    {
        _plan = plan; _cfg = cfg;
        _fsm.Enter(plan.Legs[0].Phase); // Approach: the initial state, no change
    }

    public HarvesterPhase Phase => _fsm.Current;
    public bool Finished => _fsm.IsAny(HarvesterPhase.Gone, HarvesterPhase.Stopped);
    public HarvestProgress Progress => _progress;
    public HarvestPlan Plan => _plan;
    public int LegIndex => _leg;

    /// <summary>The driver is gone (killed) or the event was cancelled.</summary>
    public void Stop() => _fsm.Enter(HarvesterPhase.Stopped);

    /// <summary>One tick. <paramref name="pos"/> is the machine's origin on the XZ plane (X, Z), <paramref name="yaw"/> as the game's yaw, <paramref name="speed"/> signed along its facing.</summary>
    public HarvesterCommand Tick(float dt, Vector2 pos, float yaw, float speed)
    {
        if (Finished) return new HarvesterCommand(0, 0, false, _progress);
        var fwd = FieldPlanner.Forward(yaw);
        var header = pos + fwd * _cfg.HeaderForwardM;
        _legTime += dt;
        var leg = _plan.Legs[_leg];
        var arrive = leg.Phase switch { HarvesterPhase.Cut => _cfg.ArriveM * 0.7f, HarvesterPhase.Turn => _cfg.ArriveM * 0.45f, _ => _cfg.ArriveM };
        var reached = Vector2.Distance(leg.Phase == HarvesterPhase.Cut ? header : pos, leg.Target) <= arrive;
        if (reached || _legTime > _cfg.LegTimeoutS)
        {
            if (leg.Phase == HarvesterPhase.Cut && reached) _progress = new HarvestProgress(leg.Pass, _plan.Passes[leg.Pass].Length); // the pass is done to its end
            if (++_leg >= _plan.Legs.Count) { _fsm.Enter(HarvesterPhase.Gone); return new HarvesterCommand(0, 0, false, _progress); }
            _legTime = 0;
            leg = _plan.Legs[_leg];
            _fsm.Enter(leg.Phase);
        }
        var cutting = false;
        if (leg.Phase == HarvesterPhase.Cut)
        {
            var pass = _plan.Passes[leg.Pass];
            var lateral = MathF.Abs(Cross(pass.Dir, header - pass.Start));
            cutting = lateral <= _cfg.HeaderWidthM * 0.6f;
            if (cutting) _progress = HarvestProgress.Advance(_progress, _plan.Passes, leg.Pass, header);
        }
        var (throttle, steer) = HarvesterPilot.Drive(pos, yaw, speed, leg.Target, leg.SpeedMps, _cfg.TurnRadiusM);
        return new HarvesterCommand(throttle, steer, cutting, _progress);
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}

/// <summary>Steering a car-model vehicle towards a point (the arcade model of <c>CarModel</c>: steer +1 is right, yaw grows turning left).</summary>
public static class HarvesterPilot
{
    /// <summary>Throttle and steer (-1..1) to drive from <paramref name="pos"/> facing <paramref name="yaw"/> at <paramref name="speed"/> to <paramref name="target"/> at about <paramref name="wantSpeed"/>.</summary>
    public static (float Throttle, float Steer) Drive(Vector2 pos, float yaw, float speed, Vector2 target, float wantSpeed, float radiusM = 7f)
    {
        var d = target - pos;
        if (d.LengthSquared() < 1e-6f) return (0, 0);
        var err = FieldPlanner.Wrap(FieldPlanner.YawOf(d) - yaw); // positive: the target is to the left
        // pure pursuit: the curvature that reaches a point ld ahead is 2 sin(err) / ld, and steer 1 is the machine's tightest radius (CarModel: yaw rate = steer * v / R)
        var ld = Math.Clamp(d.Length(), 2f, 8f);
        var steer = MathF.Abs(err) > MathF.PI / 2f ? -MathF.Sign(err) : Math.Clamp(-2f * MathF.Sin(err) / ld * radiusM, -1f, 1f);
        var ease = Math.Clamp(1.2f - MathF.Abs(err) / 1.3f, 0.3f, 1f); // slow for a sharp turn
        var want = wantSpeed * ease;
        var throttle = Math.Clamp((want - speed) * 0.9f, -1f, 1f);
        return (throttle, steer);
    }
}

using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Turrets;

namespace Drycastle.ShooterCore.Companions;

/// <summary>What a <see cref="WingCompanion"/> is doing.</summary>
public enum WingState : byte { Hover, Trail, Circle }

/// <summary>The numbers of the "wing" kind (the <c>rules</c> of its companions.json entry). Lengths in metres, times in seconds, speeds in m/s.</summary>
public class WingRules
{
    /// <summary>Its perch: this high over its owner's feet, this far behind them and this far to their left.</summary>
    public float HeightM { get; set; } = 2.2f;
    public float BehindM { get; set; } = 1.2f;
    public float SideM { get; set; } = 0.8f;
    /// <summary>The perch farther than this: it leaves its hover and flies after it; within <see cref="ArriveM"/> of a perch that has come to rest it hovers again.</summary>
    public float FollowM { get; set; } = 2.5f;
    public float ArriveM { get; set; } = 0.5f;
    /// <summary>How fast it drifts with its perch while it hovers (a perch that moves faster leaves it behind, and it flies).</summary>
    public float DriftSpeed { get; set; } = 1.5f;
    public float FlySpeed { get; set; } = 10f;
    /// <summary>Its owner farther than this: it is put beside them at once.</summary>
    public float RecallM { get; set; } = 40f;
    /// <summary>Never lower than this over the ground.</summary>
    public float ClearanceM { get; set; } = 0.8f;
    /// <summary>Now and then (a roll between these) it flies circles round its owner. Both 0: never.</summary>
    public float CircleEveryMinSeconds { get; set; } = 20f;
    public float CircleEveryMaxSeconds { get; set; } = 45f;
    public float CircleRadiusM { get; set; } = 3f;
    public float CircleSpeed { get; set; } = 6f;
    public float CircleTurns { get; set; } = 1f;
    /// <summary>How high over its owner's feet it circles; 0: at <see cref="HeightM"/>. It climbs there as the circle widens.</summary>
    public float CircleHeightM { get; set; }

    public virtual void Check(string what)
    {
        if (!(HeightM > 0) || BehindM < 0 || !(ClearanceM >= 0)) throw new DataFileException($"{what}: height_m is positive, behind_m and clearance_m are not negative");
        if (!(ArriveM > 0) || !(FollowM > ArriveM) || !(RecallM > FollowM)) throw new DataFileException($"{what}: 0 < arrive_m < follow_m < recall_m");
        if (!(FlySpeed > 0) || !(DriftSpeed > 0) || DriftSpeed > FlySpeed) throw new DataFileException($"{what}: fly_speed is positive and drift_speed is positive and not above it");
        if (CircleEveryMinSeconds < 0 || CircleEveryMaxSeconds < CircleEveryMinSeconds) throw new DataFileException($"{what}: circle_every_min_seconds is not negative and not above circle_every_max_seconds");
        if (CircleEveryMaxSeconds > 0 && (!(CircleRadiusM > 0) || !(CircleSpeed > 0) || !(CircleTurns > 0))) throw new DataFileException($"{what}: circle_radius_m, circle_speed and circle_turns must be positive");
        if (CircleHeightM < 0) throw new DataFileException($"{what}: circle_height_m is 0 (the perch height) or positive");
    }
}

/// <summary>
/// The "wing" kind: a flying Companion. It hovers at a perch over its owner's shoulder, flies after the perch when they move, and now and then flies circles round them. Nothing
/// stops it (it does not land and meets no wall); it is put beside its owner when it falls too far behind. All of its rules are here.
/// A kind that flies the same way and does more (<see cref="ScoutCompanion"/>) derives from it and adds its own rules at the circle (<see cref="OnCircleStart"/>,
/// <see cref="Beside"/>).
/// </summary>
public class WingCompanion : Companion
{
    public static readonly CompanionKind Kind = new("wing", s => new WingCompanion(s), d => RulesOf(d));

    public static readonly StateMachineSpec<WingState> Spec = StateMachineSpec<WingState>.Define(
            "Companion.Wing", "What a flying Companion of the wing kind is doing: hover at its perch over its owner, fly after the perch, or fly circles round its owner. Falling too far behind puts it at its perch at once (no state: it then hovers).", WingState.Hover)
        .Allow(WingState.Hover, WingState.Trail, WingState.Circle)
        .Allow(WingState.Trail, WingState.Hover, WingState.Circle)
        .Allow(WingState.Circle, WingState.Trail, WingState.Hover)
        .Note(WingState.Hover, "Hangs at its perch (height_m over the owner, behind_m behind, side_m to the left), drifting with it at up to drift_speed and facing as its owner does; no clock. The perch farther than follow_m (its owner moves faster than it drifts): Trail. The circle clock (beside the machine, a roll of circle_every seconds) runs out: Circle.")
        .Note(WingState.Trail, "Flies to the perch at up to fly_speed and keeps with it while its owner moves; no clock. Within arrive_m of a perch that has come to rest (slower than drift_speed): Hover. The circle clock runs out: Circle. Its owner farther than recall_m: put at its perch, Hover.")
        .Note(WingState.Circle, "Flies circle_turns turns round its owner at circle_radius_m and circle_speed, climbing to circle_height_m (0: staying at perch height) as it widens (clock: the time the turns take), then Trail back to the perch. Its owner farther than recall_m: put at its perch, Hover.")
        .Build();

    readonly StateMachine<WingState> _fsm = new(Spec);
    readonly WingRules _rules;
    Countdown _nextCircle;
    float _angle, _radius, _height; // where on its circle it is, how far out and how high (it spirals out and up to the circle)
    int _dir = 1;
    Vector3 _lastPerch; // where the perch was a tick ago: how fast it moves

    static WingRules RulesOf(CompanionDef def)
    {
        var r = DataFile.Section<WingRules>(def.Rules, $"companions.json: {def.Id}: rules");
        r.Check($"companions.json: {def.Id}: rules");
        return r;
    }

    WingCompanion(CompanionSpawn s) : this(s, RulesOf(s.Def)) { }

    protected WingCompanion(CompanionSpawn s, WingRules rules) : base(s)
    {
        _rules = rules;
        Position = _lastPerch = Perch;
        Pose = CompanionPose.Hover;
        ScheduleCircle();
    }

    public WingState State => _fsm.Current;
    public override string StateName => State.ToString();

    /// <summary>Where it hangs: over its owner's shoulder, never lower than the clearance over the ground there.</summary>
    Vector3 Perch
    {
        get
        {
            var (s, c) = MathF.SinCos(Master.Yaw);
            // forward is (-s, 0, -c), left is (-c, 0, s)
            var p = Master.Position + new Vector3(s, 0, c) * _rules.BehindM + new Vector3(-c, 0, s) * _rules.SideM + Vector3.UnitY * _rules.HeightM;
            if (World.GroundY(p.X, p.Z) is { } ground && p.Y < ground + _rules.ClearanceM) p.Y = ground + _rules.ClearanceM;
            return p;
        }
    }

    protected override void Think(float dt)
    {
        if (OwnerDistance > _rules.RecallM)
        {
            Position = _lastPerch = Perch; Yaw = Master.Yaw;
            if (State == WingState.Circle) ScheduleCircle(); // cut short: the next one is a while off, as after a whole one
            _fsm.Enter(WingState.Hover);
        }
        var perch = Perch;
        var perchSpeed = Vector3.Distance(perch, _lastPerch) / dt;
        _lastPerch = perch;
        Beside(dt);
        var circleDue = State != WingState.Circle && _rules.CircleEveryMaxSeconds > 0 && _nextCircle.Tick(dt);
        switch (State)
        {
            case WingState.Hover:
                if (circleDue) { StartCircle(); break; }
                if (Vector3.Distance(Position, perch) > _rules.FollowM) { _fsm.Enter(WingState.Trail); break; }
                Drift(perch, dt);
                break;
            case WingState.Trail:
                if (circleDue) { StartCircle(); break; }
                // slows as it closes in, so it does not overshoot a perch that moves with its owner
                var speed = MathF.Min(_rules.FlySpeed, MathF.Max(_rules.DriftSpeed, Vector3.Distance(Position, perch) * 4f));
                FlyTo(perch, speed, dt, _rules.ClearanceM);
                // it hovers only once the perch rests: beside a walking owner it keeps flying, it does not flap between the two
                if (perchSpeed < _rules.DriftSpeed && Vector3.Distance(Position, perch) <= _rules.ArriveM) _fsm.Enter(WingState.Hover);
                break;
            case WingState.Circle:
                if (_fsm.Tick(dt)) { ScheduleCircle(); _fsm.Enter(WingState.Trail); break; }
                Orbit(dt);
                break;
        }
        Pose = State == WingState.Hover ? CompanionPose.Hover : CompanionPose.Fly;
    }

    /// <summary>Hovering: eases to the perch and turns to face as its owner does.</summary>
    void Drift(Vector3 perch, float dt)
    {
        var to = perch - Position;
        var d = to.Length();
        var step = MathF.Min(d, MathF.Min(_rules.DriftSpeed, MathF.Max(0.3f, d * 3f)) * dt);
        if (d > 1e-4f) Position += to / d * step;
        Yaw = TurretMath.StepAngle(Yaw, Master.Yaw, Animal.TurnRateDegPerS * MathF.PI / 180f * dt);
    }

    void ScheduleCircle() => _nextCircle.Start(Between(_rules.CircleEveryMinSeconds, _rules.CircleEveryMaxSeconds));

    /// <summary>Begins the circles from where it is: at its own bearing from its owner and its own distance, which then widens to the circle's radius.</summary>
    void StartCircle()
    {
        _angle = MathF.Atan2(Position.Z - Master.Position.Z, Position.X - Master.Position.X);
        _radius = Flat(Position, Master.Position);
        _height = Position.Y - Master.Position.Y;
        _dir = Rng.NextDouble() < 0.5 ? 1 : -1;
        var angular = _rules.CircleSpeed / _rules.CircleRadiusM;
        _fsm.Enter(WingState.Circle, _rules.CircleTurns * MathF.Tau / angular);
        OnCircleStart();
    }

    /// <summary>It begins its circles: what a derived kind does then (a scout looks for enemies).</summary>
    protected virtual void OnCircleStart() { }

    /// <summary>Every tick, before it flies, in whatever state: a derived kind's own clocks, beside the flight machine.</summary>
    protected virtual void Beside(float dt) { }

    /// <summary>One step round its owner: the circle is centred on them, so it goes with them as they walk.</summary>
    void Orbit(float dt)
    {
        _angle += _dir * _rules.CircleSpeed / _rules.CircleRadiusM * dt;
        _radius = Ramp.MoveToward(_radius, _rules.CircleRadiusM, _rules.CircleSpeed * 0.5f * dt);
        var (s, c) = MathF.SinCos(_angle);
        _height = Ramp.MoveToward(_height, _rules.CircleHeightM > 0 ? _rules.CircleHeightM : _rules.HeightM, _rules.CircleSpeed * 0.5f * dt);
        var next = new Vector3(Master.Position.X + c * _radius, Master.Position.Y + _height, Master.Position.Z + s * _radius);
        if (World.GroundY(next.X, next.Z) is { } ground && next.Y < ground + _rules.ClearanceM) next.Y = ground + _rules.ClearanceM;
        Position = next;
        Yaw = TurretMath.YawTo(Vector3.Zero, new Vector3(-s, 0, c) * _dir); // along the circle
    }
}

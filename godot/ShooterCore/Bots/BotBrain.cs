using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>The numbers that make a bot easier or harder. Starting values, to be playtested; a data file comes with the fill rules.</summary>
public sealed record BotTuning
{
    /// <summary>Farthest an enemy is seen, metres.</summary>
    public float SightRangeM { get; init; } = 120f;
    /// <summary>Half of the view cone, degrees to each side of where the bot looks.</summary>
    public float ViewHalfAngleDeg { get; init; } = 55f;
    /// <summary>An enemy this close is noticed whatever the bot faces (footsteps), if the line to it is clear.</summary>
    public float NearAwarenessM { get; init; } = 6f;
    /// <summary>Seconds from first sight to the first shot.</summary>
    public float ReactionS { get; init; } = 0.35f;
    /// <summary>How fast the aim turns, degrees per second.</summary>
    public float TurnRateDegPerS { get; init; } = 240f;
    /// <summary>Aim error at first sight, degrees; it shrinks to <see cref="AimErrorMinDeg"/> over <see cref="AimSettleS"/> on the same target.</summary>
    public float AimErrorDeg { get; init; } = 3f;
    public float AimErrorMinDeg { get; init; } = 0.6f;
    public float AimSettleS { get; init; } = 1.2f;
    /// <summary>The bot fires when its aim is within this of where it wants to aim, degrees.</summary>
    public float FireConeDeg { get; init; } = 3f;
    /// <summary>Length of a burst and of the pause after it, seconds (each varies by 30 % either way).</summary>
    public float BurstS { get; init; } = 0.6f;
    public float BurstPauseS { get; init; } = 0.35f;
    /// <summary>How long the bot keeps aiming at where a target went out of sight, seconds.</summary>
    public float MemoryS { get; init; } = 1.5f;
    /// <summary>How long the bot looks toward where it was hit from, seconds.</summary>
    public float AlertS { get; init; } = 2.5f;
    /// <summary>Turn speed while looking around with no target, degrees per second.</summary>
    public float ScanDegPerS { get; init; } = 40f;
    /// <summary>Seconds between looks for enemies (the sight rays); the aim itself moves every tick.</summary>
    public float ThinkS { get; init; } = 0.15f;
    /// <summary>Close enough to where it was going, metres: the bot stops and looks around.</summary>
    public float ArriveM { get; init; } = 3f;
    /// <summary>A point of the route counts as passed within this distance, metres.</summary>
    public float WaypointReachM { get; init; } = 0.8f;
    /// <summary>A route is asked for again after this many seconds, or sooner when the place it led to has moved by <see cref="RepathGoalMovedM"/>.</summary>
    public float RepathS { get; init; } = 4f;
    public float RepathGoalMovedM { get; init; } = 8f;
    /// <summary>The bot sprints while it is farther than this from where it is going, metres.</summary>
    public float SprintBeyondM { get; init; } = 12f;
    /// <summary>
    /// Inside this range the bot stands to shoot; beyond it, it keeps walking toward the fight while it fires. With the aim
    /// error above, shots stop landing much past this distance, and two bots standing farther apart would duel for ever.
    /// </summary>
    public float HoldRangeM { get; init; } = 35f;

    public static readonly BotTuning Default = new();
}

/// <summary>Another player as a bot may see it. Protected: spawn protected, so shots would be absorbed.</summary>
public readonly record struct BotContact(ushort Id, Vector3 Feet, Stance Stance, bool Protected);

/// <summary>The bot's own body and weapon this tick.</summary>
public readonly record struct BotSelf(ushort Id, Vector3 Feet, Stance Stance, int Magazine, int MagazineSize, int Reserve, bool Reloading, bool FullAuto);

/// <summary>What the bot does this tick, in the terms of a player's input: movement axes, look angles (radians, Godot convention) and buttons.</summary>
public readonly record struct BotCommand(float MoveX, float MoveY, float Yaw, float Pitch, MoveButtons Buttons);

/// <summary>What a bot asks of the world. The server answers from the match and the map colliders; tests answer from a list.</summary>
public interface IBotWorld
{
    /// <summary>The living players <paramref name="self"/> may fight. The list may hold the bot itself, which it skips.</summary>
    IReadOnlyList<BotContact> Enemies(ushort self);
    /// <summary>True when nothing that stops a bullet lies between the two points (players do not count).</summary>
    bool Clear(Vector3 from, Vector3 to);
    /// <summary>Where the action is for this bot: the place it should head for, or null when there is nowhere to go.</summary>
    Vector3? Goal(ushort self);
}

/// <summary>
/// The mind of one bot (#214), Godot-free and deterministic for a seed. Each tick it turns what it can see into the same
/// input a player's client would send, so the server runs it through the normal movement and weapon code: recoil, spread,
/// reload times and damage are the player's.
/// <para>
/// Fighting: it picks the nearest enemy it can see, turns to it at a limited rate with an aim error that settles, and fires
/// in bursts. Within <see cref="BotTuning.HoldRangeM"/> it stands to shoot; farther away it keeps closing in along its route
/// while it fires. A hit from out of sight makes it look that way.
/// </para>
/// <para>
/// Moving: with nobody in sight it heads for the world's <see cref="IBotWorld.Goal"/> along a route. The brain does not
/// find routes: it states the one it wants in <see cref="PathRequest"/>, and the host answers with <see cref="SetPath"/>
/// when it has the time. It walks the route point to point, sprints when far, and when it makes no headway it jumps and
/// sidesteps, then asks for a new route.
/// </para>
/// </summary>
public sealed class BotBrain
{
    readonly BotTuning _t;
    readonly Random _rng;
    float _yaw, _pitch, _scanDir = 1f;
    ushort? _target;
    Vector3 _aimPoint;
    bool _visible;
    double _firstSeen, _lastSeen = double.NegativeInfinity, _nextThink, _nextError, _burstUntil, _pauseUntil;
    bool _inBurst, _tap;
    float _errYaw, _errPitch;
    Vector3 _alert;
    double _alertUntil = double.NegativeInfinity;
    // travel
    Vector3? _goal;
    IReadOnlyList<Vector3>? _path;
    int _wp;
    Vector3 _pathGoal, _stuckPos;
    double _pathAt = double.NegativeInfinity, _stuckAt, _escapeUntil;
    int _stuckCount;
    float _escapeDir;
    bool _escapeJump, _wasTravelling;

    /// <summary>The route the bot wants (from its feet to its goal), or null. The host answers with <see cref="SetPath"/>; it is kept up to date while it waits.</summary>
    public (Vector3 From, Vector3 To)? PathRequest { get; private set; }

    /// <summary>The answer to <see cref="PathRequest"/>: the points to walk, in order, for the goal <paramref name="to"/>. An empty route is allowed (nowhere to go).</summary>
    public void SetPath(IReadOnlyList<Vector3> points, Vector3 to, double now)
    {
        _path = points;
        _wp = 0;
        _pathGoal = to;
        _pathAt = now;
        PathRequest = null;
    }

    public BotBrain(BotTuning tuning, int seed) { _t = tuning; _rng = new Random(seed); }

    /// <summary>What the bot is doing, for the server log: fighting, alerted, walking, waiting (for a route), blocked (escaping), arrived, idle.</summary>
    public string Status { get; private set; } = "idle";
    /// <summary>How many times this life the bot made no headway and had to escape; a high number marks a bad place on the map.</summary>
    public int Stuck { get; private set; }

    /// <summary>The enemy the bot is engaging, if any (for logs and tests).</summary>
    public ushort? Target => _target;
    public float Yaw => _yaw;

    /// <summary>A fresh life: no target, no memory, facing <paramref name="yaw"/>.</summary>
    public void Reset(float yaw, double now)
    {
        _yaw = yaw; _pitch = 0;
        _target = null; _visible = false; _inBurst = false;
        _lastSeen = _alertUntil = double.NegativeInfinity;
        _nextThink = now + _rng.NextDouble() * _t.ThinkS; // bots that spawn together do not all cast their rays on the same tick
        _pauseUntil = now;
        _scanDir = _rng.Next(2) == 0 ? 1f : -1f;
        _goal = null; _path = null; PathRequest = null;
        _pathAt = double.NegativeInfinity;
        _wasTravelling = false; _escapeUntil = now;
        Status = "idle"; Stuck = 0;
    }

    /// <summary>The bot was hit from <paramref name="from"/> (the attacker's feet): it looks there for a while unless it already has a target in sight.</summary>
    public void Alert(Vector3 from, double now)
    {
        _alert = from;
        _alertUntil = now + _t.AlertS;
    }

    public BotCommand Tick(double now, float dt, in BotSelf self, IBotWorld world)
    {
        var eye = self.Feet + Vector3.UnitY * MovementStep.EyeHeight(self.Stance);
        if (now >= _nextThink)
        {
            Look(now, self, eye, world);
            _goal = world.Goal(self.Id);
            _nextThink = now + _t.ThinkS;
        }
        else if (_visible && _target is { } id)
        {
            // between looks the aim follows the target's position (no ray), so a strafing target is tracked every tick
            foreach (var e in world.Enemies(self.Id))
                if (e.Id == id) { _aimPoint = AimPoint(e); break; }
        }

        var buttons = MoveButtons.None;
        float moveX = 0, moveY = 0;
        var travelling = false;
        var engaging = _target != null && now - _lastSeen <= _t.MemoryS;
        if (engaging)
        {
            if (now >= _nextError) RollError(now);
            var (yaw, pitch) = Angles(eye, _aimPoint);
            yaw += _errYaw; pitch += _errPitch;
            var off = TurnToward(yaw, pitch, dt);
            if (Fires(now, off, self)) buttons |= MoveButtons.Fire;
            // too far to hit much: keep closing in, the aim stays on the target and the legs follow the route
            travelling = Flat(_aimPoint - self.Feet) > _t.HoldRangeM && Travel(now, dt, self, true, out moveX, out moveY, ref buttons);
            Status = travelling ? "advancing" : "fighting";
        }
        else
        {
            _target = null; _visible = false; _inBurst = false;
            if (now < _alertUntil)
            {
                var (yaw, pitch) = Angles(eye, _alert + Vector3.UnitY);
                TurnToward(yaw, pitch, dt);
                Status = "alerted";
            }
            else if (Travel(now, dt, self, false, out moveX, out moveY, ref buttons)) travelling = true;
            else
            {
                _yaw = Wrap(_yaw + _scanDir * Rad(_t.ScanDegPerS) * dt);
                _pitch = Approach(_pitch, 0, Rad(_t.TurnRateDegPerS) * dt);
            }
        }
        _wasTravelling = travelling;

        // reload when empty, and top up a half-empty magazine while nobody is in sight
        var wantsReload = self.Magazine == 0 || (!engaging && self.Magazine * 2 < self.MagazineSize);
        if (wantsReload && !self.Reloading && self.Reserve > 0 && self.MagazineSize > 0) buttons |= MoveButtons.Reload;
        return new BotCommand(moveX, moveY, _yaw, _pitch, buttons);
    }

    /// <summary>
    /// Walks toward the goal along the route. Not <paramref name="aiming"/>: it faces the way it goes and may sprint. Aiming
    /// (a fight is on): the look stays where the caller put it and the legs alone follow the route, strafing as needed.
    /// False when there is nothing to walk (no goal, already there, or the route has not arrived yet).
    /// </summary>
    bool Travel(double now, float dt, in BotSelf self, bool aiming, out float moveX, out float moveY, ref MoveButtons buttons)
    {
        moveX = moveY = 0;
        if (_goal is not { } goal) { _path = null; PathRequest = null; Status = "idle"; return false; }
        var toGoal = Flat(goal - self.Feet);
        if (toGoal <= _t.ArriveM) { PathRequest = null; Status = "arrived"; return false; }

        // the route: asked for when there is none, when it is used up short of the goal, when the goal has moved, and every few seconds
        var done = _path == null || _wp >= _path.Count;
        var old = now - _pathAt;
        if (old >= 1.0 && (done || old > _t.RepathS || Flat(goal - _pathGoal) > _t.RepathGoalMovedM)) PathRequest = (self.Feet, goal);
        else if (PathRequest != null) PathRequest = (self.Feet, goal); // still waiting: the host gets the latest positions
        if (_path == null) { Status = "waiting"; return false; }
        while (_wp < _path.Count && Flat(_path[_wp] - self.Feet) <= _t.WaypointReachM) _wp++;
        if (_wp >= _path.Count) { Status = _path.Count == 0 ? "noroute" : "routeend"; return false; }
        Status = "walking";

        if (!_wasTravelling) { _stuckPos = self.Feet; _stuckAt = now; _stuckCount = 0; }
        var (yaw, _) = Angles(self.Feet, _path[_wp]);
        if (!aiming) TurnToward(yaw, 0, dt);
        var turn = Diff(yaw, _yaw); // how far the way lies to the left of where the bot looks

        // no headway: a jump and a sidestep (a tree trunk, a kerb, another player); the third time in a row a new route
        if (now < _escapeUntil)
        {
            (moveX, moveY) = AlongRoute(_escapeDir, 0.4f, turn);
            if (_escapeJump) { buttons |= MoveButtons.Jump; _escapeJump = false; }
            Status = "blocked";
            return true;
        }
        if (now - _stuckAt >= 1.0)
        {
            if (Flat(self.Feet - _stuckPos) < 0.4f)
            {
                Stuck++;
                if (++_stuckCount >= 3) { _stuckCount = 0; _path = null; _pathAt = now - 1.0; PathRequest = (self.Feet, goal); }
                else { _escapeUntil = now + 0.6; _escapeDir = _rng.Next(2) == 0 ? 1f : -1f; _escapeJump = true; }
            }
            else _stuckCount = 0;
            _stuckPos = self.Feet; _stuckAt = now;
        }

        var off = MathF.Abs(turn);
        if (aiming || off < Rad(70)) (moveX, moveY) = AlongRoute(0, 1f, turn); // not aiming and the way is behind it: turn on the spot first
        if (!aiming && off < Rad(20) && toGoal > _t.SprintBeyondM) buttons |= MoveButtons.Sprint;
        return true;
    }

    /// <summary>
    /// Movement axes for a step given against the route's direction (<paramref name="side"/> to its right,
    /// <paramref name="ahead"/> along it), when the route lies <paramref name="turn"/> radians to the left of the look.
    /// </summary>
    static (float MoveX, float MoveY) AlongRoute(float side, float ahead, float turn)
    {
        var (sin, cos) = MathF.SinCos(turn);
        return (side * cos - ahead * sin, ahead * cos + side * sin);
    }

    /// <summary>Horizontal length.</summary>
    static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>Chooses the target: the current one while it stays in sight, else the nearest enemy in sight.</summary>
    void Look(double now, in BotSelf self, Vector3 eye, IBotWorld world)
    {
        BotContact? best = null, current = null;
        var bestDist = float.PositiveInfinity;
        foreach (var e in world.Enemies(self.Id))
        {
            if (e.Id == self.Id || e.Protected) continue;
            var aim = AimPoint(e);
            var d = Vector3.Distance(eye, aim);
            if (d > _t.SightRangeM) continue;
            if (d > _t.NearAwarenessM && e.Id != _target && !InViewCone(eye, aim)) continue; // a target already engaged is followed past the cone's edge
            if (!world.Clear(eye, aim)) continue;
            if (e.Id == _target) current = e;
            if (d < bestDist) { best = e; bestDist = d; }
        }
        // stay on the current target unless another is at most half as far: no flicking between two enemies at a similar range
        var pick = current is { } c && !(best is { } b && b.Id != c.Id && bestDist < 0.5f * Vector3.Distance(eye, AimPoint(c))) ? current : best;
        if (pick is not { } p) { _visible = false; return; }
        if (p.Id != _target || now - _lastSeen > _t.MemoryS)
        {
            _target = p.Id;
            _firstSeen = now;
            _nextError = now; // a fresh error for a fresh target
        }
        _visible = true;
        _lastSeen = now;
        _aimPoint = AimPoint(p);
    }

    /// <summary>The trigger: only on a target in sight, after the reaction time, with the aim on it; in bursts.</summary>
    bool Fires(double now, float offAim, in BotSelf self)
    {
        var can = _visible && now - _firstSeen >= _t.ReactionS && offAim <= Rad(_t.FireConeDeg) && self.Magazine > 0 && !self.Reloading;
        if (!can)
        {
            if (_inBurst) { _inBurst = false; _pauseUntil = now + _t.BurstPauseS * 0.5; }
            return false;
        }
        if (!_inBurst)
        {
            if (now < _pauseUntil) return false;
            _inBurst = true;
            _burstUntil = now + _t.BurstS * Vary();
        }
        if (now >= _burstUntil)
        {
            _inBurst = false;
            _pauseUntil = now + _t.BurstPauseS * Vary();
            return false;
        }
        if (self.FullAuto) return true;
        _tap = !_tap; // a semi-automatic weapon fires on the press, so the trigger is tapped every other tick
        return _tap;
    }

    /// <summary>0.7 to 1.3.</summary>
    double Vary() => 0.7 + _rng.NextDouble() * 0.6;

    void RollError(double now)
    {
        var settled = Math.Clamp((now - _firstSeen) / Math.Max(_t.AimSettleS, 1e-3), 0, 1);
        var m = Rad((float)(_t.AimErrorDeg + (_t.AimErrorMinDeg - _t.AimErrorDeg) * settled));
        _errYaw = (float)(_rng.NextDouble() * 2 - 1) * m;
        _errPitch = (float)(_rng.NextDouble() * 2 - 1) * m * 0.6f; // people miss sideways more than up and down
        _nextError = now + 0.25 + _rng.NextDouble() * 0.25;
    }

    /// <summary>Turns the aim toward the wanted angles at the turn rate; returns how far off it still is (radians).</summary>
    float TurnToward(float yaw, float pitch, float dt)
    {
        var step = Rad(_t.TurnRateDegPerS) * dt;
        _yaw = Wrap(_yaw + Math.Clamp(Diff(yaw, _yaw), -step, step));
        _pitch = Approach(_pitch, Math.Clamp(pitch, -1.45f, 1.45f), step);
        return MathF.Max(MathF.Abs(Diff(yaw, _yaw)), MathF.Abs(pitch - _pitch));
    }

    bool InViewCone(Vector3 eye, Vector3 point)
    {
        var (yaw, _) = Angles(eye, point);
        return MathF.Abs(Diff(yaw, _yaw)) <= Rad(_t.ViewHalfAngleDeg);
    }

    /// <summary>Where on an enemy the bot aims: the chest for its stance.</summary>
    public static Vector3 AimPoint(in BotContact e) => e.Feet + Vector3.UnitY * (e.Stance switch { Stance.Crouch => 0.75f, Stance.Prone => 0.3f, _ => 1.15f });

    /// <summary>The yaw and pitch (radians, Godot convention: yaw 0 faces -Z, positive turns left) that look from one point at another.</summary>
    public static (float Yaw, float Pitch) Angles(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        return (MathF.Atan2(-d.X, -d.Z), MathF.Atan2(d.Y, flat));
    }

    static float Rad(float deg) => deg * MathF.PI / 180f;
    static float Wrap(float a) { a %= MathF.Tau; return a < 0 ? a + MathF.Tau : a; }
    /// <summary>The signed shortest turn from <paramref name="from"/> to <paramref name="to"/>, -pi to pi.</summary>
    static float Diff(float to, float from) { var d = (to - from) % MathF.Tau; return d > MathF.PI ? d - MathF.Tau : d < -MathF.PI ? d + MathF.Tau : d; }
    static float Approach(float cur, float target, float step) => cur + Math.Clamp(target - cur, -step, step);
}

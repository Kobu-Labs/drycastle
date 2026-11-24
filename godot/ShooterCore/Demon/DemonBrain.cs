using System.Numerics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Demon;

/// <summary>
/// The demon's mind (Holy Mass event). Pure and deterministic: it is stepped with a dt, the players inside the church as plain data and a seeded
/// <see cref="Random"/>, and says what it did as <see cref="DemonEvent"/>s and a <see cref="Pose"/>; no physics, no navmesh. It crawls between the
/// anchors of an <see cref="AnchorGraph"/>: after a dwell it picks a goal within a few links that sees its target at about the preferred range, and walks
/// the links to it at the crawl speed. At rest and facing a target in range it winds up (a telegraph), then throws a fireball along a ballistic arc.
/// Damage is a hit count (<see cref="RegisterHit"/>), scaled by the players inside. If everyone inside dies it heals and waits.
/// </summary>
public sealed partial class DemonBrain
{
    const float Up = 1.5f; // eye height above the feet, for sight lines
    const float SameSurface = 0.9f; // normals this close (dot) are one surface: the same wall, the ceiling
    const float CeilingY = -0.7f; // a normal pointing down this much is the ceiling
    const float TooShort = 50f; // score lost by a route under the preferred length: only taken when nothing else is

    readonly DemonFile _def;
    readonly AnchorGraph _graph;
    readonly Random _rng;
    readonly Func<Vector3, Vector3, bool> _sight;
    readonly SortedSet<ushort> _credited = new();
    readonly Queue<int> _recent = new();
    readonly List<int> _path = new();

    // The phase carries the clock of the phase that has one (the roar, the leap and pounce crouch, the fireball and flame wind-up, the pin). _dwell stays a plain field:
    // a fireball interrupts the dwell and gives the rest of it back, which a clock restarted by entering the phase could not.
    readonly StateMachine<DemonPhase> _fsm = new(DemonMachines.PhaseSpec);
    readonly StateMachine<DemonWake> _wake = new(DemonMachines.WakeSpec);
    readonly StateMachine<PounceHold> _hold = new(DemonMachines.PounceSpec);
    int _anchor, _segFrom, _segTo, _pathIdx, _nextBall;
    float _segDist, _dwell;
    // cooldowns and delays: run down to 0 (Countdown), the thing they guard may happen when Ready
    Countdown _cooldown, _rearm, _retarget, _nextRoar, _leapCd, _flameCd, _pounceCd;
    // roar, leap, flame
    float _leapDur, _leapU, _flameT, _flameTick, _sinceLeap, _sinceCeiling;
    bool _roarPending;
    int _roarFired;
    int _leapTo;
    ushort _flameTarget;
    Vector3 _leapF0, _flameDir, _leapDir, _leapA, _leapNA;
    // pounce
    float _fightT, _pounceU, _pounceDur, _settle;
    ushort _victim; // valid while _hold is not None
    ushort? _lastVictim;
    Vector3 _landPos, _pinFeet, _pinDir, _pinRoot;
    readonly Func<Vector3, float> _floorY;
    ushort? _target;
    Vector3 _pos, _normal, _forward;

    /// <param name="sight">Is there a clear line between two points (a raycast of the church)? Null: always (an empty room).</param>
    /// <param name="floorY">The floor's height under a point, for where a pinned victim lies (a raycast of the church); null: the point's own height.</param>
    public DemonBrain(DemonFile def, AnchorGraph graph, Random rng, Func<Vector3, Vector3, bool>? sight = null, int? startAnchor = null, Func<Vector3, float>? floorY = null)
    {
        _def = def; _graph = graph; _rng = rng; _sight = sight ?? ((_, _) => true); _floorY = floorY ?? (v => v.Y);
        Reset(startAnchor);
    }

    public DemonPhase Phase => _fsm.Current;
    public DemonPose Pose => new(_pos, _normal, _forward);
    /// <summary>The anchor it is on, or the one it left while crawling.</summary>
    public int Anchor => _anchor;
    /// <summary>Anchors it crawls through now, the last being its goal (empty when it is not crawling).</summary>
    public IReadOnlyList<int> Path => _path;
    public ushort? Target => _target;
    public int HitsTaken { get; private set; }
    public int HitsNeeded { get; private set; }
    public IReadOnlyCollection<ushort> Credited => _credited;
    /// <summary>The mouth: where the flame leaves.</summary>
    public Vector3 FlameOrigin => _pos + _normal * _def.FlameUpM + _forward * _def.FlameForwardM;
    /// <summary>The axis of the flame (unit), turning after its target.</summary>
    public Vector3 FlameDir => _flameDir;
    /// <summary>The stream is burning now (the windup is over).</summary>
    public bool Flaming => _fsm.Is(DemonPhase.Breathing) && _fsm.Expired;
    /// <summary>The anchor it is leaping to (valid while <see cref="DemonPhase.LeapWind"/> and <see cref="DemonPhase.Leaping"/>).</summary>
    public int LeapTarget => _leapTo;
    /// <summary>The player it is pouncing on (from the wind until the release or an abort), or null.</summary>
    public ushort? PounceVictim => _hold.Is(PounceHold.None) ? null : _victim;
    /// <summary>Where the pinned victim lies (the floor, the middle of their body) and the direction their head is towards (unit, horizontal); valid while <see cref="DemonPhase.Pinning"/>.</summary>
    public Vector3 PinFeet => _pinFeet;
    public Vector3 PinDirection => _pinDir;
    /// <summary>The yaw a pinned victim's body is set to: their head is towards (sin yaw, cos yaw) in x and z.</summary>
    public float PinYaw => MathF.Atan2(_pinDir.X, _pinDir.Z);
    /// <summary>Half the length of a lying player: their head is this far from the middle of the body.</summary>
    public const float LyingHalfM = 0.85f;

    /// <summary>A fresh demon at <paramref name="startAnchor"/> (default the graph's first), full health, waiting: the start of an event or a round.</summary>
    public void Reset(int? startAnchor = null)
    {
        _anchor = startAnchor ?? _graph.FirstId;
        var a = _graph[_anchor];
        _pos = a.Pos; _normal = a.Normal;
        _forward = Vector3.Cross(_normal, MathF.Abs(_normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        _forward = Vector3.Normalize(_forward);
        _path.Clear(); _credited.Clear(); _recent.Clear();
        _target = null; _pathIdx = 0; _segDist = 0; _dwell = 0; _cooldown.Cancel(); _rearm.Cancel(); _retarget.Cancel();
        _wake.Reset(DemonWake.Asleep);
        _roarPending = false; _roarFired = 0; _leapCd.Cancel(); _flameCd.Cancel(); _flameT = 0; _sinceLeap = 0; _sinceCeiling = 0;
        _pounceCd.Cancel(); _fightT = 0; _hold.Reset(PounceHold.None); _lastVictim = null;
        _nextRoar.Start(RollRoar());
        HitsTaken = 0; HitsNeeded = _def.HitsNeeded(1);
        _fsm.Reset(DemonPhase.Waiting);
    }

    /// <summary>
    /// A hit on the demon (the host resolved it against its hit volume): adds the weight of <paramref name="kind"/> and credits <paramref name="shooter"/>.
    /// A hit that reaches the hits needed kills it (<see cref="DemonDied"/>). A dead demon ignores hits.
    /// </summary>
    public DemonHitResult RegisterHit(ushort shooter, DemonHitKind kind, List<DemonEvent> events)
    {
        if (_fsm.Is(DemonPhase.Dead)) return DemonHitResult.Ignored;
        var w = _def.WeightOf(kind);
        HitsTaken += w;
        _credited.Add(shooter);
        events.Add(new DemonHurt(shooter, w, HitsTaken, HitsNeeded));
        if (_def.Roar.DurationS > 0 && HitsTaken < HitsNeeded)
        {
            var left = 1f - HitsTaken / (float)Math.Max(1, HitsNeeded);
            var shares = _def.Roar.HealthShares;
            for (var i = 0; i < shares.Length && i < 31; i++)
                if (left <= shares[i] && (_roarFired & 1 << i) == 0) { _roarFired |= 1 << i; _roarPending = true; }
        }
        if (HitsTaken < HitsNeeded) return DemonHitResult.Hit;
        EndPounce(events, PounceStage.Aborted);
        _fsm.Enter(DemonPhase.Dead);
        events.Add(new DemonDied(_credited.ToArray()));
        return DemonHitResult.Killed;
    }

    /// <summary>
    /// Advances by <paramref name="dt"/>. <paramref name="players"/> are the players inside the church, dead bodies included (they do not count as inside).
    /// Appends what happened to <paramref name="events"/>.
    /// </summary>
    public void Step(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        if (_fsm.Is(DemonPhase.Dead)) return;
        var alive = 0;
        foreach (var p in players) if (p.Alive) alive++;
        var awake = _wake.Is(DemonWake.Awake);
        if (awake && alive > 0)
            HitsNeeded = Math.Max(HitsNeeded, _def.HitsNeeded(alive)); // more players come in: it gets tougher, never softer
        if (awake && alive == 0) Wipe(events);
        _cooldown.Tick(dt);
        _leapCd.Tick(dt);
        _flameCd.Tick(dt);
        _pounceCd.Tick(dt);
        if (_wake.Is(DemonWake.Awake))
        {
            _fightT += dt;
            _nextRoar.Tick(dt);
            _sinceLeap += dt;
            _sinceCeiling = _normal.Y < CeilingY ? 0 : _sinceCeiling + dt;
        }

        switch (_fsm.Current)
        {
            case DemonPhase.Waiting:
                _rearm.Tick(dt);
                if (alive > 0 && _rearm.Ready)
                {
                    _wake.Enter(DemonWake.Awake);
                    HitsNeeded = Math.Max(HitsNeeded, _def.HitsNeeded(alive));
                    _dwell = RollDwell(); _fsm.Enter(DemonPhase.Dwelling);
                    _nextRoar.Start(RollRoar());
                    if (_def.Roar.DurationS > 0 && _def.Roar.OnWake) BeginRoar(events);
                }
                break;
            case DemonPhase.Roaring: StepRoar(dt, players); break;
            case DemonPhase.LeapWind: StepLeapWind(dt, players); break;
            case DemonPhase.Leaping: StepLeap(dt, players); break;
            case DemonPhase.Breathing: StepFlame(dt, players, events); break;
            case DemonPhase.PounceWind: StepPounceWind(dt, players, events); break;
            case DemonPhase.Pouncing: StepPouncing(dt, players, events); break;
            case DemonPhase.Pinning: StepPinning(dt, players, events); break;
            case DemonPhase.Dwelling: StepDwelling(dt, players, events); break;
            case DemonPhase.Telegraphing: StepTelegraph(dt, players, events); break;
            case DemonPhase.Crawling: StepCrawl(dt, players); break;
        }
    }

    // ---- wipe ----

    void Wipe(List<DemonEvent> events)
    {
        HitsTaken = 0; HitsNeeded = _def.HitsNeeded(1);
        _credited.Clear();
        _target = null; _cooldown.Cancel();
        _roarPending = false; _roarFired = 0; _flameCd.Cancel(); _leapCd.Cancel(); _flameT = 0; _sinceLeap = 0; _sinceCeiling = 0;
        EndPounce(events, PounceStage.Aborted); _pounceCd.Cancel(); _fightT = 0; _lastVictim = null;
        _wake.Enter(DemonWake.Asleep); _rearm.Start(_def.Crawl.RearmDelayS);
        if (!_fsm.IsAny(DemonPhase.Crawling, DemonPhase.Leaping, DemonPhase.Pouncing)) _fsm.Enter(DemonPhase.Waiting); // a crawl or leap is finished first: it waits where it gets to
        events.Add(new DemonWiped());
    }

    // ---- targeting and facing ----

    void UpdateTarget(float dt, IReadOnlyList<DemonPlayer> players)
    {
        _retarget.Tick(dt);
        if (_target is { } id && _retarget.Running && Find(players, id) is { Alive: true }) return;
        _target = null;
        var best = float.MaxValue;
        foreach (var p in players)
        {
            if (!p.Alive) continue;
            var d = Vector3.DistanceSquared(p.Feet, _pos);
            if (d < best) { best = d; _target = p.Id; }
        }
        _retarget.Start(_def.Crawl.RetargetS);
    }

    static DemonPlayer? Find(IReadOnlyList<DemonPlayer> players, ushort id)
    {
        foreach (var p in players) if (p.Id == id) return p;
        return null;
    }

    void Face(Vector3 point)
    {
        var v = point - _pos;
        v -= _normal * Vector3.Dot(v, _normal);
        if (v.LengthSquared() > 1e-6f) _forward = Vector3.Normalize(v);
    }

    float RollRoar() => _def.Roar.IntervalS.Length == 2 ? _def.Roar.IntervalS[0] + (float)_rng.NextDouble() * (_def.Roar.IntervalS[1] - _def.Roar.IntervalS[0]) : float.MaxValue;

    float RollDwell() => _def.Crawl.DwellS[0] + (float)_rng.NextDouble() * (_def.Crawl.DwellS[1] - _def.Crawl.DwellS[0]);

    // ---- dwell and fireball ----

    /// <summary>
    /// A move is over: back to rest on its anchor with <paramref name="dwellS"/> to dwell. Resting is Dwelling when it is awake and Waiting when a wipe put it to sleep in the meantime
    /// (it finished the crawl, leap or pounce it was in).
    /// </summary>
    void Rest(float dwellS)
    {
        _dwell = dwellS;
        _fsm.Enter(_wake.Is(DemonWake.Asleep) ? DemonPhase.Waiting : DemonPhase.Dwelling);
    }

    void StepDwelling(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        UpdateTarget(dt, players);
        var t = _target is { } id ? Find(players, id) : null;
        if (_def.Roar.DurationS > 0 && (_roarPending || _nextRoar.Ready)) { BeginRoar(events); return; }
        if (t is { } tp)
        {
            Face(tp.Feet);
            if (LeapDue && TryLeap(tp, true)) return; // overdue: it leaps before it does anything else
            if (_flameCd.Ready && TryFlame(tp)) { BeginFlame(tp, events); return; }
            if (_cooldown.Ready && TryAim(tp, out _))
            {
                _fsm.Enter(DemonPhase.Telegraphing, _def.Fireball.TelegraphS);
                events.Add(new DemonTelegraph(tp.Id, Muzzle, _def.Fireball.TelegraphS));
                return;
            }
        }
        _dwell -= dt;
        if (_dwell > 0) return;
        if (t is { } tgt && (TryPounce(players, events) || TryLeap(tgt, false) || BeginCrawl(tgt))) return;
        _dwell = RollDwell();
    }

    void StepTelegraph(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        UpdateTarget(dt, players);
        var t = _target is { } id ? Find(players, id) : null;
        if (t is { } tp) Face(tp.Feet);
        if (!_fsm.Tick(dt)) return;
        _fsm.Enter(DemonPhase.Dwelling); // with the dwell it had left: the fireball only interrupted it
        if (t is { } tgt && TryAim(tgt, out var vel))
        {
            events.Add(new DemonFireballLaunched(new Fireball(_nextBall++, Muzzle, vel, 0)));
            _cooldown.Start(_def.Fireball.CooldownS);
        }
        else _cooldown.Start(0.5f); // the target is gone or out of reach: look again soon
    }

    Vector3 Muzzle => _pos + _normal * _def.MuzzleM;

    /// <summary>Can it hit this player from here: in range, a clear line, and an arc that reaches. The velocity is the launch velocity.</summary>
    bool TryAim(DemonPlayer p, out Vector3 vel)
    {
        vel = default;
        var f = _def.Fireball;
        var from = Muzzle;
        var aim = p.Feet + new Vector3(0, f.AimHeightM, 0);
        var d = Vector3.Distance(from, aim);
        if (d < f.MinRangeM || d > f.MaxRangeM) return false;
        if (!_sight(from, p.Feet + new Vector3(0, Up, 0))) return false;
        return Ballistics.Aim(from, aim, p.Velocity, f.SpeedMps, f.GravityMps2, f.LeadFactor, out vel);
    }

    // ---- crawling ----

    /// <summary>Picks a goal and starts crawling to it; false when there is no other anchor to go to.</summary>
    bool BeginCrawl(DemonPlayer target)
    {
        var c = _def.Crawl;
        var eye = target.Feet + new Vector3(0, Up, 0);
        var hops = new Dictionary<int, int> { [_anchor] = 0 };
        var parent = new Dictionary<int, int>();
        var queue = new Queue<int>();
        queue.Enqueue(_anchor);
        int best = -1; var bestScore = float.NegativeInfinity;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var h = hops[id];
            if (id != _anchor)
            {
                var a = _graph[id];
                var score = -MathF.Abs(Vector3.Distance(a.Pos, eye) - c.PreferredRangeM) - c.HopCost * h + (float)_rng.NextDouble() * c.Jitter;
                if (_sight(a.Pos + a.Normal * _def.MuzzleM, eye)) score += c.LosBonus;
                if (Vector3.Dot(a.Normal, _normal) < SameSurface) score += c.SurfaceChangeBonus;
                if (OnCeiling(a.Normal)) score += CeilingScore(c);
                if (h < c.MinHops) score -= TooShort;
                if (_recent.Contains(id)) score -= c.RevisitPenalty;
                if (score > bestScore) { bestScore = score; best = id; }
            }
            if (h >= c.MaxHops) continue;
            foreach (var n in _graph.Neighbours(id))
                if (!hops.ContainsKey(n)) { hops[n] = h + 1; parent[n] = id; queue.Enqueue(n); }
        }
        if (best < 0) return false;
        _path.Clear();
        for (var n = best; n != _anchor; n = parent[n]) _path.Add(n);
        _path.Reverse();
        _pathIdx = 0; _segFrom = _anchor; _segTo = _path[0]; _segDist = 0;
        _fsm.Enter(DemonPhase.Crawling);
        return true;
    }

    void StepCrawl(float dt, IReadOnlyList<DemonPlayer> players)
    {
        var budget = dt * _def.Crawl.SpeedMps;
        while (budget > 0 && _fsm.Is(DemonPhase.Crawling))
        {
            var len = Vector3.Distance(_graph[_segFrom].Pos, _graph[_segTo].Pos);
            var left = len - _segDist;
            if (budget < left) { _segDist += budget; budget = 0; break; }
            budget -= left;
            Arrive(_segTo);
            if (++_pathIdx >= _path.Count) { Finish(players); return; }
            _segFrom = _anchor; _segTo = _path[_pathIdx]; _segDist = 0;
        }
        PoseOnLink();
    }

    void Arrive(int id)
    {
        _anchor = id;
        _recent.Enqueue(id);
        while (_recent.Count > 3) _recent.Dequeue();
        var a = _graph[id];
        _pos = a.Pos; _normal = a.Normal;
    }

    void Finish(IReadOnlyList<DemonPlayer> players)
    {
        _path.Clear();
        Rest(RollDwell());
        if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
    }

    void PoseOnLink()
    {
        var a = _graph[_segFrom]; var b = _graph[_segTo];
        var len = Vector3.Distance(a.Pos, b.Pos);
        var t = len > 1e-6f ? Math.Clamp(_segDist / len, 0f, 1f) : 1f;
        _pos = Vector3.Lerp(a.Pos, b.Pos, t);
        var n = Vector3.Lerp(a.Normal, b.Normal, t);
        _normal = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : a.Normal;
        var dir = b.Pos - a.Pos;
        dir -= _normal * Vector3.Dot(dir, _normal);
        if (dir.LengthSquared() > 1e-6f) _forward = Vector3.Normalize(dir);
    }
}

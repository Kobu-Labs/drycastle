using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a hit counts as: the weapon kind decides the weight (<see cref="DemonHitWeights"/>). There is no headshot kind: no bonus.</summary>
public enum DemonHitKind : byte { Bullet, Explosive, Vehicle }

public enum DemonHitResult : byte { Ignored, Hit, Killed }

public enum DemonPhase : byte
{
    /// <summary>Still, at its anchor: before the first player comes in, and after a wipe until the delay is over and a player is inside.</summary>
    Waiting,
    /// <summary>Resting on an anchor, facing its target.</summary>
    Dwelling,
    /// <summary>Crawling along a link.</summary>
    Crawling,
    /// <summary>The wind-up of a fireball (<see cref="DemonTelegraph"/> was sent).</summary>
    Telegraphing,
    Dead,
}

/// <summary>A player in the church as the brain sees them: feet, alive, and the velocity the fireball leads with.</summary>
public readonly record struct DemonPlayer(ushort Id, Vector3 Feet, bool Alive, Vector3 Velocity = default);

/// <summary>Where the demon is and how it is turned: <see cref="Normal"/> is its up (the surface under it), <see cref="Forward"/> the way it faces along the surface. Godot axes.</summary>
public readonly record struct DemonPose(Vector3 Position, Vector3 Normal, Vector3 Forward);

public abstract record DemonEvent;
/// <summary>A hit that counted: its weight, the total taken and the total that kills it.</summary>
public sealed record DemonHurt(ushort Shooter, int Weight, int HitsTaken, int HitsNeeded) : DemonEvent;
/// <summary>A fireball is winding up at <see cref="Target"/>: the warning for clients.</summary>
public sealed record DemonTelegraph(ushort Target, Vector3 Origin, float Seconds) : DemonEvent;
public sealed record DemonFireballLaunched(Fireball Ball) : DemonEvent;
/// <summary>Every player inside died: it has healed to full and waits.</summary>
public sealed record DemonWiped : DemonEvent;
/// <summary>It is dead. <see cref="Credited"/> are the players who hit it at least once, in id order: they get the devil's curse (<see cref="CurseRules.Grant"/>).</summary>
public sealed record DemonDied(IReadOnlyList<ushort> Credited) : DemonEvent;

/// <summary>
/// The demon's mind (Holy Mass event). Pure and deterministic: it is stepped with a dt, the players inside the church as plain data and a seeded
/// <see cref="Random"/>, and says what it did as <see cref="DemonEvent"/>s and a <see cref="Pose"/>; no physics, no navmesh. It crawls between the
/// anchors of an <see cref="AnchorGraph"/>: after a dwell it picks a goal within a few links that sees its target at about the preferred range, and walks
/// the links to it at the crawl speed. At rest and facing a target in range it winds up (a telegraph), then throws a fireball along a ballistic arc.
/// Damage is a hit count (<see cref="RegisterHit"/>), scaled by the players inside. If everyone inside dies it heals and waits.
/// </summary>
public sealed class DemonBrain
{
    const float Up = 1.5f; // eye height above the feet, for sight lines

    readonly DemonFile _def;
    readonly AnchorGraph _graph;
    readonly Random _rng;
    readonly Func<Vector3, Vector3, bool> _sight;
    readonly SortedSet<ushort> _credited = new();
    readonly Queue<int> _recent = new();
    readonly List<int> _path = new();

    int _anchor, _segFrom, _segTo, _pathIdx, _nextBall;
    float _segDist, _dwell, _cooldown, _tele, _rearm, _retarget;
    bool _asleep, _engaged;
    ushort? _target;
    Vector3 _pos, _normal, _forward;

    /// <param name="sight">Is there a clear line between two points (a raycast of the church)? Null: always (an empty room).</param>
    public DemonBrain(DemonFile def, AnchorGraph graph, Random rng, Func<Vector3, Vector3, bool>? sight = null, int? startAnchor = null)
    {
        _def = def; _graph = graph; _rng = rng; _sight = sight ?? ((_, _) => true);
        Reset(startAnchor);
    }

    public DemonPhase Phase { get; private set; }
    public DemonPose Pose => new(_pos, _normal, _forward);
    /// <summary>The anchor it is on, or the one it left while crawling.</summary>
    public int Anchor => _anchor;
    /// <summary>Anchors it crawls through now, the last being its goal (empty when it is not crawling).</summary>
    public IReadOnlyList<int> Path => _path;
    public ushort? Target => _target;
    public int HitsTaken { get; private set; }
    public int HitsNeeded { get; private set; }
    public IReadOnlyCollection<ushort> Credited => _credited;

    /// <summary>A fresh demon at <paramref name="startAnchor"/> (default the graph's first), full health, waiting: the start of an event or a round.</summary>
    public void Reset(int? startAnchor = null)
    {
        _anchor = startAnchor ?? _graph.FirstId;
        var a = _graph[_anchor];
        _pos = a.Pos; _normal = a.Normal;
        _forward = Vector3.Cross(_normal, MathF.Abs(_normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        _forward = Vector3.Normalize(_forward);
        _path.Clear(); _credited.Clear(); _recent.Clear();
        _target = null; _pathIdx = 0; _segDist = 0; _dwell = 0; _cooldown = 0; _tele = 0; _rearm = 0; _retarget = 0;
        _asleep = true; _engaged = false;
        HitsTaken = 0; HitsNeeded = _def.HitsNeeded(1);
        Phase = DemonPhase.Waiting;
    }

    /// <summary>
    /// A hit on the demon (the host resolved it against its hit volume): adds the weight of <paramref name="kind"/> and credits <paramref name="shooter"/>.
    /// A hit that reaches the hits needed kills it (<see cref="DemonDied"/>). A dead demon ignores hits.
    /// </summary>
    public DemonHitResult RegisterHit(ushort shooter, DemonHitKind kind, List<DemonEvent> events)
    {
        if (Phase == DemonPhase.Dead) return DemonHitResult.Ignored;
        var w = _def.WeightOf(kind);
        HitsTaken += w;
        _credited.Add(shooter);
        events.Add(new DemonHurt(shooter, w, HitsTaken, HitsNeeded));
        if (HitsTaken < HitsNeeded) return DemonHitResult.Hit;
        Phase = DemonPhase.Dead;
        events.Add(new DemonDied(_credited.ToArray()));
        return DemonHitResult.Killed;
    }

    /// <summary>
    /// Advances by <paramref name="dt"/>. <paramref name="players"/> are the players inside the church, dead bodies included (they do not count as inside).
    /// Appends what happened to <paramref name="events"/>.
    /// </summary>
    public void Step(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        if (Phase == DemonPhase.Dead) return;
        var alive = 0;
        foreach (var p in players) if (p.Alive) alive++;
        if (!_asleep && alive > 0)
        {
            _engaged = true;
            HitsNeeded = Math.Max(HitsNeeded, _def.HitsNeeded(alive)); // more players come in: it gets tougher, never softer
        }
        if (_engaged && alive == 0) Wipe(events);
        _cooldown = MathF.Max(0, _cooldown - dt);

        switch (Phase)
        {
            case DemonPhase.Waiting:
                _rearm = MathF.Max(0, _rearm - dt);
                if (alive > 0 && _rearm <= 0)
                {
                    _asleep = false; _engaged = true;
                    HitsNeeded = Math.Max(HitsNeeded, _def.HitsNeeded(alive));
                    _dwell = RollDwell(); Phase = DemonPhase.Dwelling;
                }
                break;
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
        _target = null; _cooldown = 0; _tele = 0;
        _asleep = true; _engaged = false; _rearm = _def.Crawl.RearmDelayS;
        if (Phase != DemonPhase.Crawling) Phase = DemonPhase.Waiting; // a crawl is finished first: it waits where it gets to
        events.Add(new DemonWiped());
    }

    // ---- targeting and facing ----

    void UpdateTarget(float dt, IReadOnlyList<DemonPlayer> players)
    {
        _retarget -= dt;
        if (_target is { } id && _retarget > 0 && Find(players, id) is { Alive: true }) return;
        _target = null;
        var best = float.MaxValue;
        foreach (var p in players)
        {
            if (!p.Alive) continue;
            var d = Vector3.DistanceSquared(p.Feet, _pos);
            if (d < best) { best = d; _target = p.Id; }
        }
        _retarget = _def.Crawl.RetargetS;
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

    float RollDwell() => _def.Crawl.DwellS[0] + (float)_rng.NextDouble() * (_def.Crawl.DwellS[1] - _def.Crawl.DwellS[0]);

    // ---- dwell and fireball ----

    void StepDwelling(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        UpdateTarget(dt, players);
        var t = _target is { } id ? Find(players, id) : null;
        if (t is { } tp)
        {
            Face(tp.Feet);
            if (_cooldown <= 0 && TryAim(tp, out _))
            {
                _tele = _def.Fireball.TelegraphS; Phase = DemonPhase.Telegraphing;
                events.Add(new DemonTelegraph(tp.Id, Muzzle, _tele));
                return;
            }
        }
        _dwell -= dt;
        if (_dwell > 0) return;
        if (t is { } tgt && BeginCrawl(tgt)) return;
        _dwell = RollDwell();
    }

    void StepTelegraph(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        UpdateTarget(dt, players);
        var t = _target is { } id ? Find(players, id) : null;
        if (t is { } tp) Face(tp.Feet);
        _tele -= dt;
        if (_tele > 0) return;
        Phase = DemonPhase.Dwelling;
        if (t is { } tgt && TryAim(tgt, out var vel))
        {
            events.Add(new DemonFireballLaunched(new Fireball(_nextBall++, Muzzle, vel, 0)));
            _cooldown = _def.Fireball.CooldownS;
        }
        else _cooldown = 0.5f; // the target is gone or out of reach: look again soon
    }

    Vector3 Muzzle => _pos + _normal * _def.Fireball.MuzzleOffsetM;

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
                if (_sight(a.Pos + a.Normal * _def.Fireball.MuzzleOffsetM, eye)) score += c.LosBonus;
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
        Phase = DemonPhase.Crawling;
        return true;
    }

    void StepCrawl(float dt, IReadOnlyList<DemonPlayer> players)
    {
        var budget = dt * _def.Crawl.SpeedMps;
        while (budget > 0 && Phase == DemonPhase.Crawling)
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
        Phase = _asleep ? DemonPhase.Waiting : DemonPhase.Dwelling;
        _dwell = RollDwell();
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

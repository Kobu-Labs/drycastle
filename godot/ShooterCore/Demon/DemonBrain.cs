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
    /// <summary>Stopped on its anchor, roaring (aggressive roar).</summary>
    Roaring,
    /// <summary>Crouched on its anchor before a leap to the opposite wall.</summary>
    LeapWind,
    /// <summary>In the air: a scripted arc to the anchor across the nave.</summary>
    Leaping,
    /// <summary>Breathing fire at its target (the windup first, then the stream).</summary>
    Breathing,
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
/// <summary>It stopped to roar.</summary>
public sealed record DemonRoared : DemonEvent;
/// <summary>It began to breathe fire at <see cref="Target"/> (the windup starts now; clients aim the stream at that player).</summary>
public sealed record DemonFlameStarted(ushort Target) : DemonEvent;
/// <summary>A flame tick reached this player (in the cone, in sight): the host hurts and ignites them.</summary>
public sealed record DemonFlameHit(ushort Player) : DemonEvent;
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

    int _anchor, _segFrom, _segTo, _pathIdx, _nextBall;
    float _segDist, _dwell, _cooldown, _tele, _rearm, _retarget;
    bool _asleep, _engaged;
    // roar, leap, flame
    float _roarT, _nextRoar, _leapCd, _leapWind, _leapDur, _leapU, _flameCd, _flameWind, _flameT, _flameTick, _sinceLeap, _sinceCeiling;
    bool _roarPending;
    int _roarFired;
    int _leapTo;
    ushort _flameTarget;
    Vector3 _leapF0, _flameDir, _leapDir;
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
    /// <summary>The mouth: where the flame leaves.</summary>
    public Vector3 FlameOrigin => _pos + _normal * _def.FlameUpM + _forward * _def.FlameForwardM;
    /// <summary>The axis of the flame (unit), turning after its target.</summary>
    public Vector3 FlameDir => _flameDir;
    /// <summary>The stream is burning now (the windup is over).</summary>
    public bool Flaming => Phase == DemonPhase.Breathing && _flameWind <= 0;
    /// <summary>The anchor it is leaping to (valid while <see cref="DemonPhase.LeapWind"/> and <see cref="DemonPhase.Leaping"/>).</summary>
    public int LeapTarget => _leapTo;

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
        _roarPending = false; _roarFired = 0; _roarT = 0; _leapCd = 0; _flameCd = 0; _flameWind = 0; _flameT = 0; _sinceLeap = 0; _sinceCeiling = 0;
        _nextRoar = RollRoar();
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
        if (_def.Roar.DurationS > 0 && HitsTaken < HitsNeeded)
        {
            var left = 1f - HitsTaken / (float)Math.Max(1, HitsNeeded);
            var shares = _def.Roar.HealthShares;
            for (var i = 0; i < shares.Length && i < 31; i++)
                if (left <= shares[i] && (_roarFired & 1 << i) == 0) { _roarFired |= 1 << i; _roarPending = true; }
        }
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
        _leapCd = MathF.Max(0, _leapCd - dt);
        _flameCd = MathF.Max(0, _flameCd - dt);
        if (!_asleep)
        {
            _nextRoar -= dt;
            _sinceLeap += dt;
            _sinceCeiling = _normal.Y < CeilingY ? 0 : _sinceCeiling + dt;
        }

        switch (Phase)
        {
            case DemonPhase.Waiting:
                _rearm = MathF.Max(0, _rearm - dt);
                if (alive > 0 && _rearm <= 0)
                {
                    _asleep = false; _engaged = true;
                    HitsNeeded = Math.Max(HitsNeeded, _def.HitsNeeded(alive));
                    _dwell = RollDwell(); Phase = DemonPhase.Dwelling;
                    _nextRoar = RollRoar();
                    if (_def.Roar.DurationS > 0 && _def.Roar.OnWake) BeginRoar(events);
                }
                break;
            case DemonPhase.Roaring: StepRoar(dt, players); break;
            case DemonPhase.LeapWind: StepLeapWind(dt, players); break;
            case DemonPhase.Leaping: StepLeap(dt, players); break;
            case DemonPhase.Breathing: StepFlame(dt, players, events); break;
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
        _roarPending = false; _roarFired = 0; _flameCd = 0; _leapCd = 0; _flameWind = 0; _flameT = 0; _sinceLeap = 0; _sinceCeiling = 0;
        _asleep = true; _engaged = false; _rearm = _def.Crawl.RearmDelayS;
        if (Phase is not (DemonPhase.Crawling or DemonPhase.Leaping)) Phase = DemonPhase.Waiting; // a crawl or leap is finished first: it waits where it gets to
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

    float RollRoar() => _def.Roar.IntervalS.Length == 2 ? _def.Roar.IntervalS[0] + (float)_rng.NextDouble() * (_def.Roar.IntervalS[1] - _def.Roar.IntervalS[0]) : float.MaxValue;

    float RollDwell() => _def.Crawl.DwellS[0] + (float)_rng.NextDouble() * (_def.Crawl.DwellS[1] - _def.Crawl.DwellS[0]);

    // ---- dwell and fireball ----

    void StepDwelling(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        UpdateTarget(dt, players);
        var t = _target is { } id ? Find(players, id) : null;
        if (_def.Roar.DurationS > 0 && (_roarPending || _nextRoar <= 0)) { BeginRoar(events); return; }
        if (t is { } tp)
        {
            Face(tp.Feet);
            if (LeapDue && TryLeap(tp, true)) return; // overdue: it leaps before it does anything else
            if (_flameCd <= 0 && TryFlame(tp)) { BeginFlame(tp, events); return; }
            if (_cooldown <= 0 && TryAim(tp, out _))
            {
                _tele = _def.Fireball.TelegraphS; Phase = DemonPhase.Telegraphing;
                events.Add(new DemonTelegraph(tp.Id, Muzzle, _tele));
                return;
            }
        }
        _dwell -= dt;
        if (_dwell > 0) return;
        if (t is { } tgt && (TryLeap(tgt, false) || BeginCrawl(tgt))) return;
        _dwell = RollDwell();
    }

    // ---- roar ----

    void BeginRoar(List<DemonEvent> events)
    {
        _roarPending = false;
        _roarT = _def.Roar.DurationS;
        _nextRoar = RollRoar();
        Phase = DemonPhase.Roaring;
        events.Add(new DemonRoared());
    }

    void StepRoar(float dt, IReadOnlyList<DemonPlayer> players)
    {
        UpdateTarget(dt, players);
        if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
        _roarT -= dt;
        if (_roarT > 0) return;
        Phase = _asleep ? DemonPhase.Waiting : DemonPhase.Dwelling;
        _dwell = RollDwell() * 0.5f;
        _cooldown = MathF.Max(_cooldown, 0.8f); // it does not throw the instant it has done
    }

    // ---- flame ----

    static Vector3 Chest(DemonPlayer p) => p.Feet + new Vector3(0, 1.0f, 0);

    bool TryFlame(DemonPlayer p)
    {
        var f = _def.Flame;
        if (f.DurationS <= 0) return false;
        var from = FlameOrigin; var to = Chest(p);
        return Vector3.Distance(from, to) <= f.RangeM && _sight(from, to);
    }

    void BeginFlame(DemonPlayer p, List<DemonEvent> events)
    {
        var f = _def.Flame;
        _flameTarget = p.Id; _flameWind = f.WindupS; _flameT = f.DurationS; _flameTick = 0;
        var d = Chest(p) - FlameOrigin;
        _flameDir = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : _forward;
        Phase = DemonPhase.Breathing;
        events.Add(new DemonFlameStarted(p.Id));
    }

    void StepFlame(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        var f = _def.Flame;
        if (Find(players, _flameTarget) is not { Alive: true } t) { EndFlame(); return; }
        _target = t.Id;
        Face(t.Feet);
        var want = Chest(t) - FlameOrigin;
        if (want.LengthSquared() > 1e-6f) _flameDir = RotateToward(_flameDir, Vector3.Normalize(want), f.TrackDegPerS * MathF.PI / 180f * dt);
        if (_flameWind > 0) { _flameWind -= dt; return; }
        _flameT -= dt;
        _flameTick -= dt;
        if (_flameTick <= 0)
        {
            _flameTick += f.TickS;
            foreach (var p in players)
                if (p.Alive && InCone(p)) events.Add(new DemonFlameHit(p.Id));
        }
        if (_flameT <= 0) EndFlame();
    }

    bool InCone(DemonPlayer p)
    {
        var f = _def.Flame;
        var from = FlameOrigin; var to = Chest(p);
        var v = to - from;
        var d = v.Length();
        if (d > f.RangeM) return false;
        if (d < 1e-3f) return true;
        if (Vector3.Dot(v / d, _flameDir) < MathF.Cos(f.ConeHalfAngleDeg * MathF.PI / 180f)) return false;
        return _sight(from, to);
    }

    void EndFlame()
    {
        Phase = _asleep ? DemonPhase.Waiting : DemonPhase.Dwelling;
        _flameCd = _def.Flame.CooldownS;
        _cooldown = MathF.Max(_cooldown, 1.2f);
        _dwell = RollDwell() * 0.5f;
        _flameWind = 0; _flameT = 0;
    }

    /// <summary>The unit vector <paramref name="from"/> turned towards <paramref name="to"/> by at most <paramref name="maxRad"/>.</summary>
    static Vector3 RotateToward(Vector3 from, Vector3 to, float maxRad)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        var ang = MathF.Acos(dot);
        if (ang <= maxRad || ang < 1e-5f) return to;
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-8f) return from;
        axis = Vector3.Normalize(axis);
        return Vector3.Normalize(from * MathF.Cos(maxRad) + Vector3.Cross(axis, from) * MathF.Sin(maxRad) + axis * Vector3.Dot(axis, from) * (1 - MathF.Cos(maxRad)));
    }

    // ---- leap ----

    /// <summary>The point of the arc at <paramref name="u"/> (0..1) between two points: the straight line plus a bow of height <paramref name="h"/> along <paramref name="dir"/>.</summary>
    static Vector3 ArcPoint(Vector3 a, Vector3 b, float u, float h, Vector3 dir) => Vector3.Lerp(a, b, u) + dir * (4 * h * u * (1 - u));

    /// <summary>Which way a leap between surfaces bows: into the room. Across the nave that is up (over the pews); wall to ceiling it is the way both normals point, away from the corner.</summary>
    static Vector3 ArcDir(Vector3 na, Vector3 nb)
    {
        if (Vector3.Dot(na, nb) < -0.5f) return Vector3.UnitY;
        var d = na + nb;
        return d.LengthSquared() > 1e-4f ? Vector3.Normalize(d) : Vector3.UnitY;
    }

    static bool OnCeiling(Vector3 normal) => normal.Y < CeilingY;

    /// <summary>It has gone <see cref="DemonLeapDef.DueS"/> fighting seconds without a leap and the cooldown is over: it leaps at the next chance.</summary>
    bool LeapDue => _def.Leap.Chance > 0 && _def.Leap.DueS > 0 && _leapCd <= 0 && _sinceLeap >= _def.Leap.DueS;

    /// <summary>
    /// Picks an anchor on another surface (the opposite wall, the ceiling from a wall, a wall from the ceiling) and starts to crouch for the leap; false when it does not leap
    /// (chance, cooldown, nothing within reach). <paramref name="force"/> skips the chance; when nothing qualifies a forced leap is tried again in a second.
    /// </summary>
    bool TryLeap(DemonPlayer target, bool force)
    {
        var l = _def.Leap;
        if (l.Chance <= 0 || _leapCd > 0 || (!force && _rng.NextDouble() >= l.Chance)) return false;
        var c = _def.Crawl;
        var eye = target.Feet + new Vector3(0, Up, 0);
        var off = _def.MuzzleM;
        var from = _pos + _normal * off;
        var best = -1; var bestScore = float.NegativeInfinity;
        foreach (var a in _graph.Anchors)
        {
            if (a.Id == _anchor || Vector3.Dot(a.Normal, _normal) > l.MaxNormalDot) continue;
            var dist = Vector3.Distance(a.Pos, _pos);
            if (dist < l.MinRangeM || dist > l.MaxRangeM) continue;
            var land = a.Pos + a.Normal * off;
            var mid = ArcPoint(from, land, 0.5f, _def.Leap.ArcHeightM, ArcDir(_normal, a.Normal));
            if (!_sight(from, mid) || !_sight(mid, land)) continue;
            var score = -MathF.Abs(Vector3.Distance(a.Pos, eye) - c.PreferredRangeM) + (float)_rng.NextDouble() * c.Jitter;
            if (_sight(land, eye)) score += c.LosBonus;
            if (OnCeiling(a.Normal)) score += CeilingScore(c);
            else if (Vector3.Dot(a.Normal, _normal) < -0.7f) score += l.AcrossBonus;
            if (_recent.Contains(a.Id)) score -= c.RevisitPenalty;
            if (score > bestScore) { bestScore = score; best = a.Id; }
        }
        if (best < 0)
        {
            if (force) _sinceLeap = MathF.Max(0, l.DueS - 1f);
            return false;
        }
        _leapTo = best;
        _leapWind = l.CrouchS;
        _leapDur = MathF.Max(0.35f, Vector3.Distance(_graph[best].Pos, _pos) / l.SpeedMps);
        _leapU = 0; _sinceLeap = 0;
        Phase = DemonPhase.LeapWind;
        return true;
    }

    /// <summary>What a goal on the ceiling is worth: the base, and more the longer it has been down on the walls.</summary>
    float CeilingScore(DemonCrawlDef c) => c.CeilingBonus + MathF.Min(c.CeilingHungerCap, c.CeilingHungerPerS * _sinceCeiling);

    void StepLeapWind(float dt, IReadOnlyList<DemonPlayer> players)
    {
        UpdateTarget(dt, players);
        if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
        _leapWind -= dt;
        if (_leapWind > 0) return;
        _leapF0 = _forward; _leapU = 0;
        _leapDir = ArcDir(_graph[_anchor].Normal, _graph[_leapTo].Normal);
        Phase = DemonPhase.Leaping;
    }

    void StepLeap(float dt, IReadOnlyList<DemonPlayer> players)
    {
        _leapU += dt / _leapDur;
        var a = _graph[_anchor]; var b = _graph[_leapTo];
        if (_leapU >= 1)
        {
            Arrive(_leapTo);
            _path.Clear();
            Phase = _asleep ? DemonPhase.Waiting : DemonPhase.Dwelling;
            _dwell = _def.Leap.LandRecoverS; _leapCd = _def.Leap.CooldownS;
            _forward = ProjectOnto(_forward, _normal, _leapF0);
            if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
            return;
        }
        var u = _leapU;
        _pos = ArcPoint(a.Pos, b.Pos, u, _def.Leap.ArcHeightM, _leapDir);
        // its up turns from the surface it left to the one it lands on (across the nave through the room's up); its head leads across
        var s = MathF.Sin(MathF.PI * u);
        var n = Vector3.Dot(a.Normal, b.Normal) < -0.5f ? Vector3.Lerp(a.Normal, b.Normal, u) * (1 - s) + Vector3.UnitY * s : Vector3.Lerp(a.Normal, b.Normal, u);
        _normal = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
        var across = b.Pos - a.Pos;
        if (Vector3.Dot(a.Normal, b.Normal) < -0.5f) across.Y = 0;
        across = across.LengthSquared() > 1e-6f ? Vector3.Normalize(across) : _leapF0;
        var g = Vector3.Lerp(_leapF0, across, s);
        _forward = ProjectOnto(g.LengthSquared() > 1e-6f ? Vector3.Normalize(g) : _leapF0, _normal, _forward);
    }

    /// <summary>The unit vector in the plane of <paramref name="normal"/> that <paramref name="v"/> projects to, or <paramref name="fallback"/> projected the same way.</summary>
    static Vector3 ProjectOnto(Vector3 v, Vector3 normal, Vector3 fallback)
    {
        var p = v - normal * Vector3.Dot(v, normal);
        if (p.LengthSquared() < 1e-6f) p = fallback - normal * Vector3.Dot(fallback, normal);
        if (p.LengthSquared() < 1e-6f) p = Vector3.Cross(normal, MathF.Abs(normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        return Vector3.Normalize(p);
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

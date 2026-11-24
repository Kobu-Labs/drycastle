using System.Numerics;

namespace Drycastle.ShooterCore.Demon;

public sealed partial class DemonBrain
{
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
    bool LeapDue => _def.Leap.Chance > 0 && _def.Leap.DueS > 0 && _leapCd.Ready && _sinceLeap >= _def.Leap.DueS;

    /// <summary>
    /// Picks an anchor on another surface (the opposite wall, the ceiling from a wall, a wall from the ceiling) and starts to crouch for the leap; false when it does not leap
    /// (chance, cooldown, nothing within reach). <paramref name="force"/> skips the chance; when nothing qualifies a forced leap is tried again in a second.
    /// </summary>
    bool TryLeap(DemonPlayer target, bool force)
    {
        var l = _def.Leap;
        if (l.Chance <= 0 || _leapCd.Running || (!force && _rng.NextDouble() >= l.Chance)) return false;
        var best = PickLeapAnchor(target.Feet + new Vector3(0, Up, 0), true);
        if (best < 0)
        {
            if (force) _sinceLeap = MathF.Max(0, l.DueS - 1f);
            return false;
        }
        BeginLeap(best, l.CrouchS);
        return true;
    }

    /// <summary>
    /// The anchor on another surface that scores best for a leap from here, seen from the target's <paramref name="eye"/>; -1 when none. <paramref name="strict"/> keeps to the leap's range and
    /// wants a clear arc; without it (the leap away from a pounce, which starts wherever the victim lay) any anchor on another surface will do, the best seen first.
    /// </summary>
    int PickLeapAnchor(Vector3 eye, bool strict)
    {
        var l = _def.Leap;
        var c = _def.Crawl;
        var off = _def.MuzzleM;
        var from = _pos + _normal * off;
        var best = -1; var bestScore = float.NegativeInfinity;
        foreach (var a in _graph.Anchors)
        {
            if (a.Id == _anchor || Vector3.Dot(a.Normal, _normal) > l.MaxNormalDot) continue;
            var dist = Vector3.Distance(a.Pos, _pos);
            if (strict && (dist < l.MinRangeM || dist > l.MaxRangeM)) continue;
            var land = a.Pos + a.Normal * off;
            if (strict)
            {
                var mid = ArcPoint(from, land, 0.5f, _def.Leap.ArcHeightM, ArcDir(_normal, a.Normal));
                if (!_sight(from, mid) || !_sight(mid, land)) continue;
            }
            var score = -MathF.Abs(Vector3.Distance(a.Pos, eye) - c.PreferredRangeM) + (float)_rng.NextDouble() * c.Jitter;
            if (_sight(land, eye)) score += c.LosBonus;
            if (OnCeiling(a.Normal)) score += CeilingScore(c);
            else if (Vector3.Dot(a.Normal, _normal) < -0.7f) score += l.AcrossBonus;
            if (_recent.Contains(a.Id)) score -= c.RevisitPenalty;
            if (score > bestScore) { bestScore = score; best = a.Id; }
        }
        return best;
    }

    /// <summary>The leap's speed: the data's, or that of a plain leap when leaps are off.</summary>
    float LeapSpeed => _def.Leap.SpeedMps > 0 ? _def.Leap.SpeedMps : 12f;

    void BeginLeap(int anchor, float crouchS)
    {
        _leapTo = anchor;
        _leapDur = MathF.Max(0.35f, Vector3.Distance(_graph[anchor].Pos, _pos) / LeapSpeed);
        _leapU = 0; _sinceLeap = 0;
        _fsm.Enter(DemonPhase.LeapWind, crouchS);
    }

    /// <summary>What a goal on the ceiling is worth: the base, and more the longer it has been down on the walls.</summary>
    float CeilingScore(DemonCrawlDef c) => c.CeilingBonus + MathF.Min(c.CeilingHungerCap, c.CeilingHungerPerS * _sinceCeiling);

    void StepLeapWind(float dt, IReadOnlyList<DemonPlayer> players)
    {
        UpdateTarget(dt, players);
        if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
        if (!_fsm.Tick(dt)) return;
        _leapF0 = _forward; _leapU = 0;
        _leapA = _pos; _leapNA = _normal; // where it leaps from: an anchor, or (away from a pounce) the floor it stood on
        _leapDir = ArcDir(_leapNA, _graph[_leapTo].Normal);
        _fsm.Enter(DemonPhase.Leaping);
    }

    void StepLeap(float dt, IReadOnlyList<DemonPlayer> players)
    {
        _leapU += dt / _leapDur;
        var b = _graph[_leapTo];
        if (_leapU >= 1)
        {
            Arrive(_leapTo);
            _path.Clear();
            Rest(_def.Leap.LandRecoverS); _leapCd.Start(_def.Leap.CooldownS);
            _forward = ProjectOnto(_forward, _normal, _leapF0);
            if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
            return;
        }
        ArcPose(_leapA, _leapNA, b.Pos, b.Normal, _leapU, _def.Leap.ArcHeightM);
    }

    /// <summary>The pose at <paramref name="u"/> (0..1) of a leap from a surface point to another: its up turns from the surface it left to the one it lands on (across the nave through the room's up); its head leads across.</summary>
    void ArcPose(Vector3 aPos, Vector3 aNormal, Vector3 bPos, Vector3 bNormal, float u, float height)
    {
        _pos = ArcPoint(aPos, bPos, u, height, _leapDir);
        var s = MathF.Sin(MathF.PI * u);
        var opposite = Vector3.Dot(aNormal, bNormal) < -0.5f;
        var n = opposite ? Vector3.Lerp(aNormal, bNormal, u) * (1 - s) + Vector3.UnitY * s : Vector3.Lerp(aNormal, bNormal, u);
        _normal = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
        var across = bPos - aPos;
        if (opposite) across.Y = 0;
        across = across.LengthSquared() > 1e-6f ? Vector3.Normalize(across) : _leapF0;
        var g = Vector3.Lerp(_leapF0, across, s);
        _forward = ProjectOnto(g.LengthSquared() > 1e-6f ? Vector3.Normalize(g) : _leapF0, _normal, _forward);
    }
}

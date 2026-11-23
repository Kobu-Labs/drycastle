using System.Numerics;

namespace Drycastle.ShooterCore.Demon;

public sealed partial class DemonBrain
{
    // ---- roar ----

    void BeginRoar(List<DemonEvent> events)
    {
        _roarPending = false;
        _nextRoar.Start(RollRoar());
        _fsm.Enter(DemonPhase.Roaring, _def.Roar.DurationS);
        events.Add(new DemonRoared());
    }

    void StepRoar(float dt, IReadOnlyList<DemonPlayer> players)
    {
        UpdateTarget(dt, players);
        if (_target is { } id && Find(players, id) is { } t) Face(t.Feet);
        if (!_fsm.Tick(dt)) return;
        Rest(RollDwell() * 0.5f);
        _cooldown.Start(MathF.Max(_cooldown.Left, 0.8f)); // it does not throw the instant it has done
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
        _flameTarget = p.Id; _flameT = f.DurationS; _flameTick = 0;
        var d = Chest(p) - FlameOrigin;
        _flameDir = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : _forward;
        _fsm.Enter(DemonPhase.Breathing, f.WindupS); // the clock is the windup: Flaming once it is out
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
        if (!_fsm.Expired) { _fsm.Tick(dt); return; }
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
        Rest(RollDwell() * 0.5f);
        _flameCd.Start(_def.Flame.CooldownS);
        _cooldown.Start(MathF.Max(_cooldown.Left, 1.2f));
        _flameT = 0;
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
}

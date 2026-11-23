using System.Numerics;

namespace Drycastle.ShooterCore.Demon;

public sealed partial class DemonBrain
{
    // ---- pounce ----

    static bool Valid(DemonPlayer? p) => p is { Alive: true };

    /// <summary>
    /// A living, grounded, unseated player in range and in sight, once the cooldown and the first-after time are over, with <see cref="DemonPounceDef.Chance"/>; never the last victim while another
    /// qualifies. Starts the crouch and marks the victim. False when it does not pounce (no random number is drawn unless someone qualifies).
    /// </summary>
    bool TryPounce(IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        var pd = _def.Pounce;
        if (pd.PinS <= 0 || !_hold.Is(PounceHold.None) || _pounceCd.Running || _fightT < pd.FirstAfterS) return false;
        var from = Muzzle;
        var cands = new List<DemonPlayer>();
        foreach (var p in players)
            if (p.Alive && p.Grounded && !p.Seated && Vector3.Distance(p.Feet, _pos) <= pd.RangeM && _sight(from, p.Feet + new Vector3(0, Up, 0))) cands.Add(p);
        if (cands.Count == 0 || _rng.NextDouble() >= pd.Chance) return false;
        if (cands.Count > 1) cands.RemoveAll(p => p.Id == _lastVictim);
        var v = cands[_rng.Next(cands.Count)];
        _victim = v.Id; _target = v.Id; _hold.Enter(PounceHold.Marked); _fsm.Enter(DemonPhase.PounceWind, pd.WindS);
        Face(v.Feet);
        events.Add(new DemonPounced(v.Id, PounceStage.Wind));
        return true;
    }

    /// <summary>
    /// The one way out of a pounce: the victim (if any) is let go as <paramref name="stage"/> (Released after the pin, Aborted otherwise), remembered as the last victim, and the cooldown starts.
    /// No-op without a victim.
    /// </summary>
    void EndPounce(List<DemonEvent> events, PounceStage stage)
    {
        if (_hold.Is(PounceHold.None)) return;
        events.Add(new DemonPounced(_victim, stage));
        _lastVictim = _victim; _hold.Enter(PounceHold.None);
        _pounceCd.Start(_def.Pounce.CooldownS);
    }

    void StepPounceWind(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        if (_hold.Is(PounceHold.None) || Find(players, _victim) is not { Alive: true } v)
        {
            EndPounce(events, PounceStage.Aborted);
            Rest(RollDwell() * 0.5f);
            return;
        }
        Face(v.Feet);
        if (!_fsm.Tick(dt)) return;
        _leapA = _pos; _leapNA = _normal; _leapF0 = _forward; _landPos = v.Feet; _pounceU = 0;
        _pounceDur = MathF.Max(0.35f, Vector3.Distance(_pos, v.Feet) / LeapSpeed);
        _leapDir = ArcDir(_normal, Vector3.UnitY);
        _fsm.Enter(DemonPhase.Pouncing);
    }

    void StepPouncing(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        DemonPlayer? v = _hold.Is(PounceHold.None) ? null : Find(players, _victim);
        if (!_hold.Is(PounceHold.None) && !Valid(v)) EndPounce(events, PounceStage.Aborted); // it finishes the leap, but nobody is pinned
        if (Valid(v)) _landPos = v!.Value.Feet; // homing: the arc always ends on where the victim is now
        _pounceU += dt / _pounceDur;
        if (_pounceU < 1)
        {
            ArcPose(_leapA, _leapNA, _landPos, Vector3.UnitY, _pounceU, _def.Leap.ArcHeightM);
            return;
        }
        if (_hold.Is(PounceHold.None) || !Valid(v)) { _pos = _landPos; _normal = Vector3.UnitY; BeginPounceOff(players); return; }
        // it lands: the victim goes down flat on the floor where they are, head away from where the demon came from
        var feet = new Vector3(_landPos.X, _floorY(_landPos), _landPos.Z);
        var d = new Vector3(feet.X - _leapA.X, 0, feet.Z - _leapA.Z);
        if (d.LengthSquared() < 1e-4f) d = new Vector3(_forward.X, 0, _forward.Z);
        d = d.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(d);
        _pinFeet = feet; _pinDir = d;
        _pinRoot = feet + d * (LyingHalfM - _def.Pounce.HeadForwardM * _def.Scale);
        _pos = feet; _normal = Vector3.UnitY; _forward = d; _landPos = feet;
        _settle = 0;
        _hold.Enter(PounceHold.Pinned); _fsm.Enter(DemonPhase.Pinning, _def.Pounce.PinS);
        events.Add(new DemonPounced(_victim, PounceStage.Pinned, feet, PinYaw));
    }

    void StepPinning(float dt, IReadOnlyList<DemonPlayer> players, List<DemonEvent> events)
    {
        if (_hold.Is(PounceHold.None) || Find(players, _victim) is not { Alive: true })
        {
            EndPounce(events, PounceStage.Aborted);
            BeginPounceOff(players);
            return;
        }
        _settle = MathF.Min(1f, _settle + dt / 0.3f);
        var k = _settle * _settle * (3 - 2 * _settle);
        _pos = Vector3.Lerp(_landPos, _pinRoot, k);
        if (!_fsm.Tick(dt)) return;
        EndPounce(events, PounceStage.Released);
        BeginPounceOff(players);
    }

    /// <summary>The pounce is over: it leaps to an anchor on a wall or the ceiling (forced, from wherever it stands) and the fight goes on.</summary>
    void BeginPounceOff(IReadOnlyList<DemonPlayer> players)
    {
        UpdateTarget(0, players);
        var eye = _target is { } id && Find(players, id) is { } t ? t.Feet + new Vector3(0, Up, 0) : _pos + new Vector3(0, Up, 0);
        var best = PickLeapAnchor(eye, true);
        if (best < 0) best = PickLeapAnchor(eye, false);
        if (best < 0)
        {
            foreach (var a in _graph.Anchors)
                if (best < 0 || Vector3.DistanceSquared(a.Pos, _pos) < Vector3.DistanceSquared(_graph[best].Pos, _pos)) best = a.Id;
        }
        BeginLeap(best, MathF.Max(0.2f, _def.Leap.CrouchS * 0.5f));
    }

    /// <summary>The unit vector in the plane of <paramref name="normal"/> that <paramref name="v"/> projects to, or <paramref name="fallback"/> projected the same way.</summary>
    static Vector3 ProjectOnto(Vector3 v, Vector3 normal, Vector3 fallback)
    {
        var p = v - normal * Vector3.Dot(v, normal);
        if (p.LengthSquared() < 1e-6f) p = fallback - normal * Vector3.Dot(fallback, normal);
        if (p.LengthSquared() < 1e-6f) p = Vector3.Cross(normal, MathF.Abs(normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        return Vector3.Normalize(p);
    }
}

/// <summary>The host's safety net for the pounce: which held players are no longer held by anything.</summary>
public static class PounceRules
{
    /// <summary>
    /// The players in <paramref name="held"/> that must be let go: there is no brain (no fight, the event ended, the demon was removed), or it is not pinning them now, or <paramref name="here"/>
    /// says they are gone or dead. Checked every tick by the host, whatever else happened, so no path leaves a player held.
    /// </summary>
    public static List<ushort> Stuck(IEnumerable<ushort> held, DemonBrain? brain, Func<ushort, bool> here)
    {
        var stuck = new List<ushort>();
        foreach (var id in held)
            if (brain is not { Phase: DemonPhase.Pinning } || brain.PounceVictim != id || !here(id)) stuck.Add(id);
        return stuck;
    }
}

using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The fence hop of a Thief (docs/events/thieves/05): a scripted move, off the navigation mesh, for a target behind a fence (<see cref="StealTarget.Hop"/>). Approach brings it to the point outside the fence; then
/// <see cref="GangMemberState.HopIn"/> (an arc over the line, clock: hop_seconds), then Steal (it walks straight to the target, its clock starting once it is there), then
/// <see cref="GangMemberState.HopOut"/> (walks straight back to the landing point, then the arc out, then Haul with the piece, or Seek with nothing). The two walks and the arc are told apart by the state's clock
/// (infinite while walking), never by a flag.
/// </summary>
public sealed partial class GangMember
{
    FenceHop? _hop;           // the fence it is hopping: set from the start of HopIn until it is back outside
    Vector3 _hopFrom;         // where the arc being flown starts
    float _hopGround;         // the ground under the arc (without its rise), for a member killed in the air

    PetLootConfig HopCfg => Work!.Config.Pet;

    /// <summary>Inside a hop (a garden or the air over its fence): off the navigation mesh.</summary>
    public bool Hopping => _hop != null;

    void BeginHop(FenceHop hop)
    {
        _hop = hop;
        _hopFrom = Position;
        _hopGround = Position.Y;
        _fsm.Enter(GangMemberState.HopIn, HopCfg.HopSeconds);
    }

    /// <summary>The point of the arc from <see cref="_hopFrom"/> to <paramref name="to"/>, <paramref name="t"/> in 0 to 1: a parabola whose top clears the fence.</summary>
    Vector3 Arc(Vector3 to, float t, float heightM)
    {
        var rise = heightM + HopCfg.HopClearM + MathF.Abs(to.Y - _hopFrom.Y) * 0.5f; // the top of the arc clears the fence (and the higher end)
        var y = _hopFrom.Y + (to.Y - _hopFrom.Y) * t;
        _hopGround = y;
        return new Vector3(_hopFrom.X + (to.X - _hopFrom.X) * t, y + 4f * rise * t * (1f - t), _hopFrom.Z + (to.Z - _hopFrom.Z) * t);
    }

    void TickHopIn(float dt)
    {
        var hop = _hop!.Value;
        var done = _fsm.Tick(dt);
        var t = MathF.Min(1f, _fsm.Elapsed / HopCfg.HopSeconds);
        Face(hop.In, dt * 4f);
        Position = done ? hop.In : Arc(hop.In, t, hop.HeightM);
        if (done) _fsm.Enter(GangMemberState.Steal); // no clock yet: it walks to the pet first (TickHopWalk)
    }

    /// <summary>Steal, inside a fence: walks straight to the pet; true while it does (the steal clock has not started). The clock starts within grab_m of the pet.</summary>
    bool TickHopWalk(StealTarget now, float dt)
    {
        if (!float.IsInfinity(_fsm.Left)) return false;
        if (Flat(Position, now.Position) <= HopCfg.GrabM) { _fsm.SetLeft(Work!.Config.Of(now.Kind).StealSeconds); return false; }
        Step(now.Position, dt, HopCfg.InWalkSpeedMps);
        return true;
    }

    /// <summary>It has the piece (or the target went): back to the fence. Called from Steal.</summary>
    void EnterHopOut() => _fsm.Enter(GangMemberState.HopOut);

    /// <summary>Steal without a target while inside a fence: out first. Outside: look for another.</summary>
    void EndSteal()
    {
        if (_hop != null) EnterHopOut(); else EnterSeek();
    }

    void TickHopOut(float dt)
    {
        var hop = _hop!.Value;
        if (float.IsInfinity(_fsm.Left)) // walking back to where it landed
        {
            Step(hop.In, dt, HopCfg.InWalkSpeedMps);
            if (Flat(Position, hop.In) > 0.15f) return;
            _hopFrom = Position;
            _fsm.Restart(HopCfg.HopSeconds);
            return;
        }
        var done = _fsm.Tick(dt);
        var t = MathF.Min(1f, _fsm.Elapsed / HopCfg.HopSeconds);
        Face(hop.Out, dt * 4f);
        Position = done ? hop.Out : Arc(hop.Out, t, hop.HeightM);
        if (!done) return;
        _hop = null;
        if (Carried != 0) EnterHaul(); else EnterSeek();
    }

    /// <summary>Called off or running away inside a fence: it is put outside at once (no time to hop by the book).</summary>
    void LeaveHop()
    {
        if (_hop is not { } hop) return;
        Position = hop.Out;
        _hop = null;
    }

    /// <summary>The ground under a member killed in the air over a fence (the arc's own ground), else where it stands.</summary>
    float HopGroundY() => State == GangMemberState.HopIn || (State == GangMemberState.HopOut && !float.IsInfinity(_fsm.Left)) ? _hopGround : Position.Y;

    /// <summary>The piece it carries is gone (the pet was killed in its hands): its hands are empty and, hauling or loading, it looks for something else.</summary>
    public void LoseCarried()
    {
        Carried = 0; CarriedId = 0;
        if (State is GangMemberState.Haul or GangMemberState.Load) EnterSeek();
    }
}

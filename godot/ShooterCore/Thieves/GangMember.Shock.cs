using System.Numerics;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// The electrocution of a Thief aloft (docs/events/thieves/07): when it reaches the top of a climb the target's kind may strike it (<see cref="IStealSource.AloftHazard"/>, rolled once, through
/// <see cref="IThiefWork.Hazard"/>). Shocked at the top, Falling to the base's height under gravity, Down on the ground, GetUp, then the same target again (ClimbUp). The hit count is not touched, the
/// claim on the target is kept; a hit while it lies counts as usual (a lying hit volume), one that kills it aloft or in the fall lets it fall on and lie dead (<see cref="FallStep"/>).
/// </summary>
public sealed partial class GangMember
{
    AloftShock _shock;      // the times of the shock being served

    /// <summary>Reached the top: rolls the kind's hazard; when it strikes the member is Shocked at once (the work clock of WorkAloft is dropped, it starts anew after the climb up again).</summary>
    void RollHazard(StealTarget now)
    {
        if (Work!.Hazard(now, this, _rng) is not { } shock) return;
        _shock = shock;
        _fsm.Enter(GangMemberState.Shocked, shock.ShockSeconds);
    }

    void TickShock(float dt)
    {
        switch (State)
        {
            case GangMemberState.Shocked:
                if (_job is { } job) Face(job.Position, dt);
                if (!_fsm.Tick(dt)) return;
                _fallV = 0;
                _fsm.Enter(GangMemberState.Falling);
                return;
            case GangMemberState.Falling:
                _fallV += Gravity * dt;
                var y = MathF.Max(_base.Y, Position.Y - _fallV * dt);
                Position = new Vector3(Position.X, y, Position.Z);
                if (y > _base.Y) return;
                _fallV = 0;
                _fsm.Enter(GangMemberState.Down, _shock.DownSeconds);
                return;
            case GangMemberState.Down:
                if (_fsm.Tick(dt)) _fsm.Enter(GangMemberState.GetUp, _shock.GetUpSeconds);
                return;
            case GangMemberState.GetUp:
                if (!_fsm.Tick(dt)) return;
                if (StillThere(out _)) _fsm.Enter(GangMemberState.ClimbUp); // the same target again (StillThere lets it go and ends the job when it is gone)
                else EnterSeek();
                return;
        }
    }
}

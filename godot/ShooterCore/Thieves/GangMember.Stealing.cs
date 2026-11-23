using System.Numerics;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// The stealing half of the Gang member (docs/events/thieves/04): a Thief with <see cref="Work"/> seeks a target, goes to it, steals (or climbs, works aloft and climbs down), hauls the Loot to the
/// van's door and loads it. The states and their clocks are in <see cref="GangMember.Spec"/>; the target, the claim and the carried piece belong to the member, the climb's two points to the climb states.
/// </summary>
public sealed partial class GangMember
{
    const float Gravity = 9.81f;

    StealTarget? _job;
    Vector3 _base, _top;                // the climb: where it starts (the ground) and where it works
    float _groundY, _fallV;             // the ground a killed member lies on (below it when it died aloft) and how fast it falls
    readonly List<(StealTarget Target, float Left)> _avoid = new();

    /// <summary>What a Thief steals through and carries to: set by its Raid (<see cref="ThiefGang.Work"/>). Null: a Thief stands about, as before the Loot.</summary>
    public IThiefWork? Work { get; set; }

    /// <summary>The target it goes for or works at (claimed for it), or null.</summary>
    public StealTarget? Job => _job;

    /// <summary>The id of the piece it carries, in its source's numbers (<see cref="Carried"/> says the kind). Not on the wire.</summary>
    public int CarriedId { get; private set; }

    /// <summary>What it carries, or null.</summary>
    public LootOrigin? CarriedOrigin => LootKinds.OfCarried(Carried) is { } k ? new LootOrigin(k, CarriedId) : null;

    /// <summary>Stealing, climbing or hauling: it does not join a fight.</summary>
    public bool Working => State is GangMemberState.Steal or GangMemberState.ClimbUp or GangMemberState.WorkAloft or GangMemberState.ClimbDown or GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp or GangMemberState.Haul or GangMemberState.Load or GangMemberState.HopIn or GangMemberState.HopOut;

    /// <summary>On the way to a target or at one, or on the way to the van with it: the Loot it will bring counts against the van's capacity.</summary>
    public bool HasJob => Working || State == GangMemberState.Approach;

    /// <summary>Climbing or working aloft: off the ground.</summary>
    bool Aloft => State is GangMemberState.ClimbUp or GangMemberState.WorkAloft or GangMemberState.ClimbDown or GangMemberState.Shocked or GangMemberState.Falling;

    /// <summary>Where it lies or stands on the ground: under it, for one killed aloft.</summary>
    public Vector3 RestPoint => State == GangMemberState.Dead ? new Vector3(Position.X, _groundY, Position.Z) : Position;

    // ---- the loop ----

    void TickStealing(float dt)
    {
        if (Work == null) return;
        switch (State)
        {
            case GangMemberState.Seek: TickSeek(dt); break;
            case GangMemberState.Approach: TickApproach(dt); break;
            case GangMemberState.Steal: TickSteal(dt); break;
            case GangMemberState.ClimbUp: TickClimbUp(dt); break;
            case GangMemberState.WorkAloft: TickWorkAloft(dt); break;
            case GangMemberState.ClimbDown: TickClimbDown(dt); break;
            case GangMemberState.Shocked: case GangMemberState.Falling: case GangMemberState.Down: case GangMemberState.GetUp: TickShock(dt); break;
            case GangMemberState.HopIn: TickHopIn(dt); break;
            case GangMemberState.HopOut: TickHopOut(dt); break;
            case GangMemberState.Haul: TickHaul(dt); break;
            case GangMemberState.Load: TickLoad(dt); break;
        }
    }

    void EnterSeek()
    {
        ClearRoute();
        _fsm.Enter(GangMemberState.Seek, 0f); // looks on the next tick
    }

    void TickSeek(float dt)
    {
        if (!_fsm.Tick(dt)) return;
        var cfg = Work!.Config;
        StealTarget t;
        if (_job is { } given && Work.TryFind(given, out var found)) t = found; // handed to it (<see cref="Delegate"/>): already claimed
        else if (Work.Pick(this, Allowed) is { } picked) { ReleaseJob(); t = picked; }
        else { ReleaseJob(); _fsm.Restart(cfg.SeekRetrySeconds); return; }
        _job = t;
        ClearRoute();
        _fsm.Enter(GangMemberState.Approach, cfg.ApproachGiveUpSeconds);
        Request(t.WorkSpot);
    }

    /// <summary>Whether the Thief can be handed a target now: free hands, looking for work or on its way to some (not stealing, climbing or carrying).</summary>
    public bool CanDelegate => Kind == GangMemberKind.Thief && Alive && Work != null && Carried == 0 && State is GangMemberState.Seek or GangMemberState.Approach or GangMemberState.Idle or GangMemberState.Walk;

    /// <summary>
    /// The Thief is handed <paramref name="target"/> (claimed for it by the caller) in place of what it was after (that claim ends): it goes for it as its next target. False when it cannot take one
    /// (<see cref="CanDelegate"/>, or the target is one it gave up lately); the caller keeps the claim then.
    /// </summary>
    public bool Delegate(in StealTarget target)
    {
        if (!CanDelegate || !Allowed(target)) return false;
        ReleaseJob();
        _job = target;
        switch (State)
        {
            case GangMemberState.Approach: EnterSeek(); break; // looks at once: takes the given target
            case GangMemberState.Seek: _fsm.Restart(0f); break;
        }
        return true; // Idle and Walk reach Seek by themselves
    }

    bool Allowed(StealTarget t)
    {
        foreach (var a in _avoid) if (a.Target.Key == t.Key) return false;
        return true;
    }

    void TickAvoid(float dt)
    {
        for (var i = _avoid.Count - 1; i >= 0; i--)
        {
            var (t, left) = _avoid[i];
            if ((left -= dt) <= 0) _avoid.RemoveAt(i); else _avoid[i] = (t, left);
        }
    }

    void TickApproach(float dt)
    {
        if (_job is not { } job) { EnterSeek(); return; }
        if (!Work!.TryFind(job, out var now)) { ReleaseJob(); EnterSeek(); return; } // gone (a car was driven off, Loot was taken or sent back)
        _job = now;
        if (_fsm.Tick(dt)) { GiveUpJob(); return; } // not there in time
        var cfg = Work.Config;
        var arrived = GoTo(now.WorkSpot, dt, cfg.ApproachSpeedMps, cfg.ArriveM);
        if (!arrived && _route.Count > 0 && _next >= _route.Count && Flat(Position, now.WorkSpot) <= _d.RepathM) arrived = true; // the route ends at the mesh's nearest point
        if (arrived) BeginWork(now);
    }

    void BeginWork(StealTarget t)
    {
        ClearRoute();
        var cfg = Work!.Config;
        if (t.Hop is { } hop) { BeginHop(hop); return; }
        if (t.NeedsClimb)
        {
            _base = t.WorkSpot; _top = t.Top!.Value;
            Position = _base;
            _fsm.Enter(GangMemberState.ClimbUp);
        }
        else _fsm.Enter(GangMemberState.Steal, t.Lying ? cfg.PickupSeconds : cfg.Of(t.Kind).StealSeconds);
    }

    void TickSteal(float dt)
    {
        if (_job is not { } job || !Work!.TryFind(job, out var now)) { ReleaseJob(); EndSteal(); return; } // the target went while it worked
        if (_hop != null && TickHopWalk(now, dt)) return; // inside a fence: walking to the pet, the clock has not started
        Face(now.Position, dt);
        if (_fsm.Tick(dt)) FinishWork(now);
    }

    /// <summary>The work time is up: the piece is in its hands (or was no longer there). At the ground it hauls it away; aloft it climbs down first.</summary>
    void FinishWork(StealTarget t)
    {
        var piece = Work!.Take(t);
        _job = null;
        if (piece is { } p) { Carried = LootKinds.CarriedOf(p.Kind); CarriedId = p.Id; }
        if (_hop != null) { EnterHopOut(); return; }
        if (Aloft) { _fsm.Enter(GangMemberState.ClimbDown); return; }
        if (piece == null) EnterSeek(); else EnterHaul();
    }

    // ---- the climb ----

    void TickClimbUp(float dt)
    {
        if (!StillThere(out var now)) { _fsm.Enter(GangMemberState.ClimbDown); return; } // came for nothing: down again
        Face(now.Position, dt);
        Position = MoveTo(Position, _top, Work!.Config.ClimbSpeed * dt);
        if (Position == _top) { _fsm.Enter(GangMemberState.WorkAloft, Work.Config.Of(now.Kind).StealSeconds); RollHazard(now); }
    }

    void TickWorkAloft(float dt)
    {
        if (!StillThere(out var now)) { _fsm.Enter(GangMemberState.ClimbDown); return; }
        Face(now.Position, dt);
        if (_fsm.Tick(dt)) FinishWork(now);
    }

    void TickClimbDown(float dt)
    {
        Position = MoveTo(Position, _base, Work!.Config.ClimbSpeed * dt);
        if (Position != _base) return;
        if (Carried != 0) EnterHaul(); else EnterSeek();
    }

    /// <summary>The target while it is still there (releasing the claim and giving up the job when it is not).</summary>
    bool StillThere(out StealTarget now)
    {
        now = default;
        if (_job is not { } job) return false;
        if (Work!.TryFind(job, out now)) { _job = now; return true; }
        ReleaseJob();
        return false;
    }

    /// <summary>Called off or running away while aloft: it climbs down at once (no time to do it by the book).</summary>
    void LeaveAloft()
    {
        if (Aloft) Position = _base;
        LeaveHop();
    }

    // ---- hauling ----

    void EnterHaul()
    {
        ClearRoute();
        _fsm.Enter(GangMemberState.Haul, Work!.Config.HaulGiveUpSeconds);
        if (Work.Door is { } door) Request(door);
    }

    void TickHaul(float dt)
    {
        if (_fsm.Tick(dt) || Work!.Door is not { } door) { DropHere(); return; } // too long on the way, or the van takes no more
        var cfg = Work.Config;
        var arrived = GoTo(door, dt, cfg.HaulSpeedMps, cfg.LoadReachM);
        if (!arrived && _route.Count > 0 && _next >= _route.Count && Flat(Position, door) <= _d.RepathM) arrived = true;
        if (!arrived) return;
        ClearRoute();
        _fsm.Enter(GangMemberState.Load, cfg.LoadSeconds);
    }

    void TickLoad(float dt)
    {
        if (Work!.Door is { } door) Face(door, dt);
        if (!_fsm.Tick(dt)) return;
        if (CarriedOrigin is { } piece && Work.Load(piece)) { Carried = 0; CarriedId = 0; EnterSeek(); }
        else DropHere();
    }

    /// <summary>Puts what it carries down where it stands and looks for something else.</summary>
    void DropHere()
    {
        PutDownCarried(Position);
        EnterSeek();
    }

    /// <summary>What it carries lies at <paramref name="at"/> (a kind that does not lie goes back at once); its hands are empty.</summary>
    void PutDownCarried(Vector3 at)
    {
        if (CarriedOrigin is { } piece) Work?.Drop(piece, at);
        Carried = 0; CarriedId = 0;
    }

    /// <summary>
    /// A hit that did not kill it (<see cref="GangConfig.HitDropsLoot"/>): a Thief hauling, loading, climbing down or hopping out with Loot drops it where it stands (on the ground, at the base of a climb, outside
    /// the fence of a garden) and looks for work again. Free hands: the Raid's aggro (the hit started it) then sends it at the player. Nothing for a member that boards or flees, or that carries nothing.
    /// </summary>
    void DropOnHit()
    {
        if (!_d.HitDropsLoot || Work == null || CarriedOrigin == null || State is not (GangMemberState.Haul or GangMemberState.Load or GangMemberState.ClimbDown or GangMemberState.HopOut)) return;
        LeaveAloft(); // down at the base / out over the fence at once (no time to do it by the book)
        PutDownCarried(Position);
        EnterSeek();
    }

    // ---- ending a job ----

    /// <summary>Lets the target go: the claim ends.</summary>
    void ReleaseJob()
    {
        if (_job is { } j) Work?.Release(j);
        _job = null;
    }

    /// <summary>Gives the target up (no way to it, not there in time): it is not chosen again for a while, and the Thief looks for another.</summary>
    void GiveUpJob()
    {
        if (_job is { } j) _avoid.Add((j, Work!.Config.AvoidSeconds));
        ReleaseJob();
        EnterSeek();
    }

    /// <summary>A killed member that was aloft falls to the ground and lies there.</summary>
    void FallStep(float dt)
    {
        if (Position.Y <= _groundY) { _fallV = 0; return; }
        _fallV += Gravity * dt;
        Position = new Vector3(Position.X, MathF.Max(_groundY, Position.Y - _fallV * dt), Position.Z);
    }

    static Vector3 MoveTo(Vector3 from, Vector3 to, float step)
    {
        var d = to - from;
        var len = d.Length();
        return len <= step || len < 1e-5f ? to : from + d * (step / len);
    }
}

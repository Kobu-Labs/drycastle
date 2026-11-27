using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// The Rival crew's ways of a Gang member (#495, #496; GLOSSARY "Gunman"): a Gunman fights at range (Aim, Fire: real rounds through <see cref="IGangWorld.Shoot"/>, only at a target in its line of sight,
/// <see cref="IGangWorld.Clear"/>), and any member may be posted (<see cref="Post"/>: it holds a spot, turns and fights from there, never walks).
/// </summary>
public sealed partial class GangMember
{
    Countdown _sightCheck;   // when it looks again whether its target is in sight
    bool _inSight;           // what the last look found
    int _roundsLeft;         // rounds of the burst still to fire (Fire)
    Countdown _roundGap;     // until the next round of the burst
    GangPost? _post;

    /// <summary>The weapon a Gunman carries (by its look number, so every client draws the same), or null for a melee kind.</summary>
    public GangGunWeapon? Weapon { get; }

    /// <summary>Fights at range: a kind with a gun.</summary>
    public bool Ranged => Weapon != null;

    /// <summary>
    /// The aim error of its rounds now, degrees, when something else sets it: a Gunman shooting from a moving car (docs/cartel.md "Chase cars") turns each round off its line by up to this instead of the gun's own
    /// aim_error_deg. Null: the gun's own.
    /// </summary>
    public float? AimErrorDeg { get; set; }

    /// <summary>The spot it holds (<see cref="Post"/>), or null when it moves about.</summary>
    public GangPost? PostedAt => _post;

    /// <summary>
    /// Posts it: it stands at <paramref name="at"/> (a floor point, no navigation mesh needed) facing <paramref name="facing"/> (Godot yaw, 0 faces -Z) from now on, and fights from there. Given right after it is
    /// added (it guards then). Not for the dead.
    /// </summary>
    public void Post(Vector3 at, float facing)
    {
        if (!Alive || Ended != null) return;
        ReleaseJob();
        _post = new GangPost(at, facing);
        Position = Home = at;
        Yaw = facing;
        ClearRoute();
        _fsm.Enter(GangMemberState.Posted);
    }

    /// <summary>
    /// Moves a posted member's spot (a rider of a moving car: its post is a seat, GLOSSARY "Chase car"): it stands at <paramref name="at"/> facing <paramref name="facing"/> from now on, in whatever state it is
    /// in (an Aim or a Fire goes on). Nothing for a member that is not posted, dead or gone.
    /// </summary>
    public void MovePost(Vector3 at, float facing)
    {
        if (_post == null || !Alive || Ended != null) return;
        _post = new GangPost(at, facing);
        Position = Home = at;
    }

    /// <summary>
    /// Takes a posted member off its spot: it stands at <paramref name="at"/> facing <paramref name="facing"/> and from now on moves about and fights as any member of its gang does, heading for the gang's post
    /// (a crew that gets out of its car). Nothing for a member that is not posted, dead or gone.
    /// </summary>
    public void Unpost(Vector3 at, float facing)
    {
        if (_post == null || !Alive || Ended != null) return;
        _post = null;
        Position = at; Yaw = facing;
        ClearRoute();
        _stuck.Moved(at);
        _fsm.Enter(GangMemberState.Guard);
    }

    Vector3 Eye => Position + Vector3.UnitY * _d.Sight.EyeM;
    Vector3 ChestOf(GangPlayer t) => t.Feet + Vector3.UnitY * _d.Sight.ChestM;

    /// <summary>A Gunman with a target: aims and fires while it sees it within range, else closes in (or, posted, turns to it and waits).</summary>
    void TickGunFight(GangPlayer t, float dt)
    {
        var gun = _k.Gun!;
        _sightCheck.Tick(dt);
        if (_sightCheck.Ready)
        {
            _inSight = Flat(Position, t.Feet) <= gun.RangeM && _world.Clear(Eye, ChestOf(t));
            _sightCheck.Start(_d.Sight.CheckSeconds);
        }
        var canShoot = _inSight && Flat(Position, t.Feet) <= gun.RangeM;
        switch (State)
        {
            case GangMemberState.Aim:
                Face(t.Feet, dt);
                if (!canShoot) { NoShot(t, dt); return; }
                if (_fsm.Tick(dt)) StartBurst(t);
                return;
            case GangMemberState.Fire:
                Face(t.Feet, dt);
                _roundGap.Tick(dt);
                while (_roundsLeft > 0 && _roundGap.Ready)
                {
                    if (canShoot) FireRound(t);
                    _roundsLeft--;
                    _roundGap.Start(Weapon!.RoundGapSeconds);
                }
                if (!_fsm.Tick(dt)) return;
                if (canShoot) _fsm.Enter(GangMemberState.Aim, gun.AimSeconds);
                else NoShot(t, dt);
                return;
        }
        if (canShoot)
        {
            ClearRoute();
            ReleaseJob();
            _fsm.Enter(GangMemberState.Aim, gun.AimSeconds);
            Face(t.Feet, dt);
            return;
        }
        NoShot(t, dt);
    }

    /// <summary>The burst begins: its first round now, the rest every round gap (the Fire clock lasts the burst, and at least a tick).</summary>
    void StartBurst(GangPlayer t)
    {
        var w = Weapon!;
        _fsm.Enter(GangMemberState.Fire, MathF.Max(w.Burst * w.RoundGapSeconds, 1f / 60));
        FireRound(t);
        _roundsLeft = w.Burst - 1;
        _roundGap.Start(w.RoundGapSeconds);
    }

    /// <summary>The target is out of sight or range: a posted Gunman turns to it and holds its spot; any other closes in on a route.</summary>
    void NoShot(GangPlayer t, float dt)
    {
        if (_post != null)
        {
            _fsm.Enter(GangMemberState.Posted);
            Face(t.Feet, dt);
            return;
        }
        if (_fsm.Enter(GangMemberState.Chase)) { ClearRoute(); ReleaseJob(); }
        var stop = _k.Gun!.ChaseStopM;
        if (GoTo(t.Feet, dt, _k.RunSpeed, stop)) { Face(t.Feet, dt); return; }
        if (_route.Count == 0 && RouteRequest == null && Flat(Position, t.Feet) <= DirectM) Step(t.Feet, dt, _k.RunSpeed); // no route and nearly there: straight at it
    }

    /// <summary>One round from the muzzle at the target's chest, turned off its line by the aim error; the world resolves it.</summary>
    void FireRound(GangPlayer t)
    {
        var gun = _k.Gun!;
        var ahead = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        var from = Position + Vector3.UnitY * gun.MuzzleUpM + ahead * gun.MuzzleAheadM;
        var line = ChestOf(t) - from;
        if (line.LengthSquared() < 1e-6f) return;
        var dir = AimError(Vector3.Normalize(line), AimErrorDeg ?? gun.AimErrorDeg, (uint)_rng.Next());
        _world.Shoot(new GangShot(HitId, Weapon!, from, dir, gun.RangeM, t.Id, (uint)_rng.Next()));
    }

    /// <summary>A direction within <paramref name="errorDeg"/> of <paramref name="aim"/>, uniform by area in that cone (a pellet fan of one), from the seed.</summary>
    public static Vector3 AimError(Vector3 aim, float errorDeg, uint seed) => errorDeg > 0 ? HitMath.PelletDirections(aim, errorDeg, 1, seed)[0] : Vector3.Normalize(aim);

    /// <summary>A posted melee member with a target: strikes it within reach, else turns to it (it never walks off its spot).</summary>
    void TickPostedMelee(GangPlayer t, float dt)
    {
        var reach = _k.Melee.ReachM;
        Face(t.Feet, dt);
        if (Flat(Position, t.Feet) <= reach * 0.9f && MathF.Abs(t.Feet.Y - Position.Y) <= ReachDy && t.OnFoot) _fsm.Enter(GangMemberState.Windup, _k.Melee.WindupSeconds);
        else _fsm.Enter(GangMemberState.Posted);
    }

    /// <summary>Posted with nobody to fight: it stands and turns back to its facing.</summary>
    void TickPosted(float dt)
    {
        if (_post is not { } p) { Settle(); return; }
        Yaw = TurretMath.StepAngle(Yaw, p.Facing, _d.TurnRateDegPerS * MathF.PI / 180f * dt);
    }
}

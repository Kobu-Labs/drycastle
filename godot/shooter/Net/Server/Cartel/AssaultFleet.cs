using Godot;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Assaults of one running Heist (GLOSSARY "Assault"; docs/cartel.md "Assaults"): Rival crew vans that drive like chase cars (<see cref="ChaseUnit"/>, machine <c>Chase.Car</c>) after the Cartel van itself, put down
/// 250 to 400 m of drivable way from it (<see cref="AssaultSpawn"/>, widened rather than none), their crew shooting from the cab's windows and the rest riding seated in the cargo bay. The Heist's clock (<see cref="AssaultClock"/>)
/// says when one is due; the cap (<c>max_assaults_alive</c>) holds back a due one while that many are in the fight (a van with its crew riding or a member alive; a stalled one does not count). The NEWEST Assault whose crew still
/// rides is watched (<see cref="AssaultStall"/>): when it got no 10 m closer to the van in 10 s the next one is sent right away (the interval starts over from it) and it is removed with its crew once it is far from every
/// player and unseen. A van whose crew is dead stands without holding the cap and goes once the bodies are gone; the Job's end removes everything.
/// </summary>
internal sealed class AssaultFleet
{
    readonly ChaseServer _s;
    readonly HeistRun _run;
    readonly AssaultCarData _a;
    readonly ChaseData _drive;
    readonly List<ChaseUnit> _units = new();
    Countdown _look;   // the next look at the stalled ones, for removal

    public AssaultFleet(ChaseServer s, HeistRun run, AssaultCarData a)
    {
        _s = s; _run = run; _a = a;
        _drive = a.Drive(run.Data.Chase);
    }

    /// <summary>Assaults sent so far.</summary>
    public int Sent { get; private set; }

    /// <summary>How many Assaults are in the fight now (a stalled one is not).</summary>
    public int InFightCount
    {
        get
        {
            var n = 0;
            foreach (var u in _units) if (u.InFight) n++;
            return n;
        }
    }

    /// <summary>A due Assault may be sent: fewer than the cap are in the fight.</summary>
    public bool MaySend => InFightCount < _run.Data.MaxAssaultsAlive;

    /// <summary>The Heist's clock says an Assault is due: one goes (or none, with the reason logged). The clock goes on either way.</summary>
    public void SendDue()
    {
        if (Send() is { } why) GD.Print($"server: {_run.Job}: an Assault is due: none sent ({why})");
    }

    /// <summary>An admin's <c>/cartel assault</c>: an Assault now, by the clock's own spot rules and the cap (the clock goes on as it was). Null when it was sent; else why not.</summary>
    public string? SendNow() => Send() is { } why ? $"No Assault sent: {why}." : null;

    /// <summary>Sends one Assault, or says why not (the cap, no spot, no room in the world).</summary>
    string? Send()
    {
        if (!MaySend) return $"{InFightCount} of this heist's are still in the fight (max_assaults_alive {_run.Data.MaxAssaultsAlive})";
        var roads = _s.Roads;
        var off = _s.Open.Numbers;
        var target = new NVec2(_run.VanFeet.X, _run.VanFeet.Z);
        var rivals = new List<NVec2>(_run.Chases.CarPositions());
        foreach (var u in _units) if (u.Where is { } p) rivals.Add(p);
        var half = _a.SpawnHalfWidthM;
        var spot = AssaultSpawn.Pick(roads, target, _s.PlayerPositions(), rivals, _a, p => _s.VehicleNear(p),
            open: p => _s.Open.Spot(p, half),
            wayM: p => OffRoadRoute.Plan(roads, p, target, (x, y) => _s.Open.Line(x, y, half), off)?.Path.Length,
            _s.Rng, out var why);
        if (spot is not { } fit)
        {
            GD.Print($"server: {_run.Job}: Assault: {why}");
            return $"no spot for an Assault van: {why}";
        }
        var (gunmen, enforcers) = HeistRules.AssaultCrew(_run.Data, _run.Job.Tier, _run.Party.Count);
        var profile = new RivalProfile("Assault van", _drive, AssaultRules.SeatOrder(gunmen, enforcers), _a);
        if (ChaseUnit.Spawn(_s, _run, profile, new ChaseSpot(fit.Place, fit.Ground, fit.Yaw, 0)) is not { } unit) return "the Assault van could not be put down";
        _units.Add(unit);
        Sent++;
        GD.Print($"server: {_run.Job}: Assault van {Sent} at ({fit.Ground.X:F0},{fit.Ground.Y:F0}), {fit.WayM:F0} m of way from the van (band {fit.Band + 1} of {_a.SpawnBandsM.Length}: {_a.SpawnBandsM[fit.Band][0]:F0} to {_a.SpawnBandsM[fit.Band][1]:F0} m)");
        _run.Tell($"A Rival crew van is coming to {_run.AssaultPlace}!");
        return null;
    }

    /// <summary>One tick of the Heist: the vans, the stall watch, then the removal of stalled ones that are far and unseen.</summary>
    public void Tick(float dt)
    {
        var van = _run.VanFeet;
        var vanSpeed = _run.VanSpeed;
        ChaseUnit? newest = null;
        for (var i = _units.Count - 1; i >= 0; i--)
        {
            var u = _units[i];
            u.Tick(dt, van, vanSpeed);
            if (u.Finished) _units.RemoveAt(i);
        }
        foreach (var u in _units) if (u.Riding) newest = u; // the newest one whose crew still rides
        var stalled = false;
        foreach (var u in _units) stalled |= u.StepStall(u == newest, van, dt);
        if (stalled && _run.Heist.Assaults.Running && Send() is null) _run.Heist.Assaults.Restart(); // the next right away; the interval starts over from it
        _look.Tick(dt);
        if (_look.Ready)
        {
            _look.Start(_a.RemoveCheckSeconds);
            RemoveFarAndUnseen();
        }
    }

    /// <summary>A stalled Assault more than remove_far_m from every player and out of every player's sight is removed with its crew.</summary>
    void RemoveFarAndUnseen()
    {
        var players = _s.PlayerPositions();
        var far2 = _a.RemoveFarM * _a.RemoveFarM;
        foreach (var u in _units)
        {
            if (u.Stall is not { State: AssaultStallState.Stalled } || u.Where is not { } at) continue;
            var near = false;
            foreach (var p in players) if (NVec2.DistanceSquared(p, at) < far2) { near = true; break; }
            if (near || _s.SeenByAnyone(at)) continue;
            GD.Print($"server: {_run.Job}: the stalled Assault van at ({at.X:F0},{at.Y:F0}) is far from every player and unseen: removed with its crew");
            u.Remove();
        }
    }

    public void AfterVehicles()
    {
        foreach (var u in _units) u.PlaceRiders();
    }

    /// <summary>The Heist ended: every van and crew goes, the ones that are out too.</summary>
    public void Clear()
    {
        foreach (var u in _units) u.Remove();
        _units.Clear();
        _s.Release(this);
    }
}

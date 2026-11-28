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
/// says when one is due; the cap (<c>max_assaults_alive</c>) holds back a due one while that many are in the fight (a van with its crew riding or a member alive; a stalled one does not count). Every Assault whose crew rides
/// is watched (<see cref="AssaultStall"/>): when it got no 10 m closer to the van in 10 s (or is lost) it is relocated to a fresh spot (band rules) when no human sees it and all players are far; only when it cannot be,
/// the next one is sent right away (the interval starts over from it) and it does not count meanwhile. At most max_assaults_alive + 1 vans exist at once. A van whose crew is dead stands without holding the cap and goes once the bodies are gone; the Job's end removes everything.
/// </summary>
internal sealed class AssaultFleet
{
    readonly ChaseServer _s;
    readonly HeistRun _run;
    readonly AssaultCarData _a;
    readonly ChaseData _drive;
    readonly List<ChaseUnit> _units = new();
    NVec3? _objectivePost; // the objective's centre snapped onto the navigation mesh (looked up once)

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

    /// <summary>Assault vans of this heist that exist now (in the fight, stalled or not): never more than max_assaults_alive + 1.</summary>
    public int ExistingCount
    {
        get
        {
            var n = 0;
            foreach (var u in _units) if (u.Exists) n++;
            return n;
        }
    }

    /// <summary>A due Assault may be sent: fewer than the cap count (a stalled one that could not be relocated does not) and fewer than the hard cap exist.</summary>
    public bool MaySend => AssaultRules.MaySend(InFightCount, ExistingCount, _run.Data.MaxAssaultsAlive);

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
        if (!MaySend) return $"{InFightCount} of this heist's count towards the cap, {ExistingCount} exist (max_assaults_alive {_run.Data.MaxAssaultsAlive}, at most one more than that exist)";
        var spot = PickSpot(null, out var why);
        if (spot is not { } fit)
        {
            GD.Print($"server: {_run.Job}: Assault: {why}");
            return $"no spot for an Assault van: {why}";
        }
        var (gunmen, enforcers) = HeistRules.AssaultCrew(_run.Data, _run.Job.Tier, _run.Party.Count);
        var profile = new RivalProfile("Assault van", _drive, AssaultRules.SeatOrder(gunmen, enforcers), _a, Post: CrewPost, Fresh: Fresh, Trail: () => _run.Chases.TrailPoints);
        if (ChaseUnit.Spawn(_s, _run, profile, new ChaseSpot(fit.Place, fit.Ground, fit.Yaw, 0)) is not { } unit) return "the Assault van could not be put down";
        _units.Add(unit);
        Sent++;
        GD.Print($"server: {_run.Job}: Assault van {Sent} at ({fit.Ground.X:F0},{fit.Ground.Y:F0}), {fit.WayM:F0} m of way from the van (band {fit.Band + 1} of {_a.SpawnBandsM.Length}: {_a.SpawnBandsM[fit.Band][0]:F0} to {_a.SpawnBandsM[fit.Band][1]:F0} m)");
        _run.Tell($"A Rival crew van is coming to {_run.AssaultPlace}!");
        return null;
    }

    /// <summary>A spot for an Assault van by the band rules from the van (null: none; <paramref name="why"/>). <paramref name="self"/>: a van that is moved, not counted as another vehicle.</summary>
    AssaultSpot? PickSpot(ChaseUnit? self, out string why)
    {
        var roads = _s.Roads;
        var off = _s.Open.Numbers;
        var target = new NVec2(_run.VanFeet.X, _run.VanFeet.Z);
        var rivals = new List<NVec2>(_run.Chases.CarPositions());
        foreach (var u in _units) if (u != self && u.Where is { } p) rivals.Add(p);
        var half = _a.SpawnHalfWidthM;
        return AssaultSpawn.Pick(roads, target, _s.PlayerPositions(), rivals, _a, p => _s.VehicleNear(p),
            open: p => _s.Open.Spot(p, half),
            wayM: p => OffRoadRoute.Plan(roads, p, target, (x, y) => _s.Open.Line(x, y, half), off)?.Path.Length,
            _s.Rng, out why);
    }

    /// <summary>The fresh spot of a van that is lost or stalled: the band rules again, from the van.</summary>
    ChaseSpot? Fresh(ChaseUnit self) => PickSpot(self, out _) is { } f ? new ChaseSpot(f.Place, f.Ground, f.Yaw, 0) : null;

    /// <summary>Where an Assault crew on foot heads for: the objective's centre (snapped onto the navigation mesh) while the Heist is at the objective, else the Cartel van (on the road, at the drop).</summary>
    NVec3 CrewPost(NVec3 van) => _run.Heist.Phase == HeistPhase.AtObjective ? _objectivePost ??= _s.Rivals.Snap(_run.Goal.Centre) : van;

    /// <summary>One tick of the Heist: the vans (each watches its own progress and is relocated when lost or stalled), then the next Assault right away when one stalled and could not be relocated.</summary>
    public void Tick(float dt)
    {
        var van = _run.VanFeet;
        var vanSpeed = _run.VanSpeed;
        var stalled = false;
        for (var i = _units.Count - 1; i >= 0; i--)
        {
            var u = _units[i];
            u.Tick(dt, van, vanSpeed);
            stalled |= u.NewlyStalled;
            if (u.Finished) _units.RemoveAt(i);
        }
        if (stalled && _run.Heist.Assaults.Running && Send() is null) _run.Heist.Assaults.Restart(); // the next right away; the interval starts over from it
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

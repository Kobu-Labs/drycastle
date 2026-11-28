using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Thieves;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// The Rival vehicles of the Heists on the server, chase cars and Assault vans (GLOSSARY "Chase car", "Assault"; docs/cartel.md "Chase cars", "Assaults"): what each Heist's <see cref="ChaseFleet"/> and <see cref="AssaultFleet"/>
/// share (the roads, the vehicles, the crews, where the players are, what a player can see) and the re-seating of every rider after the vehicles moved (<see cref="AfterVehicles"/>).
/// </summary>
internal sealed class ChaseServer
{
    /// <summary>A spot is not used this close to a vehicle.</summary>
    const float VehicleApartM = 6f;
    /// <summary>A player farther than this cannot see a spot (no line is looked for).</summary>
    const float SightM = 800f;
    /// <summary>Eye height of a player, and the height above the ground of the point of a spot that is looked at.</summary>
    const float EyeM = 1.6f, SpotM = 1.2f;

    readonly ServerContext _ctx;
    readonly Func<JobVehicles> _vehicles;
    readonly Func<RoadGraph> _roads;
    readonly List<ChaseFleet> _fleets = new();
    readonly List<AssaultFleet> _assaults = new();

    public ChaseServer(ServerContext ctx, Func<JobVehicles> vehicles, RivalCrewServer rivals, Func<RoadGraph> roads, Func<float, float, float> ground, OpenGround open, Random rng)
    {
        _ctx = ctx; _vehicles = vehicles; Rivals = rivals; _roads = roads; Ground = ground; Open = open; Rng = rng;
    }

    public JobVehicles Vehicles => _vehicles();
    public RivalCrewServer Rivals { get; }
    /// <summary>The roads the Raid vans drive (the chase cars drive them too).</summary>
    public RoadGraph Roads => _roads();
    public Func<float, float, float> Ground { get; }
    /// <summary>What is open ground for a car (cartel.json off_road): where a chase car may stand and drive besides the roads.</summary>
    public OpenGround Open { get; }
    public Random Rng { get; }

    /// <summary>The chase cars of a Heist that has begun: it listens to the van leaving the objective and reaching the drop.</summary>
    public ChaseFleet Begin(HeistRun run)
    {
        var fleet = new ChaseFleet(this, run);
        _fleets.Add(fleet);
        return fleet;
    }

    public void Release(ChaseFleet fleet) => _fleets.Remove(fleet);

    /// <summary>The Assault vans of a Heist that has begun.</summary>
    public AssaultFleet BeginAssaults(HeistRun run, AssaultCarData data)
    {
        var fleet = new AssaultFleet(this, run, data);
        _assaults.Add(fleet);
        return fleet;
    }

    public void Release(AssaultFleet fleet) => _assaults.Remove(fleet);

    /// <summary>After the vehicles moved: every crew rides on its car's seats of this tick (the figures are drawn where the car is, not one tick behind).</summary>
    public void AfterVehicles()
    {
        foreach (var f in _fleets) f.AfterVehicles();
        foreach (var f in _assaults) f.AfterVehicles();
    }

    /// <summary>Where every living player stands (x, z): a chase car is put down at least spawn_player_min_m from all of them.</summary>
    public List<NVec2> PlayerPositions()
    {
        var list = new List<NVec2>();
        foreach (var c in _ctx.Players.Values)
            if (_ctx.IsAlive(c.Id)) { var p = c.Body.GlobalPosition; list.Add(new NVec2(p.X, p.Z)); }
        return list;
    }

    /// <summary>The spot (x, z) cannot be used: a human of the party has a clear line to it (the same line a Gunman's sight uses), or a vehicle stands beside it.</summary>
    public bool Blocked(NVec2 spot, IReadOnlySet<ushort> party) => VehicleNear(spot) || SeenBy(spot, party);

    /// <summary>A vehicle of the world stands beside the spot (x, z).</summary>
    public bool VehicleNear(NVec2 spot) => !Vehicles.Clear(spot, VehicleApartM);

    /// <summary>Any human player (a bot is no eye) has a clear line to the spot: it is seen. The Assault van's removal waits for this to be false.</summary>
    public bool SeenByAnyone(NVec2 spot) => SeenBy(spot, _ctx.Players.Keys);

    /// <summary>A living human among <paramref name="who"/> has a clear line (the same line a Gunman's sight uses) to the spot (x, z), a little over the ground, from their eye.</summary>
    bool SeenBy(NVec2 spot, IEnumerable<ushort> who)
    {
        var at = new NVec3(spot.X, Ground(spot.X, spot.Y) + SpotM, spot.Y);
        foreach (var id in who)
        {
            if (!_ctx.Players.TryGetValue(id, out var c) || c.IsBot || !_ctx.IsAlive(id)) continue;
            var p = c.Body.GlobalPosition;
            if (NVec2.Distance(new NVec2(p.X, p.Z), spot) > SightM) continue;
            if (Rivals.Clear(new NVec3(p.X, p.Y + EyeM, p.Z), at)) return true;
        }
        return false;
    }
}

/// <summary>
/// The chase cars of one running Heist (<see cref="HeistRun"/>): the spawn clock (<see cref="ChaseClock"/>: it starts when the van leaves the objective with the minimum, stops when it reaches the drop), the
/// van's trail (<see cref="ChaseTrail"/>), and the cars that are out (<see cref="ChaseUnit"/>). The Heist ticks it; its end removes every car and crew.
/// </summary>
internal sealed class ChaseFleet
{
    readonly ChaseServer _s;
    readonly HeistRun _run;
    readonly ChaseData _d;
    readonly ChaseClock _clock;
    readonly List<ChaseUnit> _units = new();
    ChaseTrail? _trail;

    public ChaseFleet(ChaseServer s, HeistRun run)
    {
        _s = s; _run = run; _d = run.Data.Chase; _clock = new ChaseClock(_d);
        run.LeftObjective += OnLeft;
        run.ReachedDrop += OnReached;
    }

    /// <summary>Chase cars still in the fight (a crew inside or a member alive).</summary>
    public int Alive
    {
        get
        {
            var n = 0;
            foreach (var u in _units) if (u.InFight) n++;
            return n;
        }
    }

    void OnLeft(HeistRun run)
    {
        if (!_clock.Start()) return;
        Trail().Note(Flat(run.VanFeet));
        GD.Print($"server: {run.Job}: the van left the objective with {run.Loaded} loaded: chase cars from {_d.FirstAfterSeconds:F0} s, then every {_d.IntervalSeconds:F0} s, at most {_d.MaxAlive}");
    }

    void OnReached(HeistRun run)
    {
        _clock.Stop();
        GD.Print($"server: {run.Job}: the van reached the drop: no new chase car ({Alive} still fight)");
    }

    static NVec2 Flat(NVec3 p) => new(p.X, p.Z);

    ChaseTrail Trail() => _trail ??= new ChaseTrail(Flat(_run.Goal.Centre));

    /// <summary>One tick of the Heist: the trail, the cars, then a new car when one is due.</summary>
    public void Tick(float dt)
    {
        var van = _run.VanFeet;
        if (_clock.State == ChaseClockState.Running) Trail().Note(Flat(van));
        for (var i = _units.Count - 1; i >= 0; i--)
        {
            var u = _units[i];
            u.Tick(dt, van, _run.VanSpeed);
            if (u.Finished) _units.RemoveAt(i);
        }
        if (!_clock.Tick(dt, Alive)) return;
        if (TrySpawn() == null) _clock.Sent(); else _clock.NoSpot();
    }

    /// <summary>An admin's <c>/cartel chase</c>: a car now, by the clock's own spot rules and cap (the clock goes on as it was). Null when it was sent; else why not.</summary>
    public string? SendNow()
    {
        if (Alive >= _d.MaxAlive) return $"No chase car sent: {Alive} of this heist's are still in the fight (max_alive {_d.MaxAlive}).";
        Trail().Note(Flat(_run.VanFeet));
        return TrySpawn() is { } why ? $"No chase car sent: {why}." : null;
    }

    /// <summary>Where every chase car of this Heist stands (x, z): the Heist's Assault vans keep apart from them.</summary>
    public IEnumerable<NVec2> CarPositions()
    {
        foreach (var u in _units) if (u.Where is { } p) yield return p;
    }

    /// <summary>
    /// Puts one car down with its crew seated: on open drivable ground AHEAD of the van, an ambush (<c>ambush_side_m</c> beside the van's way to the drop, <c>spawn_ahead_min_m</c> to <c>spawn_ahead_max_m</c> along it, planned
    /// as a chase plans a way, facing along it; no sight rule, only no vehicle beside it) that waits for the van to pass and then follows, else (the drop is near) behind it on its trail, out of the party's sight. Null when it was; else why not.
    /// </summary>
    string? TrySpawn()
    {
        var roads = _s.Roads;
        var van = Flat(_run.VanFeet);
        var off = _s.Open.Numbers;
        var cars = new List<NVec2>(CarPositions());
        var party = _run.Party;
        // the van's way to the drop: roads where they bring it closer, open ground where that is shorter
        var ahead = OffRoadRoute.Plan(roads, van, Flat(_run.Receiver.Position), (a, b) => _s.Open.Line(a, b, off.CarHalfWidthM), off)?.Path;
        // any open drivable ground (not only a road), with a way from it to the van
        var spot = ChaseSpawn.Pick(roads, Trail(), van, _s.PlayerPositions(), cars, _d, p => _s.Blocked(p, party), _s.Rng,
            open: p => _s.Open.Spot(p, off.CarHalfWidthM),
            reach: p => OffRoadRoute.Plan(roads, p, van, (a, b) => _s.Open.Line(a, b, off.CarHalfWidthM), off) != null,
            ahead: ahead, aheadBlocked: p => _s.VehicleNear(p));
        if (spot is not { } fit)
            return $"no open ground ahead of or behind the van is {_d.SpawnPlayerMinM:F0} m from every player and out of the party's sight yet";
        var gunmen = ChaseRules.Gunmen(_d, _run.Job.Tier, _run.Party.Count);
        var crew = new List<GangMemberKind> { GangMemberKind.Enforcer };
        for (var i = 0; i < gunmen; i++) crew.Add(GangMemberKind.Gunman);
        if (ChaseUnit.Spawn(_s, _run, new RivalProfile("chase car", _d, crew), fit) is not { } unit) return "the car could not be put down";
        _units.Add(unit);
        GD.Print($"server: {_run.Job}: chase car {(fit.BackM < 0 ? $"{-fit.BackM:F0} m ahead of the van on its way to the drop, beside the way (an ambush)" : $"{fit.BackM:F0} m behind the van")}");
        _run.Tell("A Rival car is after the van!");
        return null;
    }

    public void AfterVehicles()
    {
        foreach (var u in _units) u.PlaceRiders();
    }

    /// <summary>The Heist ended: no more cars; every car and crew goes (the ones that are out too).</summary>
    public void Clear()
    {
        _run.LeftObjective -= OnLeft;
        _run.ReachedDrop -= OnReached;
        _clock.Stop();
        foreach (var u in _units) u.Remove();
        _units.Clear();
        _s.Release(this);
    }
}

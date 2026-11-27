using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Thieves;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// What a Heist kind brings to the template (docs/cartel.md "Heists"): its objective's place, the area the Cartel van must stand in to load there, and the words of its filling. The Harvest's is the
/// Contact's plantation; the Trap house heist plugs in its house the same way (<see cref="HeistServer.Begin"/>).
/// </summary>
internal interface IHeistGoal
{
    /// <summary>The objective named in the tracker line ("the plantation").</summary>
    string Place { get; }
    /// <summary>How the party fills a Duffel there, the start of the loading line ("Fill weed duffels in the plantation (hold E) and throw them in the van").</summary>
    string FillLine { get; }
    /// <summary>The objective's centre (Godot axes): the marker on the way there, where the Assaults park near and head for.</summary>
    NVec3 Centre { get; }
    /// <summary>The ground point stands in the objective's area (the van loads there; leaving it with the minimum stops the Assaults).</summary>
    bool InArea(NVec3 p);
    /// <summary>The Job ended: what the kind put in the world goes (a trap house is stripped).</summary>
    void End() { }
}

/// <summary>
/// The Heist template on the server (GLOSSARY "Heist", "Cartel van", "Assault"; docs/cartel.md "Heists", "Cartel van", "Assaults"): a kind's <see cref="IJobKind.Begin"/> calls <see cref="Begin"/> with its
/// numbers (<see cref="HeistData"/>) and its <see cref="IHeistGoal"/>, and gets the objective (<see cref="HeistRun"/>): the Cartel van on the road beside the Contact reserved for the party, a Drop point
/// far from the objective, the van's bay as the party's cargo volume, the Assaults of the Rival crew on the Raid vans' roads, and all of it removed at the end. The Harvest (<see cref="HarvestJobKind"/>)
/// is registered here; the Trap house heist registers its own kind on the same services.
/// </summary>
internal sealed class HeistServer
{
    readonly Func<JobVehicles> _vehicles;
    readonly RivalCrewServer _rivals;
    readonly Func<float, float, float> _ground;
    readonly Random _rng;

    /// <param name="vehicles">The Job vehicles (built after the Cartel; asked at a Job's start).</param>
    /// <param name="ground">The terrain height under (x, z) (the map's, or its mean height off it).</param>
    public HeistServer(ServerContext ctx, CartelFile data, CartelServer cartel, IJobParties parties, DuffelServer duffels, Func<JobVehicles> vehicles, RaidServer raids, RivalCrewServer rivals,
        Func<CartelMap> map, Func<ZonePolygons> polygons, Func<float, float, float> ground, Random rng)
    {
        Ctx = ctx; Cartel = cartel; Parties = parties; Duffels = duffels; _vehicles = vehicles; Raids = raids; _rivals = rivals; Map = map; Polygons = polygons; _ground = ground; _rng = rng;
        Harvest = new HarvestJobKind(this, data);
        cartel.Register(Harvest);
    }

    public ServerContext Ctx { get; }
    public CartelServer Cartel { get; }
    public IJobParties Parties { get; }
    public DuffelServer Duffels { get; }
    public RaidServer Raids { get; }
    public JobVehicles Vehicles => _vehicles();
    public Func<CartelMap> Map { get; }
    public Func<ZonePolygons> Polygons { get; }
    /// <summary>The Harvest heist: also the plantation's hold during one (<see cref="IPlantationFill"/>).</summary>
    public HarvestJobKind Harvest { get; }

    public float Ground(float x, float z) => _ground(x, z);

    /// <summary>The running Heist whose party the player is in now, or null.</summary>
    public HeistRun? RunOf(ushort player) => Cartel.Jobs.Involving(player, Parties)?.Objective as HeistRun;

    /// <summary>
    /// Begins a Heist for <paramref name="job"/>: a Drop point at least drop_min_m from the objective (reserved in the book), then the Cartel van on the road spot nearest the Contact, reserved for the
    /// party, and the counts for the party's size. Null with why not (no drop point left, no van type).
    /// </summary>
    public HeistRun? Begin(Job job, JobBook book, HeistData data, IHeistGoal goal, ContactPose contact, out string? refusal)
    {
        refusal = null;
        var members = Parties.MembersOf(job.Owner);
        if (JobDrops.Reserve(Map(), book, job, _rng, goal.Centre, data.DropMinM, data.DropApartM) is not { } drop)
        {
            refusal = $"No drop point is free {data.DropMinM:F0} m or more from {goal.Place}.";
            return null;
        }
        var (at, yaw) = VanSpot(contact, data.VanSpawnMinM);
        if (Vehicles.Spawn(data.VanType, at, yaw, members) is not { } van) { refusal = "The Cartel van cannot be put down."; return null; }
        var capacity = CarCargo.Capacity(van);
        var heist = new Heist(HeistRules.MinDuffels(data, job.Tier, members.Count, capacity), HeistRules.MaxDuffels(data, job.Tier, members.Count, capacity),
            HeistRules.GraceSeconds(data, job.Tier), HeistRules.IntervalSeconds(data, job.Tier));
        GD.Print($"server: {job}: Cartel van {van.Id} at ({at.X:F0},{at.Z:F0}), drop at ({drop.X:F0},{drop.Z:F0}), {heist.Min} to {heist.Max} duffels");
        return new HeistRun(this, job, data, goal, van, drop, heist, members);
    }

    /// <summary>Where the Cartel van stands: the road spot nearest the Contact at least <paramref name="minM"/> from him (the Raid vans' roads), facing along the road; without a road, beside him.</summary>
    (NVec3 At, float Yaw) VanSpot(ContactPose contact, float minM)
    {
        var c = contact.Position;
        if (RaidPlanner.Fallback(Raids.Roads, new NVec2(c.X, c.Z), minM, _rng, (_, _) => 1) is { } spot)
            return (new NVec3(spot.StopAt.X, Ground(spot.StopAt.X, spot.StopAt.Y), spot.StopAt.Y), spot.Yaw);
        var right = new NVec2(-contact.Facing.Y, contact.Facing.X);
        var p = new NVec2(c.X, c.Z) + right * minM;
        return (new NVec3(p.X, Ground(p.X, p.Y), p.Y), contact.YawDeg * MathF.PI / 180f);
    }

    /// <summary>An Assault is due for <paramref name="run"/>: a Rival crew van to the objective, hostile to the party, unless max_assaults_alive of the Heist's are still in the fight.</summary>
    public void LaunchAssault(HeistRun run)
    {
        var alive = run.Assaults.Count(r => !r.Over || r.Gang.AliveCount > 0);
        if (alive >= run.Data.MaxAssaultsAlive) { GD.Print($"server: {run.Job}: an Assault is due, {alive} still fight: none sent"); return; }
        var (gunmen, enforcers) = HeistRules.AssaultCrew(run.Data, run.Job.Tier, run.Party.Count);
        var party = run.Party;
        var centre = run.Goal.Centre;
        var crew = _rivals.NewCrew(centre, p => party.Contains(p));
        var used = run.Stops.Append(new NVec2(run.VanFeet.X, run.VanFeet.Z)).ToList(); // not on top of the Cartel van (a Raid van shoves cars aside)
        if (Raids.LaunchAssault(new NVec2(centre.X, centre.Z), used, HeistRules.CrewOrder(gunmen, enforcers), crew, centre) is not { } raid)
        {
            _rivals.Remove(crew);
            return;
        }
        run.AddAssault(raid);
        run.Tell($"A Rival crew van is coming to {run.Goal.Place}!");
    }
}

/// <summary>
/// One running Heist's objective on the server (the template, <see cref="Heist"/> for its phase and Assault clock): it keeps the party (the Duffels' party set and the van's riders), feeds the
/// facts of the Cartel van each tick, launches the Assaults when due, and is the cargo volume of the van's bay (only the party's Duffels, while the van takes more). It succeeds when the van stops
/// at the Drop point with the minimum loaded, fails when the van is destroyed, and its end removes the van, the party's Duffels and the Assaults.
/// </summary>
internal sealed class HeistRun : IJobObjective, ICargoVolume
{
    readonly HeistServer _s;
    readonly ServerCar _van;
    readonly NVec3 _drop;
    readonly HashSet<ushort> _party = new();
    readonly List<Raid> _assaults = new();
    readonly List<NVec2> _stops = new();
    Change<HeistPhase> _told;

    public HeistRun(HeistServer s, Job job, HeistData data, IHeistGoal goal, ServerCar van, NVec3 drop, Heist heist, IEnumerable<ushort> members)
    {
        _s = s; Job = job; Data = data; Goal = goal; _van = van; _drop = drop; Heist = heist;
        _party.UnionWith(members);
        _told.Update(heist.Phase);
        van.Exploded += OnExploded;
        s.Duffels.Book.AddVolume(this);
    }

    public Job Job { get; }
    public HeistData Data { get; }
    public IHeistGoal Goal { get; }
    public Heist Heist { get; }
    /// <summary>The party now: the set the Duffels of this Heist belong to (one set, kept in step with the owner's members).</summary>
    public IReadOnlySet<ushort> Party => _party;
    /// <summary>Duffels the party has filled so far (a kind's filling counts them up to the maximum).</summary>
    public int Filled { get; set; }
    /// <summary>The Duffels loaded in the van now.</summary>
    public int Loaded => _s.Duffels.Book.LoadedIn(this);
    public NVec3 VanFeet => CarPose.Present(_van).Feet;
    public IReadOnlyList<Raid> Assaults => _assaults;
    /// <summary>Where this Heist's Assault vans stopped (the next parks away from them).</summary>
    public IReadOnlyList<NVec2> Stops => _stops;

    public void AddAssault(Raid raid) { _assaults.Add(raid); _stops.Add(raid.Plan.StopAt); }

    /// <summary>The party follows the owner's members (a squad mate came or went): the Duffels' set and the van's riders.</summary>
    public void SyncParty()
    {
        var members = _s.Parties.MembersOf(Job.Owner);
        if (_party.SetEquals(members)) return;
        _party.Clear();
        _party.UnionWith(members);
        JobVehicles.SetRiders(_van, members);
    }

    public JobStep Step(Job job, float dt)
    {
        SyncParty();
        var feet = VanFeet;
        var atDrop = CarPose.Flat(feet, _drop) <= Data.DropRadiusM && MathF.Abs(_van.State.Speed) < Data.DropStopMps;
        var t = Heist.Step(new HeistFacts(_van.Destroyed, Goal.InArea(feet), atDrop, Loaded), dt);
        if (t.AssaultDue) _s.LaunchAssault(this);
        if (_told.Update(Heist.Phase)) TellPhase();
        return t.Step;
    }

    void TellPhase()
    {
        switch (Heist.Phase)
        {
            case HeistPhase.AtObjective: Tell(Heist.Assaults.Due == 0 && Heist.Assaults.State == AssaultClockState.Grace ? $"The van is at {Goal.Place}. Load at least {Heist.Min} duffels; the Rival crew is coming." : $"Back at {Goal.Place}: the Rival crew comes again."); break;
            case HeistPhase.ToDrop: Tell($"The van is away with {Heist.Loaded} duffels: drive to the drop point and stop there."); break;
        }
    }

    /// <summary>A chat line to every human of the party.</summary>
    public void Tell(string text)
    {
        foreach (var id in _party)
            if (_s.Ctx.Players.TryGetValue(id, out var c) && !c.IsBot) _s.Ctx.ChatTo(c, ChatLineKind.Info, text);
    }

    void OnExploded(ServerCar car, ushort attacker) =>
        GD.Print($"server: {Job}: the Cartel van {car.Id} was destroyed{(attacker != 0 ? $" by {_s.Ctx.NameOf(attacker)} #{attacker}" : "")}");

    public int BonusRespect => Heist.Phase == HeistPhase.Delivered ? HeistRules.Bonus(Data, Job.Tier, Heist.Loaded, Heist.Min) : 0;

    public string Objective(Job job) => Heist.Line(Goal.Place, Goal.FillLine);

    public System.Numerics.Vector3? Marker(Job job) => Heist.Marker switch
    {
        HeistMarker.Objective => Goal.Centre,
        HeistMarker.Van => CarCargo.Centre(_van) ?? VanFeet,
        HeistMarker.Drop => _drop,
        _ => null,
    };

    public void End(Job job)
    {
        _van.Exploded -= OnExploded;
        _s.Duffels.Book.RemoveVolume(this);
        var duffels = _s.Duffels.RemoveParty(_party);
        foreach (var raid in _assaults) _s.Raids.Remove(raid);
        _s.Vehicles.Remove(_van);
        Goal.End();
        GD.Print($"server: {job} over: {Heist.Phase}, {Heist.Loaded} loaded, {_assaults.Count} Assault(s); van, {duffels} duffel(s) and crews removed");
    }

    // ---- the van's bay as the party's cargo volume ----

    public bool Contains(System.Numerics.Vector3 point) => CarCargo.Contains(_van, point, Data.VanLoadMarginM);

    public bool Accepts(Duffel duffel) => ReferenceEquals(duffel.Party, _party) && !_van.Destroyed && CarCargo.Count(_van) < CarCargo.Capacity(_van);

    public void Load(Duffel duffel)
    {
        CarCargo.TryLoad(_van);
        var n = Loaded;
        Tell(n < Heist.Min ? $"Loaded: {n}/{Heist.Min}." : $"Loaded: {n} (the drop takes it from {Heist.Min}, the van up to {CarCargo.Capacity(_van)}).");
    }
}

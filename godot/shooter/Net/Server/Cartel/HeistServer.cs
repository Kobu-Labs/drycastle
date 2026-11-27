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
/// Contact's plantation; the Trap house heist (<see cref="TrapHouseJobKind"/>) plugs in its house the same way (<see cref="HeistServer.Begin"/>).
/// </summary>
internal interface IHeistGoal
{
    /// <summary>The objective named in the tracker line ("the plantation").</summary>
    string Place { get; }
    /// <summary>How the party fills a Duffel there, the start of the loading line ("Fill weed duffels in the plantation (hold {interact}) and throw them in the van"; the client names the bound key).</summary>
    string FillLine { get; }
    /// <summary>The objective's centre (Godot axes): the marker on the way there, where the Assaults park near and head for.</summary>
    NVec3 Centre { get; }
    /// <summary>The ground point stands in the objective's area (the van loads there; leaving it with the minimum stops the Assaults).</summary>
    bool InArea(NVec3 p);
    /// <summary>The tracker line now, given the template's (<paramref name="line"/>): the template's by default; a trap house adds its safe's Drill.</summary>
    string Line(Heist heist, string line) => line;
    /// <summary>The marker now, given the template's (<paramref name="marker"/>): the template's by default; a trap house points at its safe while the Drill runs.</summary>
    NVec3? Marker(Heist heist, NVec3? marker) => marker;
    /// <summary>The Job ended: what the kind put in the world goes (a trap house is stripped).</summary>
    void End() { }
}

/// <summary>
/// The Heist template on the server (GLOSSARY "Heist", "Cartel van", "Assault"; docs/cartel.md "Heists", "Cartel van", "Assaults"): a kind's <see cref="IJobKind.Begin"/> calls <see cref="Begin"/> with its
/// numbers (<see cref="HeistData"/>) and its <see cref="IHeistGoal"/>, and gets the objective (<see cref="HeistRun"/>): the Cartel van on the road beside the Contact reserved for the party, a Drop point
/// far from the objective, the van's bay as the party's cargo volume (<see cref="VanBay"/>), the Assaults (<see cref="HeistAssaults"/>) of the Rival crew on the Raid vans' roads, and all of it removed at the end. The Harvest (<see cref="HarvestJobKind"/>)
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
        var members = job.Party;
        if (CartelSpots.Claim(job, book, Map(), CartelZones.DropKind, _rng, goal.Centre, goal.Place, data.DropMinM, data.DropApartM, out refusal) is not { } drop) return null;
        var (at, yaw) = VanSpot(contact, data.VanSpawnMinM);
        if (Vehicles.Spawn(data.VanType, at, yaw, members) is not { } van) { refusal = "The Cartel van cannot be put down."; return null; }
        var capacity = CarCargo.Capacity(van);
        var heist = new Heist(HeistRules.MinDuffels(data, job.Tier, members.Count, capacity), HeistRules.MaxDuffels(data, job.Tier, members.Count, capacity),
            HeistRules.GraceSeconds(data, job.Tier), HeistRules.IntervalSeconds(data, job.Tier));
        GD.Print($"server: {job}: Cartel van {van.Id} at ({at.X:F0},{at.Z:F0}), drop at ({drop.X:F0},{drop.Z:F0}), {heist.Min} to {heist.Max} duffels");
        return new HeistRun(this, job, data, goal, van, drop, heist);
    }

    /// <summary>The Cartel van is not put down this close to another vehicle (a second squad's van standing on the road spot nearest the Contact).</summary>
    const float VanApartM = 6f;

    /// <summary>
    /// Where the Cartel van stands: the road spot nearest the Contact at least <paramref name="minM"/> from him (the Raid vans' roads) with no vehicle within <see cref="VanApartM"/>, facing along
    /// the road; without one, beside him.
    /// </summary>
    (NVec3 At, float Yaw) VanSpot(ContactPose contact, float minM)
    {
        var c = contact.Position;
        if (RaidPlanner.Fallback(Raids.Roads, new NVec2(c.X, c.Z), minM, _rng, (p, _) => Vehicles.Clear(p, VanApartM) ? 1 : null) is { } spot)
            return (new NVec3(spot.StopAt.X, Ground(spot.StopAt.X, spot.StopAt.Y), spot.StopAt.Y), spot.Yaw);
        var right = new NVec2(-contact.Facing.Y, contact.Facing.X);
        var p = new NVec2(c.X, c.Z) + right * minM;
        return (new NVec3(p.X, Ground(p.X, p.Y), p.Y), contact.YawDeg * MathF.PI / 180f);
    }

    /// <summary>An Assault is due for <paramref name="run"/>: a Rival crew van to the objective, hostile to the party, unless max_assaults_alive of the Heist's are still in the fight (<see cref="HeistAssaults.MaySend"/>).</summary>
    public void LaunchAssault(HeistRun run)
    {
        var assaults = run.Assaults;
        if (!assaults.MaySend) { GD.Print($"server: {run.Job}: an Assault is due, {assaults.InFightCount} still fight: none sent"); return; }
        var (gunmen, enforcers) = HeistRules.AssaultCrew(run.Data, run.Job.Tier, run.Party.Count);
        var party = run.Party;
        var centre = run.Goal.Centre;
        var crew = _rivals.NewCrew(centre, party.Contains);
        var used = assaults.Stops.Append(new NVec2(run.VanFeet.X, run.VanFeet.Z)).ToList(); // not on top of the Cartel van (a Raid van shoves cars aside)
        if (Raids.LaunchAssault(new NVec2(centre.X, centre.Z), used, CrewRules.Order(gunmen, enforcers), crew, centre) is not { } raid)
        {
            _rivals.Remove(crew);
            return;
        }
        assaults.Add(raid);
        run.Tell($"A Rival crew van is coming to {run.Goal.Place}!");
    }
}

/// <summary>
/// One running Heist's objective on the server (the template, <see cref="Heist"/> for its phase and Assault clock): the van's riders follow the Job's party (<see cref="Job.Party"/>), it feeds the
/// facts of the Cartel van each tick, launches the Assaults when due and takes out the spent ones (<see cref="HeistAssaults"/>), and owns the van's bay (<see cref="VanBay"/>). It succeeds when the
/// van stops at the Drop point with the minimum loaded, fails when the van is destroyed, and its end removes the van, the party's Duffels and the Assaults.
/// </summary>
internal sealed class HeistRun : IJobObjective
{
    readonly HeistServer _s;
    readonly ServerCar _van;
    readonly NVec3 _drop;
    readonly VanBay _bay;
    Change<HeistPhase> _told;
    /// <summary>The party's version the van's riders were last set from (the van was put down with the party at the start).</summary>
    Change<int> _riders;

    public HeistRun(HeistServer s, Job job, HeistData data, IHeistGoal goal, ServerCar van, NVec3 drop, Heist heist)
    {
        _s = s; Job = job; Data = data; Goal = goal; _van = van; _drop = drop; Heist = heist;
        _told.Update(heist.Phase);
        _riders.Update(job.Party.Version);
        Assaults = new HeistAssaults(data.MaxAssaultsAlive);
        _bay = new VanBay(this, van, data.VanLoadMarginM);
        van.Exploded += OnExploded;
        s.Duffels.Book.AddVolume(_bay);
    }

    public Job Job { get; }
    public HeistData Data { get; }
    public IHeistGoal Goal { get; }
    public Heist Heist { get; }
    /// <summary>The Job's party: the set the Duffels of this Heist belong to and its crews attack.</summary>
    public JobParty Party => Job.Party;
    /// <summary>Duffels the party has filled so far (a kind's filling counts them up to the maximum).</summary>
    public int Filled { get; set; }
    /// <summary>The Duffels loaded in the van now (the bay's count).</summary>
    public int Loaded => _bay.Loaded;
    public NVec3 VanFeet => CarPose.Present(_van).Feet;
    public HeistAssaults Assaults { get; }

    public JobStep Step(Job job, float dt)
    {
        if (_riders.Update(Party.Version)) JobVehicles.SetRiders(_van, Party); // a squad mate came or went: the van takes whom the party has now
        foreach (var spent in Assaults.TakeSpent()) _s.Raids.Remove(spent); // its crew is gone: the van frees its slot
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
        foreach (var id in Party)
            if (_s.Ctx.Players.TryGetValue(id, out var c) && !c.IsBot) _s.Ctx.ChatTo(c, ChatLineKind.Info, text);
    }

    /// <summary>The van blew up: the party hears why before the Job fails on its next step (the failure line alone says only that the objective was lost).</summary>
    void OnExploded(ServerCar car, ushort attacker)
    {
        var by = attacker != 0 ? $" by {_s.Ctx.NameOf(attacker)}" : "";
        Tell($"The Cartel van was destroyed{by}: the heist is lost.");
        GD.Print($"server: {Job}: the Cartel van {car.Id} was destroyed{(attacker != 0 ? $"{by} #{attacker}" : "")}");
    }

    public int BonusRespect => Heist.Phase == HeistPhase.Delivered ? HeistRules.Bonus(Data, Job.Tier, Heist.Loaded, Heist.Min) : 0;

    public string Objective(Job job) => Goal.Line(Heist, Heist.Line(Goal.Place, Goal.FillLine));

    public System.Numerics.Vector3? Marker(Job job) => Goal.Marker(Heist, Heist.Marker switch
    {
        HeistMarker.Objective => Goal.Centre,
        HeistMarker.Van => CarCargo.Centre(_van) ?? VanFeet,
        HeistMarker.Drop => _drop,
        _ => (NVec3?)null,
    });

    public void End(Job job)
    {
        _van.Exploded -= OnExploded;
        _s.Duffels.Book.RemoveVolume(_bay);
        var duffels = _s.Duffels.Book.RemoveParty(Party);
        foreach (var raid in Assaults.TakeAll()) _s.Raids.Remove(raid);
        _s.Vehicles.Remove(_van);
        Goal.End();
        GD.Print($"server: {job} over: {Heist.Phase}, {Heist.Loaded} loaded, {Assaults.Sent} Assault(s); van, {duffels} duffel(s) and crews removed");
    }
}

/// <summary>
/// The Cartel van's bay as the party's cargo volume (docs/cartel.md "Cartel van"): only the party's Duffels, while the van stands and the bay has room (vehicles.json cargo_volume capacity). It keeps
/// the one count of what went in, raised as each Duffel loads; the vehicle keeps none, so a van put back on the road after a fall out of the world still holds its load and takes no second one.
/// The party hears the milestones (the minimum, the maximum); the tracker line carries the count.
/// </summary>
internal sealed class VanBay : ICargoVolume
{
    readonly HeistRun _run;
    readonly ServerCar _van;
    readonly float _margin;
    readonly int _capacity;

    /// <param name="marginM">The bay takes a Duffel this much outside its volume too (van_load_margin_m: the closed rear doors).</param>
    public VanBay(HeistRun run, ServerCar van, float marginM)
    {
        _run = run; _van = van; _margin = marginM;
        _capacity = CarCargo.Capacity(van);
    }

    /// <summary>The Duffels loaded so far.</summary>
    public int Loaded { get; private set; }

    public bool Contains(System.Numerics.Vector3 point) => CarCargo.Contains(_van, point, _margin);

    public bool Accepts(Duffel duffel) => ReferenceEquals(duffel.Party, _run.Party) && !_van.Destroyed && Loaded < _capacity;

    public void Load(Duffel duffel)
    {
        Loaded++;
        var h = _run.Heist;
        if (Loaded == h.Max) _run.Tell($"All {Loaded} duffels are loaded: drive the van to the drop point and stop there.");
        else if (Loaded == h.Min) _run.Tell($"{Loaded} loaded, enough for the drop: drive the van there, or load up to {h.Max} for more Respect.");
    }
}

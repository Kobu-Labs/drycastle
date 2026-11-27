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
using Drycastle.ShooterCore.Vehicles;
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
/// numbers (<see cref="HeistData"/>) and its <see cref="IHeistGoal"/>, and gets the objective (<see cref="HeistRun"/>): the Cartel van on the road beside the Contact reserved for the party, a drop
/// far from the objective that is a Receiver (<see cref="ReceiverServer"/>: a stash house, a helicopter or a boat), the van's bay as the party's cargo volume (<see cref="VehicleCargo"/>, restricted to the party), the Assaults (<see cref="HeistAssaults"/>) of the Rival crew on the Raid vans' roads, at the objective and at the drop, and all of it removed at the end. The Harvest (<see cref="HarvestJobKind"/>)
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
    public HeistServer(ServerContext ctx, CartelFile data, CartelServer cartel, IJobParties parties, DuffelServer duffels, CargoServer cargo, ReceiverServer receivers, Func<JobVehicles> vehicles, RaidServer raids, RivalCrewServer rivals,
        Func<CartelMap> map, Func<ZonePolygons> polygons, Func<float, float, float> ground, Random rng)
    {
        Ctx = ctx; Cartel = cartel; Parties = parties; Duffels = duffels; Cargo = cargo; Receivers = receivers; _vehicles = vehicles; Raids = raids; _rivals = rivals; Map = map; Polygons = polygons; _ground = ground; _rng = rng;
        receivers.RunOf = RunOf;
        Harvest = new HarvestJobKind(this, data);
        cartel.Register(Harvest);
    }

    public ServerContext Ctx { get; }
    public CartelServer Cartel { get; }
    public IJobParties Parties { get; }
    public DuffelServer Duffels { get; }
    /// <summary>The cargo vehicles: the Cartel van's bay is one of them.</summary>
    public CargoServer Cargo { get; }
    /// <summary>The Receivers: where a Heist's drop is.</summary>
    public ReceiverServer Receivers { get; }
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
    /// Begins a Heist for <paramref name="job"/>: a drop (a Receiver's place: a hollow house, a landing spot or a mooring) at least drop_min_m from the objective (reserved in the book), then the Cartel van on the road
    /// spot nearest the Contact, reserved for the party, the counts for the party's size and the Receiver put down for it. Null with why not (no drop left, no van type).
    /// </summary>
    public HeistRun? Begin(Job job, JobBook book, HeistData data, IHeistGoal goal, ContactPose contact, out string? refusal)
    {
        refusal = null;
        var members = job.Party;
        if (Receivers.Plan(job, book, goal.Centre, goal.Place, data, out refusal) is not { } plan) return null;
        var (at, yaw) = VanSpot(contact, data.VanSpawnMinM);
        if (Vehicles.Spawn(data.VanType, at, yaw, members) is not { } van) { refusal = "The Cartel van cannot be put down."; return null; }
        if (Cargo.BayOf(van) == null) { Vehicles.Remove(van); refusal = "The Cartel van has no cargo volume."; return null; }
        var capacity = CarCargo.Capacity(van);
        var heist = new Heist(HeistRules.MinDuffels(data, job.Tier, members.Count, capacity), HeistRules.MaxDuffels(data, job.Tier, members.Count, capacity),
            HeistRules.GraceSeconds(data, job.Tier), HeistRules.IntervalSeconds(data, job.Tier), data.DropGraceSeconds) { Drop = ReceiverRules.Text(plan.Kind) };
        if (Receivers.Put(plan, members, heist.Min, Math.Min(heist.Max, VehicleCargoVolumeDef.MaxCapacity)) is not { } receiver) { Vehicles.Remove(van); refusal = "The drop cannot be put down."; return null; }
        GD.Print($"server: {job}: Cartel van {van.Id} at ({at.X:F0},{at.Z:F0}), drop: {ReceiverRules.NameOf(plan.Kind)} at ({plan.At.X:F0},{plan.At.Z:F0}), {heist.Min} to {heist.Max} duffels");
        return new HeistRun(this, job, data, goal, van, receiver, heist);
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

    /// <summary>
    /// An Assault is due for <paramref name="run"/> (its clock, or an admin's <c>/cartel assault</c>): a Rival crew van to the objective, hostile to the party, unless max_assaults_alive of the
    /// Heist's are still in the fight (<see cref="HeistAssaults.MaySend"/>). Null when it was sent; else why not.
    /// </summary>
    public string? LaunchAssault(HeistRun run)
    {
        var assaults = run.Assaults;
        if (!assaults.MaySend)
        {
            GD.Print($"server: {run.Job}: an Assault is due, {assaults.InFightCount} still fight: none sent");
            return $"No Assault sent: {assaults.InFightCount} of this heist's are still in the fight (max_assaults_alive {run.Data.MaxAssaultsAlive}).";
        }
        var (gunmen, enforcers) = HeistRules.AssaultCrew(run.Data, run.Job.Tier, run.Party.Count);
        var party = run.Party;
        var centre = run.AssaultTarget;
        var crew = _rivals.NewCrew(centre, party.Contains);
        var used = assaults.Stops.Append(new NVec2(run.VanFeet.X, run.VanFeet.Z)).ToList(); // not on top of the Cartel van (a Raid van shoves cars aside)
        if (Raids.LaunchAssault(new NVec2(centre.X, centre.Z), used, CrewRules.Order(gunmen, enforcers), crew, centre) is not { } raid)
        {
            _rivals.Remove(crew);
            return "No Assault sent: no Rival crew van could be put on the road now.";
        }
        assaults.Add(raid);
        run.Tell($"A Rival crew van is coming to {run.AssaultPlace}!");
        return null;
    }
}

/// <summary>
/// One running Heist's objective on the server (the template, <see cref="Heist"/> for its phase and Assault clock): the van's riders follow the Job's party (<see cref="Job.Party"/>), it feeds the
/// facts of the Cartel van and the drop's <see cref="Receiver"/> each tick, launches the Assaults when due (at the objective, and at the drop once the van is there) and takes out the spent ones
/// (<see cref="HeistAssaults"/>), and counts the van's bay (<see cref="VehicleCargo"/>). It succeeds the moment the Receiver is sent off with the minimum on it, fails when the van is destroyed, and its end removes
/// the van, the party's Duffels (but those on a Receiver that is still leaving) and the Assaults.
/// <para>
/// The seam for the Rival chase cars (a later wave): <see cref="LeftObjective"/> is raised when the van left the objective's area with the minimum loaded (the drive to the drop begins), <see cref="ReachedDrop"/>
/// when the van came within the drop's area (the drive is over); <see cref="VanFeet"/>, <see cref="Heist"/> and <see cref="Receiver"/> say where everything is.
/// </para>
/// </summary>
internal sealed class HeistRun : IJobObjective
{
    readonly HeistServer _s;
    readonly ServerCar _van;
    readonly VehicleCargo _bay;
    /// <summary>The party's version the van's riders were last set from (the van was put down with the party at the start).</summary>
    Change<int> _riders;

    public HeistRun(HeistServer s, Job job, HeistData data, IHeistGoal goal, ServerCar van, Receiver receiver, Heist heist)
    {
        _s = s; Job = job; Data = data; Goal = goal; _van = van; Receiver = receiver; Heist = heist;
        _riders.Update(job.Party.Version);
        Assaults = new HeistAssaults(data.MaxAssaultsAlive);
        _bay = s.Cargo.BayOf(van)!; // the van's bay is a cargo volume like every vehicle's; this Job's van takes only its party's Duffels, and counts them
        _bay.Restrict(job.Party);
        _bay.MarginM = data.VanLoadMarginM;
        _bay.DuffelLoaded += OnLoaded;
        receiver.DuffelLoaded += OnReceived;
        heist.PhaseChanged += OnPhase;
        van.Exploded += OnExploded;
    }

    public Job Job { get; }
    public HeistData Data { get; }
    public IHeistGoal Goal { get; }
    public Heist Heist { get; }
    /// <summary>The drop: a stash house's pallet, a helicopter or a boat. The party unloads the van into it.</summary>
    public Receiver Receiver { get; }
    /// <summary>The Job's party: the set the Duffels of this Heist belong to and its crews attack.</summary>
    public JobParty Party => Job.Party;
    /// <summary>Duffels the party has filled so far (a kind's filling counts them up to the maximum).</summary>
    public int Filled { get; set; }
    /// <summary>The Duffels loaded in the van now (the bay's count).</summary>
    public int Loaded => _bay.Loaded;
    public NVec3 VanFeet => CarPose.Present(_van).Feet;
    public HeistAssaults Assaults { get; }

    /// <summary>The van left the objective's area with at least the minimum loaded: the drive to the drop begins (the Assaults stop). A later wave's chase cars start here.</summary>
    public event Action<HeistRun>? LeftObjective;
    /// <summary>The van came within the drop's area: the drive is over, the party unloads (the Assaults come to the drop). A later wave's chase cars stop coming here.</summary>
    public event Action<HeistRun>? ReachedDrop;

    /// <summary>Where an Assault goes now: the van at the drop once it is there, else the objective's centre.</summary>
    public NVec3 AssaultTarget => Heist.Phase == HeistPhase.AtDrop ? VanFeet : Goal.Centre;
    /// <summary>What the chat calls where the Assault goes.</summary>
    public string AssaultPlace => Heist.Phase == HeistPhase.AtDrop ? Heist.Drop.Place : Goal.Place;

    public JobStep Step(Job job, float dt)
    {
        if (_riders.Update(Party.Version)) JobVehicles.SetRiders(_van, Party); // a squad mate came or went: the van takes whom the party has now
        foreach (var spent in Assaults.TakeSpent()) _s.Raids.Remove(spent); // its crew is gone: the van frees its slot
        var feet = VanFeet;
        var r = Receiver;
        // every Duffel of the Job on board a helicopter or a boat sends it off by itself; a stash house waits for its buyer's E
        if (r.Kind != ReceiverKind.StashHouse && r.CanSend && Heist.Phase is HeistPhase.ToDrop or HeistPhase.AtDrop && _s.Duffels.Book.PartyCount(Party) == r.Loaded && r.Send())
        {
            Tell($"All {r.SentWith} duffels are aboard: the {ReceiverRules.Man(r.Kind)} takes off.");
            GD.Print($"server: {Job}: every duffel is on the {ReceiverRules.NameOf(r.Kind)}, it leaves with {r.SentWith}");
        }
        var inDrop = CarPose.Flat(feet, r.Position) <= r.Data.AreaM;
        var t = Heist.Step(new HeistFacts(_van.Destroyed, Goal.InArea(feet), inDrop, Loaded, r.Sent ? r.SentWith : r.Loaded, r.Sent), dt);
        if (t.AssaultDue) _s.LaunchAssault(this);
        return t.Step;
    }

    /// <summary>The phase changed: the party is told, and the seam's events go out.</summary>
    void OnPhase(HeistPhase from, HeistPhase to)
    {
        switch (to)
        {
            case HeistPhase.AtObjective: Tell(from == HeistPhase.ToObjective ? $"The van is at {Goal.Place}. Load at least {Heist.Min} duffels; the Rival crew is coming." : $"Back at {Goal.Place}: the Rival crew comes again."); break;
            case HeistPhase.ToDrop when from == HeistPhase.AtObjective: Tell($"The van is away with {Loaded} duffels: drive to {Heist.Drop.Place}."); LeftObjective?.Invoke(this); break;
            case HeistPhase.AtDrop: Tell($"The van is at {Heist.Drop.Place}: carry or throw the duffels into it, at least {Heist.Min}. The Rival crew will come here too."); ReachedDrop?.Invoke(this); break;
        }
    }

    /// <summary>A chat line to every human of the party.</summary>
    public void Tell(string text)
    {
        foreach (var id in Party)
            if (_s.Ctx.Players.TryGetValue(id, out var c) && !c.IsBot) _s.Ctx.ChatTo(c, ChatLineKind.Info, text);
    }

    /// <summary>A Duffel took a slot in the van's bay: the party hears the milestones (the minimum, the maximum); the tracker line carries the count.</summary>
    void OnLoaded(Duffel duffel)
    {
        var loaded = _bay.Loaded; // counted by the bay itself (the one count): a Duffel taken out lowers it, a reset of the van empties it
        if (loaded == Heist.Max) Tell($"All {loaded} duffels are loaded: drive the van to {Heist.Drop.Place}.");
        else if (loaded == Heist.Min) Tell($"{loaded} loaded, enough for the drop: drive the van to {Heist.Drop.Place}, or load up to {Heist.Max} for more Respect.");
    }

    /// <summary>A Duffel took a slot on the Receiver: the party hears the minimum and the maximum.</summary>
    void OnReceived(Duffel duffel)
    {
        var on = Receiver.Loaded;
        var man = ReceiverRules.Man(Receiver.Kind);
        if (on == Receiver.Capacity) Tell($"{on} duffels are on board: that is all it takes. The leader presses the interact key at the {man} to finish.");
        else if (on == Heist.Min) Tell($"{on} on board, enough: the leader presses the interact key at the {man} to finish, or unload up to {Heist.Max} for more Respect.");
    }

    /// <summary>The van blew up: the party hears why before the Job fails on its next step (the failure line alone says only that the objective was lost).</summary>
    void OnExploded(ServerCar car, ushort attacker)
    {
        var by = attacker != 0 ? $" by {_s.Ctx.NameOf(attacker)}" : "";
        Tell($"The Cartel van was destroyed{by}: the heist is lost.");
        GD.Print($"server: {Job}: the Cartel van {car.Id} was destroyed{(attacker != 0 ? $"{by} #{attacker}" : "")}");
    }

    /// <summary>An admin's <c>/cartel assault</c>: an Assault now, by the clock's own path and cap (the clock goes on as it was). Null when it was sent; else why not.</summary>
    public string? SendAssault() => _s.LaunchAssault(this);

    public int BonusRespect => Heist.Phase == HeistPhase.Delivered ? HeistRules.Bonus(Data, Job.Tier, Heist.Received, Heist.Min) : 0;

    public string Objective(Job job) => Goal.Line(Heist, Heist.Line(Goal.Place, Goal.FillLine));

    public System.Numerics.Vector3? Marker(Job job) => Goal.Marker(Heist, Heist.Marker switch
    {
        HeistMarker.Objective => Goal.Centre,
        HeistMarker.Van => CarCargo.Centre(_van) ?? VanFeet,
        HeistMarker.Drop => Receiver.Position,
        _ => (NVec3?)null,
    });

    public void End(Job job)
    {
        _van.Exploded -= OnExploded;
        _bay.DuffelLoaded -= OnLoaded;
        _bay.Restrict(null);
        Receiver.DuffelLoaded -= OnReceived;
        Heist.PhaseChanged -= OnPhase;
        _s.Receivers.Release(Receiver); // a helicopter or boat that is leaving finishes its way, with the Duffels on it
        var duffels = _s.Duffels.Book.RemoveParty(Party, Receiver.Leaving ? Receiver : null);
        foreach (var raid in Assaults.TakeAll()) _s.Raids.Remove(raid);
        _s.Vehicles.Remove(_van);
        Goal.End();
        GD.Print($"server: {job} over: {Heist.Phase}, {Heist.Received} received, {Assaults.Sent} Assault(s); van, {duffels} duffel(s) and crews removed");
    }
}

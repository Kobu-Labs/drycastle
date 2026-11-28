using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>
/// What kind of Rival vehicle a <see cref="ChaseUnit"/> is: a chase car (<see cref="ChaseFleet"/>) or an Assault van (<see cref="AssaultFleet"/>). <see cref="Drive"/> are the numbers it drives, stops, remounts and gives up on
/// (the chase's own, the Assault's with its vehicle, skin and give-up distance: <see cref="AssaultCarData.Drive"/>); <see cref="Crew"/> the kinds that sit in it, in slot order: the driver first, then the car's other seats, then
/// (an Assault van) the posts of <see cref="Bay"/>. <see cref="Post"/> says where the crew on foot heads for, given the Cartel van's feet (null: the Cartel van; an Assault van's: the objective while the Heist is at it).
/// </summary>
internal sealed record RivalProfile(string Label, ChaseData Drive, IReadOnlyList<GangMemberKind> Crew, AssaultCarData? Bay = null, Func<NVec3, NVec3>? Post = null);

/// <summary>
/// One Rival vehicle on the server, a chase car or an Assault van (GLOSSARY "Chase car", "Assault"; docs/cartel.md "Chase cars", "Assaults"): a real vehicle of vehicles.json (a <see cref="ServerCar"/> on the drive model, in a dark skin) that an NPC drives, and its
/// crew of Rival Gang members. The two differ only in their <see cref="RivalProfile"/>: the vehicle, the crew and where it sits (an Assault van's riders after the cab's two seats sit in the cargo bay, drawn seated, never shooting;
/// they step out behind the van), where it is put down and when it gives up. The vehicle is driven by inputs this class sets (<see cref="ServerCar.NpcInput"/>: <see cref="ChasePilot"/> over the way <see cref="OffRoadRoute"/> found, roads where they bring it closer and
/// open ground where that is shorter; the vehicle step does the rest), so it is shot, bumped and destroyed like any car. The crew rides it as <see cref="GangMember"/>s posted on its seats (the post moves with the car:
/// <see cref="PlaceRiders"/>): the driver a Rival Enforcer on seat 0, the Gunmen on the others, drawn seated. While they ride, their sight and rounds start inside the hull and skip it (<see cref="RivalCrewServer.Mount"/>); they
/// shoot only while the car is within shoot_range_m of the van, with a worse aim while it moves. The machine (<see cref="ChaseCar"/>) decides when the car stops, backs out of a wedge, when the crew gets out, when it walks
/// back and gets in again (the van pulling away) and when it gives up.
/// </summary>
internal sealed class ChaseUnit
{
    /// <summary>A member's step-out point snapped to the navigation mesh is used when it moved no more than this.</summary>
    const float SnapM = 3f;

    readonly ChaseServer _s;
    readonly HeistRun _run;
    readonly RivalProfile _p;
    readonly ChaseData _d;
    readonly ChaseCar _machine;
    readonly List<(GangMember Member, int Seat)> _seats = new();   // the crew's seats, as it was seated when it spawned
    readonly List<(GangMember Member, int Seat)> _riders = new();  // the ones who sit in the car now
    GangMember? _driver;
    ServerCar? _car;
    ThiefGang? _crew;
    RoadPath _path = RoadPath.Empty;
    float _noWay;      // seconds the way has been missing (the old one is kept a little)
    Countdown _repath;
    readonly ChaseCleanup _cleanup;

    ChaseUnit(ChaseServer s, HeistRun run, RivalProfile p, ServerCar car, NVec3 van, bool ambush)
    {
        _s = s; _run = run; _p = p; _d = p.Drive; _car = car; _machine = new ChaseCar(_d, ambush); _cleanup = new ChaseCleanup(_d);
        if (p.Bay != null) Stall = new AssaultStall(p.Bay);
        _crew = s.Rivals.NewCrew(van, id => _machine.Shoots && run.Party.Contains(id)); // it attacks the Job's party, only while the machine lets it shoot
        _crew.Mounted += OnMounted;
        car.Exploded += OnExploded;
        _machine.Changed += (from, to) => GD.Print($"server: {_run.Job}: {p.Label} {car.Id}: {from} -> {to}");
    }

    /// <summary>Puts a vehicle down on <paramref name="spot"/> with its crew seated (the driver, then the other seats, then the bay's posts), reserved for nobody: no player may board while the crew rides. Null when the world has no room.</summary>
    public static ChaseUnit? Spawn(ChaseServer s, HeistRun run, RivalProfile p, ChaseSpot spot)
    {
        var d = p.Drive;
        var at = new NVec3(spot.Ground.X, s.Ground(spot.Ground.X, spot.Ground.Y), spot.Ground.Y);
        if (s.Vehicles.Spawn(d.VehicleType, at, spot.Yaw, Array.Empty<ushort>(), (byte)Math.Clamp(d.Skin, 0, 255)) is not { } car) return null;
        var unit = new ChaseUnit(s, run, p, car, run.VanFeet, spot.BackM < 0f); // a spot ahead of the van is an ambush: it waits for the van
        var capacity = car.Def.Seats.Count + (p.Bay?.BayPosts.Length ?? 0);
        var n = Math.Min(p.Crew.Count, capacity);
        if (n < p.Crew.Count) GD.PrintErr($"server: {run.Job}: {p.Label} {car.Id}: {p.Crew.Count} in the crew, room for {capacity}: {p.Crew.Count - n} left out");
        var ok = n > 0;
        for (var slot = 0; ok && slot < n; slot++) ok = unit.Seat(p.Crew[slot], slot) != null;
        if (!ok) { unit.Remove(); return null; }
        s.Rivals.Mount(car);
        run.SealBay(car); // a Rival vehicle's cargo bay takes nobody's Duffels
        var inBay = Math.Max(0, n - car.Def.Seats.Count);
        GD.Print($"server: {run.Job}: {p.Label} {car.Id} ({car.Def.Id} skin {d.Skin}) at ({at.X:F0},{at.Z:F0}) on {(spot.Place != null ? "the road" : "open ground")}, crew of {n}{(inBay > 0 ? $" ({inBay} in the bay)" : "")}");
        return unit;
    }

    /// <summary>One member of the crew in a slot of the car (a seat, or a post of the bay: posted there, sitting). Null when the world has no room for it.</summary>
    GangMember? Seat(GangMemberKind kind, int slot)
    {
        var m = _crew!.AddPosted(kind, SlotFeet(_car!, slot), _car!.State.Yaw, seat: true, shut: InBay(_car, slot));
        if (m == null) return null;
        _seats.Add((m, slot));
        _riders.Add((m, slot));
        if (slot == 0) _driver = m;
        return m;
    }

    /// <summary>The slot is a post of the cargo bay (past the car's seats): its rider shoots from no window.</summary>
    static bool InBay(ServerCar car, int slot) => slot >= car.Def.Seats.Count;

    /// <summary>The bay's post of a slot past the car's seats, in the car's frame.</summary>
    float[] BayPost(ServerCar car, int slot) => _p.Bay!.BayPosts[slot - car.Def.Seats.Count];

    /// <summary>Where a rider's feet are: a seat's point lowered seat_drop_m (the seated figure's hips are then on it), or a bay post (a point of the floor: it sits there, the hips seat_pelvis_m above).</summary>
    NVec3 SlotFeet(ServerCar car, int slot)
    {
        if (InBay(car, slot))
        {
            var b = CarPose.PointAt(car, BayPost(car, slot));
            return new NVec3(b.X, b.Y, b.Z);
        }
        var p = CarPose.SeatPos(car, slot);
        return new NVec3(p.X, p.Y - _d.SeatDropM, p.Z);
    }

    // ---- what the Heist asks ----

    /// <summary>The car counts towards max_alive (an Assault van: unless it is stalled, <see cref="Counts"/>).</summary>
    public bool InFight => _machine.InFight(_crew?.AliveCount ?? 0) && Counts;

    /// <summary>The Assault's progress watch (null for a chase car).</summary>
    public AssaultStall? Stall { get; }

    /// <summary>It counts towards the cap: every unit but a stalled Assault.</summary>
    public bool Counts => Stall?.Counts ?? true;

    /// <summary>The crew sits in the vehicle (not out on foot) and the vehicle stands in the world.</summary>
    public bool Riding => _car != null && _machine.CrewInside;

    /// <summary>One tick of the stall watch (an Assault van only): see <see cref="AssaultStall.Step"/>. True once, the tick it becomes stalled.</summary>
    public bool StepStall(bool newest, NVec3 van, float dt)
    {
        if (Stall is not { } stall || _car is not { } car) return false;
        var gap = NVec2.Distance(new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), new NVec2(van.X, van.Z));
        var stalled = stall.Step(Riding, newest, gap, dt);
        if (stalled) GD.Print($"server: {_run.Job}: {_p.Label} {car.Id} is stalled ({gap:F0} m from the van, no {_p.Bay!.StallProgressM:F0} m closer in {_p.Bay!.StallSeconds:F0} s)");
        return stalled;
    }

    /// <summary>Nothing of it is left in the world (car and crew removed): the Heist forgets it.</summary>
    public bool Finished => _car == null && _crew == null;

    /// <summary>Where the car stands (x, z), or null once it is removed.</summary>
    public NVec2? Where => _car is { } c ? new NVec2(c.GlobalPosition.X, c.GlobalPosition.Z) : null;

    bool DriverAlive => _driver?.Alive ?? false;

    // ---- the tick ----

    /// <summary>One tick of the Heist (before the vehicles step): the machine, then what its state asks of the car and the crew.</summary>
    public void Tick(float dt, NVec3 van, float vanSpeed)
    {
        if (_cleanup.WreckOver(_car is { Destroyed: true }, dt)) RemoveCar(); // the wreck lies its time, then goes (before the type's return time brings it back)
        var car = _car;
        var van2 = new NVec2(van.X, van.Z);
        var pos = car != null ? new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z) : van2;
        var speed = car?.State.Speed ?? 0f;
        _repath.Tick(dt);
        if (car != null && _repath.Ready && _machine.State is ChaseState.Chasing or ChaseState.Reversing or ChaseState.Spawned or ChaseState.Waiting) { Replan(car, pos, van2); _repath.Start(_d.RepathSeconds); }
        var crewAlive = _crew?.AliveCount ?? 0;
        var vanDir = _run.VanDir;
        var meeting = car != null && !car.Destroyed;
        var facts = new ChaseFacts(car != null ? NVec2.Distance(pos, van2) : float.PositiveInfinity, vanSpeed, MathF.Abs(speed), DriverAlive, car == null || car.Destroyed, !_path.IsEmpty,
            CrewGap(van2), crewAlive, car is { Destroyed: false } && !car.Seat.Occupied, _machine.Remounting && AllAboard(crewAlive),
            Passed: meeting && ChaseRules.Passed(pos, van2, vanDir), HeadOn: meeting && ChaseRules.HeadOn(_d, pos, car!.State.Yaw, van2, vanDir, vanSpeed));
        var step = _machine.Step(facts, dt);
        if (step.Remove) { Apply(ChaseCleanup.Due(ChaseState.GivingUp, Left())); _machine.End(); return; }
        if (car != null)
        {
            if (step.GetOut) StepOut(car, van);
            if (step.Remount) StartRemount(car);
            if (step.AbortRemount) AbortRemount(car, van);
            if (step.Mounted) { _s.Rivals.Mount(car); }
        }
        switch (_machine.State)
        {
            case ChaseState.Chasing when car != null:
                var want = ChaseRules.FollowSpeed(_d, facts.GapM, vanSpeed);
                car.NpcInput = ChasePilot.Follow(_path, pos, car.State.Yaw, speed, DriveModel.TurnRadiusAt(car.Def, MathF.Abs(speed)), want);
                break;
            case ChaseState.Waiting when car != null:
                car.NpcInput = ChasePilot.Wait(speed); // aside, and standing while the van drives up and past
                break;
            case ChaseState.Reversing when car != null:
                car.NpcInput = ChasePilot.Reverse(_path, pos, car.State.Yaw, van2);
                break;
            case ChaseState.Stopping when car != null:
                car.NpcInput = DriverAlive ? ChasePilot.Stop(speed) : null; // a dead driver: it rolls to a stop
                break;
            case ChaseState.Remounting when car != null:
                car.NpcInput = ChasePilot.Stop(speed); // it stands while the crew walks back
                break;
            case ChaseState.Out:
                TickOut(van);
                return;
            case ChaseState.Gone:
                Wind();
                return;
        }
        if (_machine.CrewInside && car != null) TickRiders(speed);
        else if (car == null && _machine.State != ChaseState.Out) Wind();
    }

    /// <summary>The crew rides: the Gunmen's aim by the car's speed, and the crew holds fire while the car is farther than shoot_range_m from the van.</summary>
    void TickRiders(float speed)
    {
        var error = ChaseRules.AimErrorDeg(_d, speed);
        foreach (var (m, _) in _riders) m.AimErrorDeg = error;
        if (!_machine.Shoots) _crew?.Calm();
    }

    /// <summary>The crew is out: it heads for the van's place and fights on foot; once nothing of it is left the car is Gone.</summary>
    void TickOut(NVec3 van)
    {
        if (_crew is not { } crew) { _machine.End(); return; }
        crew.Post = FootPost(van);
        if (crew.Count == 0) _machine.End();
    }

    /// <summary>Where the crew on foot heads for: the profile's post (an Assault crew: the objective while the Heist is at it), else the Cartel van.</summary>
    NVec3 FootPost(NVec3 van) => _p.Post?.Invoke(van) ?? van;

    /// <summary>Distance from the nearest living member of the crew to the van (infinite when none lives).</summary>
    float CrewGap(NVec2 van)
    {
        var best = float.PositiveInfinity;
        if (_crew == null) return best;
        foreach (var m in _crew.Members)
            if (m.Alive) best = MathF.Min(best, NVec2.Distance(new NVec2(m.Position.X, m.Position.Z), van));
        return best;
    }

    /// <summary>Every living member of the crew sits in the car again.</summary>
    bool AllAboard(int crewAlive)
    {
        var seated = 0;
        foreach (var (m, _) in _riders) if (m.Alive) seated++;
        return crewAlive > 0 && seated >= crewAlive;
    }

    /// <summary>Gone: the car is taken out unless a player sits in it (they keep it until they leave or the Job ends), and the crew's bodies are given their time.</summary>
    void Wind() => Apply(ChaseCleanup.Due(_machine.State, Left()));

    /// <summary>What is left of the car in the world, for <see cref="ChaseCleanup"/>.</summary>
    ChaseLeft Left() => new(_car != null, _car?.Destroyed ?? false, _car?.Seat.Occupied ?? false, _crew != null, _crew?.Count ?? 0);

    /// <summary>Takes out what the clean-up says is due.</summary>
    void Apply(ChaseDue due)
    {
        if (due.Car) RemoveCar();
        if (due.Crew) RemoveCrew();
    }

    // ---- the way ----

    /// <summary>
    /// The way to the van, again (every repath_seconds): <see cref="OffRoadRoute"/> over the roads where they bring the car closer and over open ground where that is shorter (a line is open when no building, trunk,
    /// fence, water or steep slope is on it). When none is found the old way is kept for a few seconds, then there is none (the car is stuck: the machine lets it back out, then stop).
    /// </summary>
    void Replan(ServerCar car, NVec2 from, NVec2 van)
    {
        var half = car.HalfWidth;
        var plan = OffRoadRoute.Plan(_s.Roads, from, van, (a, b) => _s.Open.Line(a, b, half), _s.Open.Numbers);
        if (plan != null) { _path = plan.Path; _noWay = 0; return; }
        _noWay += _d.RepathSeconds;
        if (_noWay > 4f) _path = RoadPath.Empty;
    }

    // ---- the crew ----

    /// <summary>
    /// After the vehicles moved: the riders sit on their seats where the car is now. The car's pose, yaw included, is theirs: a posted member turns back to it when it has no target.
    /// </summary>
    public void PlaceRiders()
    {
        if (_car is not { } car || !(_machine.CrewInside || _machine.Remounting)) return;
        foreach (var (m, slot) in _riders) m.MovePost(SlotFeet(car, slot), car.State.Yaw);
    }

    /// <summary>
    /// Where a member of slot <paramref name="slot"/> steps out, and walks back to: beside the door on its seat's side (snapped to the navigation mesh); a bay rider behind the van, out of the rear doors
    /// (the post's own x, <see cref="AssaultCarData.BayExitZ"/> along the frame, on the ground).
    /// </summary>
    NVec3 ExitSpot(ServerCar car, int slot)
    {
        NVec3 raw;
        if (InBay(car, slot))
        {
            var post = BayPost(car, slot);
            var b = CarPose.PointAt(car, new[] { post[0], 0f, _p.Bay!.BayExitZ });
            raw = new NVec3(b.X, car.GlobalPosition.Y, b.Z);
        }
        else
        {
            var yaw = car.State.Yaw;
            var right = new NVec3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
            var off = car.Def.Seats[slot].Offset;
            var side = off[0] < 0 ? -1f : 1f;
            var p = CarPose.SeatPos(car, slot);
            raw = new NVec3(p.X, car.GlobalPosition.Y, p.Z) + right * (side * (car.HalfWidth - MathF.Abs(off[0]) + _d.ExitSideM));
        }
        var snapped = _s.Rivals.Snap(raw);
        return NVec3.Distance(snapped, raw) <= SnapM ? snapped : raw;
    }

    /// <summary>
    /// The car stands: the crew steps out through the door on its seat's side (snapped to the navigation mesh), is no longer posted and heads for the van and fights on foot as a Rival crew; the car is free to take
    /// (a player may drive it away, also with a dead driver: nobody is reserved any more).
    /// </summary>
    void StepOut(ServerCar car, NVec3 van)
    {
        _s.Rivals.Unmount(car);
        car.NpcInput = null;
        JobVehicles.SetRiders(car, null);
        SitOut(car);
        if (_crew != null) _crew.Post = FootPost(van);
    }

    /// <summary>The ones who sit in the car step out (standing beside it, no longer posted).</summary>
    void SitOut(ServerCar car)
    {
        var yaw = car.State.Yaw;
        foreach (var (m, seat) in _riders)
        {
            if (!m.Alive) continue;
            m.AimErrorDeg = null;
            m.Unpost(ExitSpot(car, seat), yaw);
        }
        _riders.Clear();
    }

    /// <summary>The van pulls away from the crew on foot: the car is kept for it (reserved for nobody) and every living member walks back to its door.</summary>
    void StartRemount(ServerCar car)
    {
        JobVehicles.SetRiders(car, Array.Empty<ushort>());
        foreach (var (m, seat) in _seats)
            if (m.Alive) m.Remount(ExitSpot(car, seat));
    }

    /// <summary>A member that got to the car's door sits down in its seat (posted there, as when it spawned).</summary>
    void OnMounted(GangMember m)
    {
        if (_car is not { Destroyed: false } car || !_machine.Remounting) { m.GiveUpRemount(); return; }
        foreach (var (member, seat) in _seats)
        {
            if (member != m) continue;
            m.Post(SlotFeet(car, seat), car.State.Yaw, seat: true, shut: InBay(car, seat));
            _riders.Add((m, seat));
            return;
        }
    }

    /// <summary>The walk back did not end in the car (it was wrecked or taken, the driver died, the clock ran out): the ones seated step out again, the ones still walking stand and fight on foot; the car is free to take.</summary>
    void AbortRemount(ServerCar car, NVec3 van)
    {
        SitOut(car);
        foreach (var (m, _) in _seats) m.GiveUpRemount();
        JobVehicles.SetRiders(car, null);
        if (_crew != null) _crew.Post = FootPost(van);
    }

    /// <summary>The car blew up: whoever still rides it dies with it (as the riders of any destroyed vehicle), and its wreck is given its time.</summary>
    void OnExploded(ServerCar car, ushort attacker)
    {
        foreach (var (m, _) in _riders) m.Kill(attacker);
        _cleanup.Exploded();
    }

    // ---- removal ----

    /// <summary>Takes the car and the crew out of the world now (given up, or the Heist ended).</summary>
    public void Remove() => Apply(ChaseCleanup.Due(_machine.State, Left(), ended: true));

    void RemoveCar()
    {
        if (_car is not { } car) return;
        car.Exploded -= OnExploded;
        car.NpcInput = null;
        _s.Rivals.Unmount(car);
        _s.Vehicles.Remove(car);
        _car = null;
    }

    void RemoveCrew()
    {
        if (_crew is not { } crew) return;
        crew.Mounted -= OnMounted;
        _s.Rivals.Remove(crew);
        _crew = null;
        _riders.Clear();
        _seats.Clear();
    }
}

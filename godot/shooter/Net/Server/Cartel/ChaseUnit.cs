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
/// One Rival chase car on the server (GLOSSARY "Chase car"; docs/cartel.md "Chase cars"): a real vehicle of vehicles.json (a <see cref="ServerCar"/> on the drive model, in its darkest skin) that an NPC drives, and its
/// crew of Rival Gang members. The vehicle is driven by inputs this class sets (<see cref="ServerCar.NpcInput"/>: <see cref="ChasePilot"/> over the roads, the vehicle step does the rest), so it is shot, bumped and
/// destroyed like any car. The crew rides it as <see cref="GangMember"/>s posted on its seats (the post moves with the car: <see cref="PlaceRiders"/>): the driver a Rival Enforcer on seat 0, the Gunmen on the others,
/// standing figures lowered into the body so their torso is at the window. While they ride, their sight and rounds start inside the hull and skip it (<see cref="RivalCrewServer.Mount"/>); they shoot only while the car is
/// within shoot_range_m of the van, with a worse aim while it moves. The machine (<see cref="ChaseCar"/>) decides when the car stops, when the crew gets out and when it gives up.
/// </summary>
internal sealed class ChaseUnit
{
    /// <summary>A member's step-out point snapped to the navigation mesh is used when it moved no more than this.</summary>
    const float SnapM = 3f;
    /// <summary>The car is "on the road" for its way when a road lies within this of it.</summary>
    const float RoadSnapM = 40f;
    /// <summary>The van is searched for a road point this far from it when it is off the road.</summary>
    const float RoadSearchM = 1000f;

    /// <summary>The way over the roads to where the car is going, and what the van is doing to it: on the road (the way ends at the van's place), or off it (the way ends at the road point nearest it).</summary>
    readonly record struct Way(RoadPath Path, bool VanOnRoad, NVec2 RoadPoint);

    readonly ChaseServer _s;
    readonly HeistRun _run;
    readonly ChaseData _d;
    readonly ChaseCar _machine;
    readonly List<(GangMember Member, int Seat)> _riders = new();
    GangMember? _driver;
    ServerCar? _car;
    ThiefGang? _crew;
    Way _way = new(RoadPath.Empty, true, NVec2.Zero);
    Countdown _repath;
    Countdown _wreck;

    ChaseUnit(ChaseServer s, HeistRun run, ChaseData d, ServerCar car, NVec3 van)
    {
        _s = s; _run = run; _d = d; _car = car; _machine = new ChaseCar(d);
        _crew = s.Rivals.NewCrew(van, id => _machine.Shoots && run.Party.Contains(id)); // it attacks the Job's party, only while the machine lets it shoot
        car.Exploded += OnExploded;
        _machine.Changed += (from, to) => GD.Print($"server: {_run.Job}: chase car {car.Id}: {from} -> {to}");
    }

    /// <summary>Puts a car down on <paramref name="spot"/> with its crew seated (the driver, then the Gunmen), reserved for nobody: no player may board while the crew rides. Null when the world has no room.</summary>
    public static ChaseUnit? Spawn(ChaseServer s, HeistRun run, ChaseData d, ChaseSpot spot)
    {
        var at = new NVec3(spot.Ground.X, s.Ground(spot.Ground.X, spot.Ground.Y), spot.Ground.Y);
        if (s.Vehicles.Spawn(d.VehicleType, at, spot.Yaw, Array.Empty<ushort>(), (byte)Math.Clamp(d.Skin, 0, 255)) is not { } car) return null;
        var unit = new ChaseUnit(s, run, d, car, run.VanFeet);
        var gunmen = ChaseRules.Gunmen(d, run.Job.Tier, run.Party.Count);
        var seats = Math.Min(1 + gunmen, car.Def.Seats.Count);
        var driver = unit.Seat(GangMemberKind.Enforcer, 0);
        var ok = driver != null;
        for (var seat = 1; ok && seat < seats; seat++) ok = unit.Seat(GangMemberKind.Gunman, seat) != null;
        if (!ok) { unit.Remove(); return null; }
        s.Rivals.Mount(car);
        GD.Print($"server: {run.Job}: chase car {car.Id} ({car.Def.Id} skin {d.Skin}) at ({at.X:F0},{at.Z:F0}), {seats - 1} Gunmen, {spot.BackM:F0} m behind the van");
        return unit;
    }

    /// <summary>One member of the crew in a seat of the car (posted there). Null when the world has no room for it.</summary>
    GangMember? Seat(GangMemberKind kind, int seat)
    {
        var m = _crew!.AddPosted(kind, SeatFeet(_car!, seat), _car!.State.Yaw);
        if (m == null) return null;
        _riders.Add((m, seat));
        if (seat == 0) _driver = m;
        return m;
    }

    /// <summary>Where a rider's feet are: the seat's point, lowered seat_drop_m (a standing figure's torso is then at the window and its legs in the body).</summary>
    NVec3 SeatFeet(ServerCar car, int seat)
    {
        var p = CarPose.SeatPos(car, seat);
        return new NVec3(p.X, p.Y - _d.SeatDropM, p.Z);
    }

    // ---- what the Heist asks ----

    /// <summary>The car counts towards max_alive.</summary>
    public bool InFight => _machine.InFight(_crew?.AliveCount ?? 0);

    /// <summary>Nothing of it is left in the world (car and crew removed): the Heist forgets it.</summary>
    public bool Finished => _car == null && _crew == null;

    /// <summary>Where the car stands (x, z), or null once it is removed.</summary>
    public NVec2? Where => _car is { } c ? new NVec2(c.GlobalPosition.X, c.GlobalPosition.Z) : null;

    bool DriverAlive => _driver?.Alive ?? false;

    // ---- the tick ----

    /// <summary>One tick of the Heist (before the vehicles step): the machine, then what its state asks of the car and the crew.</summary>
    public void Tick(float dt, NVec3 van, float vanSpeed)
    {
        if (_car is { Destroyed: true } && _wreck.Tick(dt)) RemoveCar(); // the wreck lies its time, then goes (before the type's return time brings it back)
        if (_car is not { } car)
        {
            if (_machine.State == ChaseState.Out) TickOut(van);
            else Wind();
            return;
        }
        var pos = new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z);
        var van2 = new NVec2(van.X, van.Z);
        var speed = car.State.Speed;
        _repath.Tick(dt);
        if (_repath.Ready && _machine.CrewInside) { Replan(pos, van2); _repath.Start(_d.RepathSeconds); }
        var gap = NVec2.Distance(pos, van2);
        var roadPointM = _way.VanOnRoad ? float.PositiveInfinity : NVec2.Distance(pos, _way.RoadPoint);
        var step = _machine.Step(new ChaseFacts(gap, vanSpeed, _way.VanOnRoad, roadPointM, MathF.Abs(speed), DriverAlive, car.Destroyed, !_way.Path.IsEmpty), dt);
        if (step.Remove) { Remove(); _machine.End(); return; }
        if (step.GetOut) GetOut(car, van);
        switch (_machine.State)
        {
            case ChaseState.Chasing:
                var want = _way.VanOnRoad ? ChaseRules.FollowSpeed(_d, gap, vanSpeed) : ChaseRules.ApproachSpeed(_d, roadPointM);
                car.NpcInput = ChasePilot.Follow(_way.Path, pos, car.State.Yaw, speed, DriveModel.TurnRadiusAt(car.Def, MathF.Abs(speed)), want);
                break;
            case ChaseState.Stopping:
                car.NpcInput = DriverAlive ? ChasePilot.Stop(speed) : null; // a dead driver: it rolls to a stop
                break;
            case ChaseState.Out:
                TickOut(van);
                return;
            case ChaseState.Gone:
                Wind();
                return;
        }
        if (_machine.CrewInside) TickRiders(car, speed);
    }

    /// <summary>The crew rides: the Gunmen's aim by the car's speed, and the crew holds fire while the car is farther than shoot_range_m from the van.</summary>
    void TickRiders(ServerCar car, float speed)
    {
        var error = ChaseRules.AimErrorDeg(_d, speed);
        foreach (var (m, _) in _riders) m.AimErrorDeg = error;
        if (!_machine.Shoots) _crew?.Calm();
    }

    /// <summary>The crew is out: it heads for the van's place and fights on foot; once nothing of it is left the car is Gone.</summary>
    void TickOut(NVec3 van)
    {
        if (_crew is not { } crew) { _machine.End(); return; }
        crew.Post = van;
        if (crew.Count == 0) _machine.End();
    }

    /// <summary>Gone: the car is taken out unless a player sits in it (they keep it until they leave or the Job ends), and the crew's bodies are given their time.</summary>
    void Wind()
    {
        if (_car is { Destroyed: false, Seat.Occupied: false }) RemoveCar();
        if (_crew is { Count: 0 }) RemoveCrew();
    }

    // ---- the way ----

    /// <summary>
    /// The way to where the car goes, again (every repath_seconds): to the van's place on the road when a road lies within off_road_m of it, else to the road point nearest it (the car then stops there and the crew
    /// gets out: roads only). No way when the car has no road within reach.
    /// </summary>
    void Replan(NVec2 car, NVec2 van)
    {
        var roads = _s.Roads;
        var onRoad = roads.Nearest(van, _d.OffRoadM);
        var target = onRoad ?? roads.Nearest(van, RoadSearchM);
        var point = target is { } t ? roads.PointAt(t.Place) : van;
        if (roads.Nearest(car, RoadSnapM) is not { } from || target is not { } to) { _way = new Way(RoadPath.Empty, onRoad != null, point); return; }
        _way = new Way(roads.RouteBetween(from.Place, to.Place)?.Path ?? RoadPath.Empty, onRoad != null, point);
    }

    // ---- the crew ----

    /// <summary>
    /// After the vehicles moved: the riders sit on their seats where the car is now. The car's pose, yaw included, is theirs: a posted member turns back to it when it has no target.
    /// </summary>
    public void PlaceRiders()
    {
        if (_car is not { } car || !_machine.CrewInside) return;
        foreach (var (m, seat) in _riders) m.MovePost(SeatFeet(car, seat), car.State.Yaw);
    }

    /// <summary>
    /// The car stands: the crew steps out through the door on its seat's side (snapped to the navigation mesh), is no longer posted and heads for the van and fights on foot as a Rival crew; the car is free to take
    /// (a player may drive it away, also with a dead driver: nobody is reserved any more).
    /// </summary>
    void GetOut(ServerCar car, NVec3 van)
    {
        _s.Rivals.Unmount(car);
        car.NpcInput = null;
        JobVehicles.SetRiders(car, null);
        var yaw = car.State.Yaw;
        var right = new NVec3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        foreach (var (m, seat) in _riders)
        {
            if (!m.Alive) continue;
            var off = car.Def.Seats[seat].Offset;
            var side = off[0] < 0 ? -1f : 1f;
            var p = CarPose.SeatPos(car, seat);
            var raw = new NVec3(p.X, car.GlobalPosition.Y, p.Z) + right * (side * (car.HalfWidth - MathF.Abs(off[0]) + _d.ExitSideM));
            var snapped = _s.Rivals.Snap(raw);
            m.AimErrorDeg = null;
            m.Unpost(NVec3.Distance(snapped, raw) <= SnapM ? snapped : raw, yaw);
        }
        _riders.Clear();
        if (_crew != null) _crew.Post = van;
    }

    /// <summary>The car blew up: whoever still rides it dies with it (as the riders of any destroyed vehicle), and its wreck is given its time.</summary>
    void OnExploded(ServerCar car, ushort attacker)
    {
        foreach (var (m, _) in _riders) m.Kill(attacker);
        _wreck.Start(_d.WreckSeconds);
    }

    // ---- removal ----

    /// <summary>Takes the car and the crew out of the world now (given up, or the Heist ended).</summary>
    public void Remove()
    {
        RemoveCar();
        RemoveCrew();
    }

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
        _s.Rivals.Remove(crew);
        _crew = null;
        _riders.Clear();
    }
}

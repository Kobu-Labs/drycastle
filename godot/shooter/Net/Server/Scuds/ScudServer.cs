using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>
/// The Scud truck on the server (GLOSSARY "Scud truck", "Launcher", "Scud missile", "Scud blast"). Each truck (a vehicle whose type has a vehicles.json <c>launcher</c> block) has a
/// <see cref="ScudLauncher"/> (<see cref="ScudRigs"/>): the machine owns the rocket, the operator's lock, the crew's confirmations and the rack's clock; this class feeds it the crew and the truck's place, brakes the truck
/// while the rack moves, spawns the missile when the ignition ends, answers the crew's commands and tells the clients what changed. Loading from a cart is <see cref="ScudLoading"/>, the blast <see cref="ScudBlastServer"/>,
/// the event that brings a truck <see cref="ScudEventServer"/>.
/// </summary>
internal sealed class ScudServer
{
    readonly ServerContext _ctx;
    readonly ScudFile _scud;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Node3D _world;
    readonly Water _water;
    readonly Func<FlightBounds> _flightBounds;
    readonly Fleet _fleet;
    readonly ScudRigs _rigs;
    readonly ScudLoading _loading;
    readonly ScudBlastServer _blast;
    readonly Func<WorldQuery> _query;

    /// <summary>Registers the missile's world entity type (so it comes after the kinds that must register first).</summary>
    public ScudServer(ServerContext ctx, ScudFile scud, Func<VehiclesFile?> vehicles, Drycastle.MapLoader.MapLoader? map, Node3D world, Water water, Func<FlightBounds> flightBounds, Fleet fleet,
        ScudRigs rigs, ScudLoading loading, ScudBlastServer blast, Func<WorldQuery> query)
    {
        _ctx = ctx; _scud = scud; _vehicles = vehicles; _map = map; _world = world; _water = water; _flightBounds = flightBounds; _fleet = fleet; _rigs = rigs; _loading = loading; _blast = blast; _query = query;
        ScudMissileRules.Register(WorldEntityRegistry.Shared);
    }

    // ---- the trucks ----

    /// <summary>
    /// A launcher truck standing at <paramref name="position"/> (the ground there is found), turned to <paramref name="yaw"/>: the first vehicle type with a launcher block, one more vehicle record. Null
    /// when the data has no such type. It is never dealt at the match start (spawn_count 0) and never returns once destroyed (<see cref="VehicleDef.Returns"/>); a new round retires it.
    /// </summary>
    public ServerCar? SpawnTruck(NVec3 position, float yaw)
    {
        var vehicles = _vehicles();
        if (vehicles == null) return null;
        var kind = vehicles.Vehicles.FindIndex(v => v.Launcher != null);
        if (kind < 0) { GD.PrintErr("server: no vehicle type has a launcher block, no Scud truck spawned"); return null; }
        var def = vehicles.Vehicles[kind];
        var ground = _map?.GroundHeight(position.X, position.Z) ?? float.NaN;
        if (float.IsNaN(ground)) ground = position.Y;
        var space = _world.GetWorld3D().DirectSpaceState;
        var y = ServerCar.RestHeight(space, new Vector3(position.X, ground, position.Z), yaw, def.HalfWidth, def.HalfLength, ground) ?? ground;
        var car = new ServerCar
        {
            Name = $"{def.Id}{_fleet.Count}", Id = _fleet.NextId, Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(position.X, y + 0.02f, position.Z), OriginYaw = yaw,
            FlightWater = _water, FlightBounds = _flightBounds(),
        };
        _world.AddChild(car);
        _fleet.Add(car);
        GD.Print($"server: Scud truck {car.Id} at ({position.X:F1},{y:F1},{position.Z:F1}) facing {yaw * 180f / MathF.PI:F0} deg");
        return car;
    }

    /// <summary>
    /// The rocket cart's job: loads a rocket onto an empty truck (Empty to Reloading, then Stowed after <see cref="ScudLauncherData.ReloadSeconds"/>; the truck stays immobilised meanwhile).
    /// False when the truck is not a launcher, is destroyed, or is not Empty (a loaded or busy launcher takes no second rocket).
    /// </summary>
    public bool TryReloadScud(ServerCar truck)
    {
        if (truck.Destroyed || _rigs.Of(truck) is not { } rig || !rig.Launcher.BeginReload()) return false;
        GD.Print($"server: Scud truck {truck.Id} is reloading");
        return true;
    }

    /// <summary>An admin's instant reload (Empty, or a reload under way, to Stowed at once). False when there is nothing to reload.</summary>
    public bool ReloadScudNow(ServerCar truck)
    {
        if (truck.Destroyed || _rigs.Of(truck) is not { } rig || !rig.Launcher.ReloadNow()) return false;
        GD.Print($"server: Scud truck {truck.Id} reloaded at once");
        return true;
    }

    /// <summary>The launcher's state of a truck, or null when it is not a launcher.</summary>
    public ScudLauncherState? StateOf(ServerCar truck) => _rigs.Of(truck)?.Launcher.State;

    /// <summary>THE Scud blast (<see cref="ScudBlastServer.Blast"/>): the missile's impact, and a loaded rocket cart that is shot.</summary>
    public void Blast(NVec3 position, ushort attackerId, ushort missileId = 0) => _blast.Blast(position, attackerId, missileId);

    /// <summary>The interact key beside a truck with an empty rack and a loaded cart: a tap gets in or out, a hold loads the rocket (<see cref="ScudLoading.UseKey"/>).</summary>
    public bool UseKey(ServerClient c, bool useDown, bool useEdge, float dt) => _loading.UseKey(c, useDown, useEdge, dt);

    /// <summary>Whether a loading from a cart is on offer to the player: the interact key has another use there.</summary>
    public bool HasLoadingOffer(ServerClient c) => _loading.HasOffer(c);

    /// <summary>A new round: the launchers start over, and a truck that never returns is retired instead of reset (hidden, out of its seats). The event's own end comes first (<see cref="ScudEventServer.RoundReset"/>).</summary>
    public void RoundReset()
    {
        _rigs.Clear();
        foreach (var car in _fleet.Cars)
        {
            if (car.Def.Returns || car.Destroyed) continue;
            car.Seat.Clear();
            car.Destroy(double.MaxValue);
        }
    }

    // ---- the tick ----

    /// <summary>
    /// Once a tick, before the vehicles are stepped: every launcher truck's machine, its brakes while the rack moves, the missile when the ignition ends, and what changed sent to the clients.
    /// </summary>
    public void Tick(float dt)
    {
        if (_vehicles() == null) return;
        foreach (var car in _fleet.Cars)
        {
            if (car.Def.Launcher == null) continue;
            if (car.Destroyed)
            {
                car.Immobilised = false;
                if (_rigs.Remove(car.Id)) _ctx.Broadcast(EventKind.ScudState, ScudCodec.Encode(new ScudStateMsg(car.Id, ScudLauncherState.Stowed, 0, float.PositiveInfinity, 0, 0, null, 0, 0)));
                continue;
            }
            var rig = _rigs.Of(car)!;
            var crew = CrewOf(car);
            var order = rig.Launcher.Tick(dt, crew, N(car.GlobalPosition), _loading.SenseOf(car, rig));
            if (order.Launch) Launch(car, order);
            if (order.TakeCart != 0) _loading.TakeRocket(order.TakeCart);
            car.Immobilised = rig.Launcher.Immobilised; // a driven truck: every drive step is held (ServerCar.StepDrive), as its driver's client predicts it
            if (rig.Launcher.Immobilised && _fleet.OccupantAt(car, 0) is { } driver)
            {
                driver.Throttle = 0; driver.Steer = 0; driver.Handbrake = true; // brakes on, the driver's input ignored while the rack moves
                car.Wake();
            }
            SendState(car, rig);
            SendView(car, rig, crew);
        }
    }

    /// <summary>The truck's seats and who sits in them, the operator's seat named by the type.</summary>
    ScudCrew CrewOf(ServerCar car)
    {
        var seats = new ushort[car.Seat.Count];
        for (var i = 0; i < seats.Length; i++) seats[i] = car.Seat.Occupant(i);
        return new ScudCrew(seats, car.Def.Launcher!.OperatorSeat);
    }

    static ScudStateMsg StateMsgOf(ServerCar car, ScudRig rig)
    {
        var l = rig.Launcher;
        return new ScudStateMsg(car.Id, l.State, l.Elapsed, l.Left, l.RackFrom, l.Operator, l.Lock, (byte)l.Confirmed, (byte)l.Need);
    }

    /// <summary>Sends the launcher's state to everyone when the state, the lock, the operator or the confirmations changed (the clients count the clocks on from there).</summary>
    void SendState(ServerCar car, ScudRig rig)
    {
        var now = StateMsgOf(car, rig);
        var was = rig.Sent;
        if (was != null && was.State == now.State && was.Lock == now.Lock && was.Operator == now.Operator && was.Confirmed == now.Confirmed && was.Need == now.Need) return;
        rig.Sent = now;
        if (was == null || was.State != now.State) rig.ViewDirty = true; // the feed starts with the screen
        _ctx.Broadcast(EventKind.ScudState, ScudCodec.Encode(now));
    }

    /// <summary>The operator's camera to the rest of the crew, at <see cref="ScudTargetingData.ViewHz"/> while it changes and the screen is open.</summary>
    void SendView(ServerCar car, ScudRig rig, ScudCrew crew)
    {
        if (!rig.ViewDirty || !ScudRules.FeedLive(rig.Launcher.State) || _ctx.Now - rig.ViewSentAt < 1.0 / _scud.Targeting.ViewHz) return;
        rig.ViewDirty = false; rig.ViewSentAt = _ctx.Now;
        var msg = new EventMsg((byte)EventKind.ScudView, ScudCodec.Encode(new ScudViewMsg(car.Id, rig.ViewX, rig.ViewZ, rig.ViewZoom)));
        for (var s = 0; s < crew.Seats.Count; s++)
            if (s != crew.OperatorSeat && _fleet.OccupantAt(car, s) is { } o) _ctx.Send(o, msg);
    }

    /// <summary>A joiner learns every launcher that is not stowed and idle (the rack, the proposal) and the operator's camera while a screen is open.</summary>
    public void SendOnJoin(ServerClient c)
    {
        foreach (var (id, rig) in _rigs.All)
        {
            if (_fleet.Find(id) is not { } car) continue;
            if (rig.Launcher.State != ScudLauncherState.Stowed) _ctx.SendEvent(c, EventKind.ScudState, ScudCodec.Encode(StateMsgOf(car, rig)));
        }
    }

    // ---- the crew's commands ----

    /// <summary>A crew member's command (<see cref="EventKind.ScudCommand"/>). Anything invalid is ignored; a refused one answers with a notice.</summary>
    public void OnCommand(ServerClient c, byte[] payload)
    {
        if (ScudCodec.DecodeCommand(payload) is not { } m) return;
        if (c.Car is not { Destroyed: false } car || _rigs.Of(car) is not { } rig || !_ctx.IsAlive(c.Id)) return;
        var crew = CrewOf(car);
        var l = rig.Launcher;
        ScudAnswer answer;
        switch (m.Cmd)
        {
            case ScudCmd.Open: answer = l.Open(crew, c.Id); break;
            case ScudCmd.Close: answer = l.Close(crew, c.Id); break;
            case ScudCmd.Confirm: answer = l.Confirm(crew, c.Id, car.State.Speed); break;
            case ScudCmd.Withdraw: answer = l.Withdraw(crew, c.Id); break;
            case ScudCmd.Abort: answer = l.Abort(crew, c.Id); break;
            case ScudCmd.View:
                // the operator's camera, relayed to the others at the view rate; anywhere on the map
                answer = crew.Operator != c.Id ? ScudAnswer.NotOperator : !ScudRules.FeedLive(l.State) ? ScudAnswer.WrongState : ScudAnswer.Ok;
                if (answer == ScudAnswer.Ok)
                {
                    rig.ViewX = m.X; rig.ViewZ = m.Z; rig.ViewZoom = Math.Clamp(m.Zoom, (float)_scud.Targeting.ZoomMin, (float)_scud.Targeting.ZoomMax);
                    rig.ViewDirty = true;
                }
                return; // a camera that does not apply is not worth a notice
            case ScudCmd.Lock:
            {
                var ground = _map?.GroundHeight(m.X, m.Z) ?? 0f;
                var onMap = _map == null || !float.IsNaN(ground);
                answer = l.PlaceLock(crew, c.Id, new NVec3(m.X, onMap ? ground : 0f, m.Z), N(car.GlobalPosition), onMap);
                if (answer == ScudAnswer.Ok) GD.Print($"server: {c.Name} #{c.Id} locked ({m.X:F0},{ground:F0},{m.Z:F0}) with Scud truck {car.Id}, {ScudRules.Range(N(car.GlobalPosition), l.Lock!.Value):F0} m");
                break;
            }
            default: return;
        }
        if (ScudCodec.NoticeOf(answer) is { } code) _ctx.SendEvent(c, EventKind.ScudNotice, ScudCodec.Encode(code));
    }

    // ---- the missile ----

    /// <summary>The rack's ignition ran out: the missile enters the world at the rack, on a flight planned now onto the locked point (<see cref="ScudFlightPlan"/>).</summary>
    void Launch(ServerCar car, ScudTick order)
    {
        var from = VehicleSeat.SeatPosition(N(car.GlobalPosition), car.State.Yaw, car.Def.Launcher!.Muzzle);
        var plan = new ScudFlightPlan(from, order.Target, _scud.Missile, (float)_scud.Launcher.MinRangeM);
        var missile = new ScudMissileEntity(order.Placer, plan, m => _blast.Blast(m.Plan.Target, m.Owner, m.Id));
        if (_ctx.Match.Entities.Spawn(missile) is not { } id)
        {
            GD.PrintErr("server: no room for the Scud missile (entity cap)");
            return;
        }
        missile.Id = id;
        _ctx.Broadcast(EventKind.ScudLaunch, ScudCodec.Encode(new ScudLaunchMsg(id, car.Id, order.Placer, from, order.Target, plan.Seconds)));
        GD.Print($"server: Scud missile {id} left truck {car.Id} for ({order.Target.X:F0},{order.Target.Y:F0},{order.Target.Z:F0}) placed by #{order.Placer}, {plan.Seconds:F1} s, apex {plan.Apex.Y:F0} m");
    }

    // ---- the admin's command ----

    /// <summary>"/scud spawn | reload | blast | state": the test commands (admins only).</summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        switch (args[0])
        {
            case "spawn":
            {
                if (c.Car != null) { _ctx.ChatTo(c, ChatLineKind.Error, "Leave the vehicle first."); return; }
                var def = _vehicles()?.Vehicles.FirstOrDefault(v => v.Launcher != null);
                if (def == null) { _ctx.ChatTo(c, ChatLineKind.Error, "There is no Scud truck in vehicles.json."); return; }
                var yaw = c.Body.Yaw;
                var p = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * (def.HalfLength + 5f);
                if (SpawnTruck(N(p), yaw) is { } car) _ctx.ChatTo(c, ChatLineKind.Info, $"A Scud truck stands in front of you (vehicle {car.Id}).");
                else _ctx.ChatTo(c, ChatLineKind.Error, "The Scud truck could not be spawned.");
                break;
            }
            case "reload":
            {
                var truck = TruckNear(c);
                if (truck == null) { _ctx.ChatTo(c, ChatLineKind.Error, "No Scud truck within 40 m, and you do not ride one."); return; }
                var reloaded = ReloadScudNow(truck);
                var state = StateOf(truck);
                if (reloaded || state == ScudLauncherState.Stowed) _ctx.ChatTo(c, ChatLineKind.Info, "The Scud truck is loaded.");
                else _ctx.ChatTo(c, ChatLineKind.Error, $"Nothing to reload: the launcher is {state} (a rack that is up has to lower first).");
                break;
            }
            case "blast":
            {
                var yaw = c.Body.Yaw;
                var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * 200f;
                if (_query().Ground(MoverKind.Player, at, 80f, 300f) is { } ground) at = ground.Position;
                _blast.Blast(N(at), c.Id);
                _ctx.ChatTo(c, ChatLineKind.Info, "A Scud blast 200 m in front of you.");
                break;
            }
            case "state":
            {
                var truck = TruckNear(c);
                _ctx.ChatTo(c, ChatLineKind.Info, truck == null ? "No Scud truck within 40 m, and you do not ride one." : $"Scud truck {truck.Id}: launcher {StateOf(truck)}.");
                break;
            }
        }
    }

    /// <summary>The Scud truck the player rides, else the nearest within 40 m.</summary>
    ServerCar? TruckNear(ServerClient c)
    {
        if (c.Car is { Destroyed: false } ridden && ridden.Def.Launcher != null) return ridden;
        ServerCar? best = null; var bd = 40f;
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Def.Launcher == null) continue;
            var d = car.GlobalPosition.DistanceTo(c.Body.GlobalPosition);
            if (d <= bd) { best = car; bd = d; }
        }
        return best;
    }

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);
}

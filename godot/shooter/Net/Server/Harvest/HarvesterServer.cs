using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Harvest;

/// <summary>
/// The harvester event on the server (#331, GLOSSARY "Event"). Now and then, or on <c>/event harvester</c>, a harvester (vehicles.json <c>harvester</c>, an ordinary <see cref="ServerCar"/> with an
/// open cab) appears on a crop field near the players (<see cref="CropFields"/>, from the ground patches of patches.json) and drives it in passes (<see cref="FieldPlanner"/>, <see cref="HarvesterBrain"/>),
/// then leaves along the edge. Its driver is a seated bot (<see cref="ServerClient.HarvesterDriver"/>): shot dead it stays seated and rides (ADR 0026) and anyone may take the seat once it rests.
/// This class is the job: its start, its AI driver's pedals, its end and what a joiner is told of it; what the machine does to the ground and to people is <see cref="CropCut"/>, <see cref="ReelServer"/> and
/// <see cref="BaleFire"/>. The rules are pure and tested in <c>ShooterCore/Events</c>. One <see cref="ServerCar"/> serves every job of a server run: it is hidden (destroyed, silently) between them.
/// </summary>
internal sealed class HarvesterServer
{
    readonly ServerContext _ctx;
    readonly HarvesterConfig _cfg;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Node3D _world;
    readonly Water _water;
    readonly Func<FlightBounds> _flightBounds;
    readonly Random _rng;
    readonly EventBook _events;
    readonly Fleet _fleet;
    readonly ProfileServer _profiles;
    readonly PlayerIds _ids;
    readonly Action<ServerClient> _join;
    readonly Action<ServerClient> _leave;
    readonly Func<ServerCar, int, Vector3> _seatPos;
    readonly Action<ServerClient, bool> _leaveSeat;
    readonly CropCut _cut;
    readonly BaleFire _bales;
    readonly ReelServer _reel;
    readonly HarvesterDirector _director;

    ServerCar? _car;
    ServerClient? _driver;
    HarvesterBrain? _brain;
    CropField? _field;
    int _fieldIndex;
    double _sendAt;
    /// <summary>Drivers that lost the job (dead, or the job ended): taken out of the match once their corpse exists.</summary>
    readonly List<ServerClient> _oldDrivers = new();

    /// <param name="vehicles">vehicles.json, read live (the server falls back to a default table at the end of the start).</param>
    /// <param name="seatPos">Where a seat of a vehicle is in the world (<c>NetServer.SeatPos</c>).</param>
    /// <param name="leaveSeat">A player stops being seated; the flag puts the body on a free side of the vehicle (<c>NetServer.LeaveSeat</c>).</param>
    /// <param name="join">A made client joins the match; <paramref name="leave"/> takes one out (<c>NetServer.Join</c>, <c>Leave</c>).</param>
    public HarvesterServer(ServerContext ctx, HarvesterConfig cfg, Func<VehiclesFile?> vehicles, Drycastle.MapLoader.MapLoader? map, Node3D world, Water water, Func<FlightBounds> flightBounds, Random rng,
        EventBook events, Fleet fleet, ProfileServer profiles, PlayerIds ids, Action<ServerClient> join, Action<ServerClient> leave, Func<ServerCar, int, Vector3> seatPos, Action<ServerClient, bool> leaveSeat,
        CropCut cut, BaleFire bales, ReelServer reel)
    {
        _ctx = ctx; _cfg = cfg; _vehicles = vehicles; _map = map; _world = world; _water = water; _flightBounds = flightBounds; _rng = rng; _events = events; _fleet = fleet; _profiles = profiles; _ids = ids;
        _join = join; _leave = leave; _seatPos = seatPos; _leaveSeat = leaveSeat; _cut = cut; _bales = bales; _reel = reel;
        _director = new HarvesterDirector(cfg, rng);
    }

    bool Running => _brain != null;

    /// <summary>The harvester's driver: the server steers it, so its input is not a player's (the vehicle step reads this).</summary>
    public bool IsDriver(ServerClient c) => c == _driver;

    // ---- the lifecycle ----

    /// <summary>"/event harvester": one now, ignoring the cooldown; refuses while one is out. The answer says where it appeared.</summary>
    public void Start(ServerClient admin)
    {
        if (!TryStart(forced: true, out var said)) { _ctx.ChatTo(admin, ChatLineKind.Error, said); return; }
        _ctx.ChatTo(admin, ChatLineKind.Info, said);
    }

    /// <summary>
    /// Starts a job: a field (near the players; for the command also the nearest one), its plan, the machine and its driver at the field's entry. False with the reason; the automatic spawn just waits for its next roll.
    /// <paramref name="said"/> is the command's answer: field id and the position in map metres.
    /// </summary>
    bool TryStart(bool forced, out string said)
    {
        said = "";
        var vehicles = _vehicles();
        if (vehicles == null || vehicles.Vehicles.FindIndex(v => v.Id == HarvesterRules.VehicleId) is var kind && kind < 0) { said = "There is no harvester in vehicles.json."; return false; }
        if (_director.Active)
        {
            said = _field != null && _car != null ? HarvesterRules.RefusedRunning(_field.Id, N(_car.GlobalPosition)) : _director.WhyNotForced()!;
            return false;
        }
        if (_events.WhyNotStart(EventBook.Harvester) is { } busy) { said = busy; return false; }
        if (_ctx.Match.Phase != MatchPhase.Running) { said = "No round is running."; return false; }
        if (_car is { Destroyed: false } old && old.Seat.Occupied) { said = HarvesterRules.RefusedInUse; return false; }

        if (PickField(forced) is not { } pick) { said = forced ? HarvesterRules.RefusedNoField : ""; return false; }
        var (field, plan) = pick;
        var def = vehicles.Vehicles[kind];
        var car = Machine((byte)kind, def, plan);
        if (AddDriver() is not { } driver) { said = "No player id is free for the harvester's driver."; return false; }
        if (!car.Seat.TryEnter(driver.Id, 0)) { _leave(driver); said = "The harvester's driver could not take the seat."; return false; }
        _car = car; _driver = driver;
        driver.Car = car; driver.CarSeat = 0; driver.Throttle = driver.Steer = 0; driver.Handbrake = false; driver.Body.InVehicle = true;
        driver.Body.Teleport(_seatPos(car, 0));
        _ctx.Match.SetPosition(driver.Id, N(driver.Body.GlobalPosition));
        _brain = new HarvesterBrain(plan, _cfg);
        _field = field; _fieldIndex = _cut.Fields.IndexOf(field);
        _director.Started();
        _events.Start(EventBook.Harvester, _ctx.Now);
        _sendAt = 0;
        var at = N(car.GlobalPosition);
        said = HarvesterRules.SpawnedText(field.Id, at);
        GD.Print($"server: harvester started on {field.Id} ({field.Type}, {field.Area:F0} m2), {plan.Passes.Count} passes of {plan.TotalCutM:F0} m, at ({at.X:F1},{at.Y:F1},{at.Z:F1}), driver #{driver.Id}");
        return true;
    }

    /// <summary>A field near the players (for the command also the nearest) and the passes that fit it; null when none does.</summary>
    (CropField Field, HarvestPlan Plan)? PickField(bool forced)
    {
        var players = _ctx.Players.Values.Where(o => !o.IsBot && _ctx.IsAlive(o.Id)).Select(o => new NVec2(o.Body.GlobalPosition.X, o.Body.GlobalPosition.Z)).ToList();
        CropField? field = null; HarvestPlan? plan = null;
        var pool = _cut.Fields.ToList();
        for (var tries = 0; tries < 8 && plan == null; tries++)
        {
            field = CropFields.Pick(pool, players, _cfg.NearPlayerM, _cfg.MinFieldAreaM2, _rng, orNearest: forced);
            if (field == null) break;
            plan = FieldPlanner.Plan(field.Ring, _cfg);
            if (plan == null) pool.Remove(field); // a sliver that fits no pass: another field
        }
        return field == null || plan == null ? null : (field, plan);
    }

    /// <summary>The machine at the plan's entry, on the ground there: the one car of the server run, repaired and put back from where it was hidden.</summary>
    ServerCar Machine(byte kind, VehicleDef def, HarvestPlan plan)
    {
        var ground = _map?.GroundHeight(plan.Entry.X, plan.Entry.Y) ?? float.NaN;
        if (float.IsNaN(ground)) ground = _map?.GroundHeightOrMean(plan.Entry.X, plan.Entry.Y) ?? 0f;
        var space = _world.GetWorld3D().DirectSpaceState;
        var y = ServerCar.RestHeight(space, new Vector3(plan.Entry.X, ground, plan.Entry.Y), plan.EntryYaw, def.HalfWidth, def.HalfLength, ground) ?? ground;
        var origin = new Vector3(plan.Entry.X, y + 0.02f, plan.Entry.Y);
        var car = _car ?? MakeCar(kind, def, origin, plan.EntryYaw);
        car.OriginPos = origin; car.OriginYaw = plan.EntryYaw;
        car.Reset(); // back from where it was hidden: repaired, at the entry
        car.Seat.Clear(); car.Seat.Unblock(0); // a corpse that rode the last job's seat is not in the way
        return car;
    }

    ServerCar MakeCar(byte kind, VehicleDef def, Vector3 origin, float yaw)
    {
        var car = new ServerCar
        {
            Name = $"{def.Id}{_fleet.Count}", Id = _fleet.NextId, Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = origin, OriginYaw = yaw, FlightWater = _water, FlightBounds = _flightBounds(),
        };
        _world.AddChild(car);
        _fleet.Add(car);
        return car;
    }

    /// <summary>The AI driver: a bot-like player with no brain's say (<see cref="ServerClient.HarvesterDriver"/>), so it can be shot, scores kills and leaves a corpse like any seated player. Null when no id is free.</summary>
    ServerClient? AddDriver()
    {
        var id = _ids.Allocate(i => _ctx.Connections.Any(o => o.Id == i) || _ctx.Players.ContainsKey(i));
        if (id == 0) return null;
        var c = new ServerClient { Id = id, Name = "Bot Harvester", ConnectedAt = _ctx.Now, Brain = new BotBrain(BotTuning.Default, id), HarvesterDriver = true };
        var profile = _profiles.NewGuest();
        profile.Active = 0;
        _profiles.Attach(c, profile);
        _join(c);
        return c;
    }

    /// <summary>The job is over, one way or another: the event ends (won when it left the field), the cooldown starts, and a harvester that left is hidden.</summary>
    void Finish(HarvesterPhase end, string why)
    {
        if (_brain == null) return;
        if (end == HarvesterPhase.Stopped) _brain.Stop();
        SendState(force: true, end);
        var car = _car;
        if (_driver is { } d) _oldDrivers.Add(d);
        if (end == HarvesterPhase.Gone && car != null) Retire(car);
        _events.End(EventBook.Harvester, end == HarvesterPhase.Gone ? EventPhase.Won : EventPhase.Despawned, _ctx.Now);
        _director.Finished();
        GD.Print($"server: harvester {why}; no new one for {_cfg.CooldownS / 60f:F0} min");
        _brain = null; _driver = null; _field = null;
    }

    /// <summary>Hides the machine until the next job: out of its seat, destroyed with no blast and no return time (<see cref="ServerCar.Destroy"/>).</summary>
    void Retire(ServerCar car)
    {
        foreach (var o in _fleet.Occupants(car)) { if (o.HarvesterDriver) _oldDrivers.Add(o); _leaveSeat(o, true); }
        car.Seat.Clear();
        if (!car.Destroyed) car.Destroy(double.MaxValue);
    }

    /// <summary>"/event current end": the harvester leaves at once, as a win.</summary>
    public void End(ServerClient c)
    {
        if (!Running) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} ended the harvester event.");
        Finish(HarvesterPhase.Gone, $"ended by {c.Name}");
    }

    /// <summary>"/event current cancel": the harvester is removed, no win.</summary>
    public void Cancel(ServerClient c)
    {
        if (!Running) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} cancelled the harvester event.");
        if (_car is { } car) Retire(car);
        Finish(HarvesterPhase.Stopped, $"cancelled by {c.Name}");
    }

    /// <summary>A new round clears the world: the machine is hidden again (the car reset brought it back to its old field), the bales are gone, the first harvester waits its delay.</summary>
    public void RoundReset()
    {
        if (_car is { } car) Retire(car);
        if (_brain != null) Finish(HarvesterPhase.Stopped, "the round ended");
        foreach (var o in _oldDrivers.ToList()) if (_ctx.Players.ContainsKey(o.Id)) _leave(o);
        _oldDrivers.Clear();
        _reel.Reset(); _bales.Reset(); _cut.Reset();
        _director.Reset();
    }

    /// <summary>A joiner learns the job that runs, the cut so far and the static bales that burn or burnt.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_brain != null && _field != null) _ctx.SendEvent(c, EventKind.HarvesterState, HarvesterCodec.EncodeState(StateOf(_brain.Phase)));
        _cut.SendOnJoin(c);
        _bales.SendOnJoin(c);
    }

    HarvesterCodec.StateMsg StateOf(HarvesterPhase phase)
    {
        var p = _brain!.Progress;
        var driven = _driver != null && _car?.Seat.Occupant(0) == _driver.Id;
        return new HarvesterCodec.StateMsg(phase, _car?.Id ?? 0, (ushort)_fieldIndex, (byte)(p.Pass < 0 ? 255 : Math.Min(254, p.Pass)), p.Along, phase == HarvesterPhase.Cut && _brain.Progress.Pass >= 0, driven,
            _cfg.HeaderWidthM, _cfg.EdgeMarginM, _cfg.PassOverlapM);
    }

    void SendState(bool force, HarvesterPhase? phase = null)
    {
        if (_brain == null || (!force && _ctx.Now < _sendAt)) return;
        _sendAt = _ctx.Now + 0.5;
        _ctx.Broadcast(EventKind.HarvesterState, HarvesterCodec.EncodeState(StateOf(phase ?? _brain.Phase)));
    }

    // ---- the tick ----

    /// <summary>Before the players are simulated and the vehicles step: the automatic spawn, the AI's driving, the end of a job, the drivers that are no more.</summary>
    public void Tick(float dt)
    {
        for (var i = _oldDrivers.Count - 1; i >= 0; i--)
        {
            var o = _oldDrivers[i];
            if (!_ctx.Players.ContainsKey(o.Id)) { _oldDrivers.RemoveAt(i); continue; }
            if (_ctx.IsAlive(o.Id)) continue; // still aboard (the job was cancelled): retiring sent it out of the seat
            _leave(o); _oldDrivers.RemoveAt(i);
        }
        if (_brain == null)
        {
            var humans = _ctx.Players.Values.Any(o => !o.IsBot);
            if (_ctx.Match.Phase == MatchPhase.Running && _director.Tick(dt, humans, _events.Current != null)) TryStart(forced: false, out _);
            return;
        }
        var car = _car!; var driver = _driver!;
        if (_ctx.Match.Phase != MatchPhase.Running || !_ctx.Players.Values.Any(o => !o.IsBot)) { CancelQuiet(); return; } // the round ended or everyone left
        if (car.Destroyed) { Finish(HarvesterPhase.Stopped, "its machine was destroyed"); return; }
        if (!_ctx.IsAlive(driver.Id) || car.Seat.Occupant(0) != driver.Id)
        {
            Finish(HarvesterPhase.Stopped, "its driver was killed");
            return;
        }
        var cmd = _brain.Tick(dt, new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw, car.State.Speed);
        driver.Throttle = cmd.Throttle; driver.Steer = cmd.Steer; driver.Handbrake = false;
        car.Lights = false;
        if (_brain.Phase == HarvesterPhase.Gone) { Finish(HarvesterPhase.Gone, "left the field"); return; }
        SendState(force: false);
    }

    void CancelQuiet()
    {
        if (_car is { } car) Retire(car);
        Finish(HarvesterPhase.Stopped, "no round or no player");
    }

    /// <summary>After the vehicles moved: what the machine runs over and what its reel draws in, the cut and the crushed crops, the corpses on their way into it, the bales' fire.</summary>
    public void AfterVehicles(float dt)
    {
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Def.Id != HarvesterRules.VehicleId || car.Seat.Occupant(0) == 0) continue; // any harvester with somebody at the wheel: the event's, a stolen one, a spawned one
            if (_ctx.Match.Phase == MatchPhase.Running) _reel.Contacts(car);
            _cut.Cut(car);
        }
        _cut.Crush();
        foreach (var car in _fleet.Cars) if (car.Def.Id == HarvesterRules.VehicleId && (car.Destroyed || car.Seat.Occupant(0) == 0)) _cut.Forget(car.Id);
        _cut.FlushSwaths();
        _reel.Step(dt);
        _bales.FollowMade();
        _bales.Step(dt);
    }

    // ---- what the shots and the deaths tell it ----

    /// <summary>A shot along <paramref name="from"/> + t * <paramref name="dir"/> for <paramref name="length"/> m: any bale it passes catches fire (#331).</summary>
    public void Shot(NVec3 from, NVec3 dir, float length) => _bales.Shot(from, dir, length);

    /// <summary>A kill event (<c>PlayerKilled</c>), before the corpse: a body the harvester killed is not thrown.</summary>
    public void OnKill(PlayerKilled k) => _reel.OnKill(k);

    /// <summary>A corpse was made: a victim of the harvester goes into the machine.</summary>
    public void OnCorpse(ushort victim, ushort corpseId, Corpse corpse) => _reel.OnCorpse(victim, corpseId, corpse);

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);
}

using Godot;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The Scud truck on the server (GLOSSARY "Scud truck", "Launcher", "Scud missile", "Scud blast"). Each truck (a vehicle whose type has a vehicles.json <c>launcher</c> block) has a
/// <see cref="ScudLauncher"/>, made the first time it is looked at: the machine owns the rocket, the operator's lock, the crew's confirmations and the rack's clock; this file feeds it the
/// crew and the truck's place, brakes the truck while the rack moves, spawns the missile when the ignition ends and works out the blast.
/// <para>
/// What the others read: <see cref="SpawnScudTruck"/> (the event that brings the truck; also <c>/scud spawn</c>), <see cref="TryReloadScud"/> (the rocket cart's parked beside it: Empty to Reloading
/// to Stowed), <see cref="ReloadScudNow"/> (the admin's instant reload) and <see cref="ScudBlast"/> (the blast, for the missile's impact and for a loaded rocket cart that is shot).
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>scud.json: the launcher's timings, the targeting screen, the missile's flight and the blast. The host replaces the defaults with the file.</summary>
    public ScudFile Scud = ScudFile.Default;

    /// <summary>One truck's launcher and the operator's camera as last sent to the crew.</summary>
    sealed class ScudRig
    {
        public required ScudLauncher Launcher;
        public ScudStateMsg? Sent;
        public float ViewX, ViewZ, ViewZoom = 1f;
        /// <summary>The player who held the key to start the loading from a cart (told how it went even when on foot).</summary>
        public ushort Starter;
        public bool ViewDirty;
        public double ViewSentAt;
    }

    readonly Dictionary<ushort, ScudRig> _scudRigs = new();
    int _scudLaunches, _scudBlasts, _scudKills;

    /// <summary>The kills of a Scud blast are the placer's ordinary kills with this weapon byte.</summary>
    const byte ScudWeapon = SpecialWeapons.Scud;

    /// <summary>How hard a blast throws a body that dies in it, metres a second at the blast's centre, falling off to nothing at the damage radius.</summary>
    const float ScudKnockMps = 28f;

    void InitScud() => ScudMissileRules.Register(WorldEntityRegistry.Shared);

    // ---- the trucks ----

    /// <summary>The launcher of a truck, made on first use; null for a vehicle that is not a launcher.</summary>
    ScudRig? ScudRigOf(ServerCar car)
    {
        if (car.Def.Launcher == null) return null;
        if (_scudRigs.TryGetValue(car.Id, out var rig)) return rig;
        rig = new ScudRig { Launcher = new ScudLauncher(Scud.Launcher, Scud.Reload) };
        _scudRigs[car.Id] = rig;
        var id = car.Id;
        rig.Launcher.ProposalDropped += why => ScudNotifyCrew(id, ScudCodec.NoticeOf(why));
        rig.Launcher.LoadDropped += why => { ScudNotifyCrew(id, ScudCodec.NoticeOf(why)); ScudNotifyStarter(rig, ScudCodec.NoticeOf(why)); };
        return rig;
    }

    /// <summary>The truck's seats and who sits in them, the operator's seat named by the type.</summary>
    ScudCrew ScudCrewOf(ServerCar car)
    {
        var seats = new ushort[car.Seat.Count];
        for (var i = 0; i < seats.Length; i++) seats[i] = car.Seat.Occupant(i);
        return new ScudCrew(seats, car.Def.Launcher!.OperatorSeat);
    }

    ServerCar? ScudCarById(ushort id) => _fleet.Find(id);

    /// <summary>
    /// A launcher truck standing at <paramref name="position"/> (the ground there is found), turned to <paramref name="yaw"/>: the first vehicle type with a launcher block, one more vehicle record. Null
    /// when the data has no such type. It is never dealt at the match start (spawn_count 0) and never returns once destroyed (<see cref="VehicleDef.Returns"/>); a new round retires it.
    /// </summary>
    public ServerCar? SpawnScudTruck(NVec3 position, float yaw)
    {
        if (Vehicles == null) return null;
        var kind = Vehicles.Vehicles.FindIndex(v => v.Launcher != null);
        if (kind < 0) { GD.PrintErr("server: no vehicle type has a launcher block, no Scud truck spawned"); return null; }
        var def = Vehicles.Vehicles[kind];
        var ground = Map?.GroundHeight(position.X, position.Z) ?? float.NaN;
        if (float.IsNaN(ground)) ground = position.Y;
        var space = World.GetWorld3D().DirectSpaceState;
        var y = ServerCar.RestHeight(space, new Vector3(position.X, ground, position.Z), yaw, def.HalfWidth, def.HalfLength, ground) ?? ground;
        var car = new ServerCar
        {
            Name = $"{def.Id}{_fleet.Count}", Id = _fleet.NextId, Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(position.X, y + 0.02f, position.Z), OriginYaw = yaw,
            FlightWater = Water, FlightBounds = MapFlightBounds,
        };
        World.AddChild(car);
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
        if (truck.Destroyed || ScudRigOf(truck) is not { } rig || !rig.Launcher.BeginReload()) return false;
        GD.Print($"server: Scud truck {truck.Id} is reloading");
        return true;
    }

    /// <summary>An admin's instant reload (Empty, or a reload under way, to Stowed at once). False when there is nothing to reload.</summary>
    public bool ReloadScudNow(ServerCar truck)
    {
        if (truck.Destroyed || ScudRigOf(truck) is not { } rig || !rig.Launcher.ReloadNow()) return false;
        GD.Print($"server: Scud truck {truck.Id} reloaded at once");
        return true;
    }

    /// <summary>The launcher's state of a truck, or null when it is not a launcher (for the rocket cart and the tests).</summary>
    public ScudLauncherState? ScudStateOf(ServerCar truck) => ScudRigOf(truck)?.Launcher.State;

    // ---- the tick ----

    /// <summary>
    /// Once a tick, before the vehicles are stepped: every launcher truck's machine, its brakes while the rack moves, the missile when the ignition ends, and what changed sent to the clients.
    /// </summary>
    void StepScud(float dt)
    {
        if (Vehicles == null) return;
        foreach (var car in _fleet.Cars)
        {
            if (car.Def.Launcher == null) continue;
            if (car.Destroyed)
            {
                car.Immobilised = false;
                if (_scudRigs.Remove(car.Id)) BroadcastEvent(EventKind.ScudState, ScudCodec.Encode(new ScudStateMsg(car.Id, ScudLauncherState.Stowed, 0, float.PositiveInfinity, 0, 0, null, 0, 0)));
                continue;
            }
            var rig = ScudRigOf(car)!;
            var crew = ScudCrewOf(car);
            var order = rig.Launcher.Tick(dt, crew, N(car.GlobalPosition), CartSenseOf(car, rig));
            if (order.Launch) LaunchScud(car, order);
            if (order.TakeCart != 0) TakeRocketFromCart(order.TakeCart);
            car.Immobilised = rig.Launcher.Immobilised; // a driven truck: every drive step is held (ServerCar.StepDrive), as its driver's client predicts it
            if (rig.Launcher.Immobilised && _fleet.OccupantAt(car, 0) is { } driver)
            {
                driver.Throttle = 0; driver.Steer = 0; driver.Handbrake = true; // brakes on, the driver's input ignored while the rack moves
                car.Wake();
            }
            SendScudState(car, rig);
            SendScudView(car, rig, crew);
        }
    }

    ScudStateMsg ScudStateMsgOf(ServerCar car, ScudRig rig)
    {
        var l = rig.Launcher;
        return new ScudStateMsg(car.Id, l.State, l.Elapsed, l.Left, l.RackFrom, l.Operator, l.Lock, (byte)l.Confirmed, (byte)l.Need);
    }

    /// <summary>Sends the launcher's state to everyone when the state, the lock, the operator or the confirmations changed (the clients count the clocks on from there).</summary>
    void SendScudState(ServerCar car, ScudRig rig)
    {
        var now = ScudStateMsgOf(car, rig);
        var was = rig.Sent;
        if (was != null && was.State == now.State && was.Lock == now.Lock && was.Operator == now.Operator && was.Confirmed == now.Confirmed && was.Need == now.Need) return;
        rig.Sent = now;
        if (was == null || was.State != now.State) rig.ViewDirty = true; // the feed starts with the screen
        BroadcastEvent(EventKind.ScudState, ScudCodec.Encode(now));
    }

    /// <summary>The operator's camera to the rest of the crew, at <see cref="ScudTargetingData.ViewHz"/> while it changes and the screen is open.</summary>
    void SendScudView(ServerCar car, ScudRig rig, ScudCrew crew)
    {
        if (!rig.ViewDirty || !ScudRules.FeedLive(rig.Launcher.State) || _elapsed - rig.ViewSentAt < 1.0 / Scud.Targeting.ViewHz) return;
        rig.ViewDirty = false; rig.ViewSentAt = _elapsed;
        var msg = new EventMsg((byte)EventKind.ScudView, ScudCodec.Encode(new ScudViewMsg(car.Id, rig.ViewX, rig.ViewZ, rig.ViewZoom)));
        for (var s = 0; s < crew.Seats.Count; s++)
            if (s != crew.OperatorSeat && _fleet.OccupantAt(car, s) is { } o) Send(o, msg);
    }

    /// <summary>A joiner learns every launcher that is not stowed and idle (the rack, the proposal) and the operator's camera while a screen is open.</summary>
    void SendScudOnJoin(ServerClient c)
    {
        foreach (var (id, rig) in _scudRigs)
        {
            if (ScudCarById(id) is not { } car) continue;
            if (rig.Launcher.State != ScudLauncherState.Stowed) Send(c, new EventMsg((byte)EventKind.ScudState, ScudCodec.Encode(ScudStateMsgOf(car, rig))));
        }
    }

    void ScudNotifyCrew(ushort carId, ScudNoticeCode code)
    {
        if (ScudCarById(carId) is not { } car) return;
        var msg = new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(code));
        foreach (var o in _fleet.Occupants(car)) Send(o, msg);
    }

    /// <summary>A new round: the launchers start over, and a truck that never returns is retired instead of reset (hidden, out of its seats).</summary>
    void ScudRoundReset()
    {
        ScudEventRoundReset(); // the Scud event is over with the round (no reward); the loop below retires its truck and carts
        _scudRigs.Clear();
        foreach (var car in _fleet.Cars)
        {
            if (car.Def.Returns || car.Destroyed) continue;
            car.Seat.Clear();
            car.Destroy(double.MaxValue);
        }
    }

    // ---- loading a rocket from a cart ----

    /// <summary>The box of a vehicle on the ground plane, as the reload rules measure it.</summary>
    static BodyBox BoxOf(ServerCar car) => new(car.GlobalPosition.X, car.GlobalPosition.Z, car.HalfWidth, car.HalfLength, car.State.Yaw);

    /// <summary>Every live rocket cart, as the rules want it.</summary>
    IEnumerable<ScudCartCandidate> CartCandidates() =>
        _fleet.Cars.Where(c => c.Cargo != null && !c.Retired).Select(c => new ScudCartCandidate(c.Id, BoxOf(c), c.Cargo!.Loaded && !c.Life.Doomed, !c.Destroyed));

    /// <summary>What the launcher is told about the cart it is loading from this tick: alive, still loaded, not doomed and in reach (null when it is loading from none).</summary>
    ScudCartSense? CartSenseOf(ServerCar truck, ScudRig rig)
    {
        if (rig.Launcher.Cart == 0) return null;
        var cart = ScudCarById(rig.Launcher.Cart);
        if (cart == null) return new ScudCartSense(false, default);
        var ok = ScudReloadRules.NearestCart(BoxOf(truck), CartCandidates().Where(k => k.Id == cart.Id), (float)Scud.Reload.CartReachM + 1f) != null; // a metre of slack: the cart settling is not leaving
        return new ScudCartSense(ok, N(cart.GlobalPosition));
    }

    /// <summary>The transfer point: the rocket leaves the cart (it is empty from now on, and weighs and breaks like an empty one).</summary>
    void TakeRocketFromCart(ushort cartId)
    {
        if (ScudCarById(cartId) is { } cart && SetRocketCartLoaded(cart, false)) GD.Print($"server: rocket cart {cartId} unloaded onto a Scud truck");
    }

    /// <summary>
    /// The truck a player may load from a cart now, and the cart: the truck they ride (any seat), else the nearest launcher truck within <see cref="ScudReloadData.PlayerReachM"/> of the player on foot (not while loot lies in
    /// reach: that key is the loot's); the rack must be empty and a loaded, live cart within <see cref="ScudReloadData.CartReachM"/> of the truck. Null when nothing is on offer.
    /// </summary>
    (ServerCar Truck, ServerCar Cart)? ScudReloadOffer(ServerClient c)
    {
        if (_scudRigs.Count == 0) return null;
        var p = c.Body.GlobalPosition;
        IEnumerable<ServerCar> trucks;
        if (c.Car != null) trucks = c.Car.Def.Launcher != null ? new[] { c.Car } : Array.Empty<ServerCar>();
        else if (_thieves.LootInReach(c) != null) return null;
        else trucks = _fleet.Cars.Where(t => t.Def.Launcher != null);
        foreach (var truck in trucks)
        {
            if (truck.Destroyed || truck.Retired || !_scudRigs.TryGetValue(truck.Id, out var rig)) continue;
            var box = BoxOf(truck);
            if (!ScudReloadRules.MayStart(rig.Launcher.State, c.Car == truck, box, p.X, p.Z, Scud.Reload)) continue;
            if (ScudReloadRules.NearestCart(box, CartCandidates(), (float)Scud.Reload.CartReachM) is { } near && ScudCarById(near.Id) is { } cart) return (truck, cart);
        }
        return null;
    }

    /// <summary>
    /// The interact key of a player who may be beside a loading offer (<see cref="ScudReloadOffer"/>): with one standing, a press waits to be a tap (true here: the key does what it always does, get in or out of the
    /// truck) or a hold (the loading starts, false here); with none the key is the plain press. Returns whether the key's usual action runs now.
    /// </summary>
    bool ScudUseKey(ServerClient c, bool useDown, bool useEdge, float dt)
    {
        var offer = ScudReloadOffer(c);
        if (offer is not { } o) { c.ReloadKey.Disarm(useDown); return useEdge; }
        switch (c.ReloadKey.Tick(useDown, dt, (float)Scud.Reload.HoldSeconds))
        {
            case ScudKeyAction.Hold: StartLoad(c, o.Truck, o.Cart); return false;
            case ScudKeyAction.Tap: return true;
            default: return false;
        }
    }

    void StartLoad(ServerClient c, ServerCar truck, ServerCar cart)
    {
        if (ScudRigOf(truck) is not { } rig || !rig.Launcher.BeginLoad(cart.Id, N(truck.GlobalPosition), N(cart.GlobalPosition))) return;
        rig.Starter = c.Id;
        GD.Print($"server: {c.Name} #{c.Id} began loading Scud truck {truck.Id} from rocket cart {cart.Id}");
        ScudNotifyCrew(truck.Id, ScudNoticeCode.LoadStarted);
        if (c.Car != truck) Send(c, new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(ScudNoticeCode.LoadStarted)));
    }

    void ScudNotifyStarter(ScudRig rig, ScudNoticeCode code)
    {
        if (rig.Starter != 0 && _byId.TryGetValue(rig.Starter, out var c) && c.Car == null) Send(c, new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(code))); // those aboard got it with the crew
        rig.Starter = 0;
    }

    // ---- the crew's commands ----

    /// <summary>A crew member's command (<see cref="EventKind.ScudCommand"/>). Anything invalid is ignored; a refused one answers with a notice.</summary>
    void OnScudCommand(ServerClient c, byte[] payload)
    {
        if (ScudCodec.DecodeCommand(payload) is not { } m) return;
        if (c.Car is not { Destroyed: false } car || ScudRigOf(car) is not { } rig || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        var crew = ScudCrewOf(car);
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
                    rig.ViewX = m.X; rig.ViewZ = m.Z; rig.ViewZoom = Math.Clamp(m.Zoom, (float)Scud.Targeting.ZoomMin, (float)Scud.Targeting.ZoomMax);
                    rig.ViewDirty = true;
                }
                return; // a camera that does not apply is not worth a notice
            case ScudCmd.Lock:
            {
                var ground = Map?.GroundHeight(m.X, m.Z) ?? 0f;
                var onMap = Map == null || !float.IsNaN(ground);
                answer = l.PlaceLock(crew, c.Id, new NVec3(m.X, onMap ? ground : 0f, m.Z), N(car.GlobalPosition), onMap);
                if (answer == ScudAnswer.Ok) GD.Print($"server: {c.Name} #{c.Id} locked ({m.X:F0},{ground:F0},{m.Z:F0}) with Scud truck {car.Id}, {ScudRules.Range(N(car.GlobalPosition), l.Lock!.Value):F0} m");
                break;
            }
            default: return;
        }
        if (ScudCodec.NoticeOf(answer) is { } code) Send(c, new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(code)));
    }

    // ---- the missile ----

    /// <summary>The rack's ignition ran out: the missile enters the world at the rack, on a flight planned now onto the locked point (<see cref="ScudFlightPlan"/>).</summary>
    void LaunchScud(ServerCar car, ScudTick order)
    {
        var from = VehicleSeat.SeatPosition(N(car.GlobalPosition), car.State.Yaw, car.Def.Launcher!.Muzzle);
        var plan = new ScudFlightPlan(from, order.Target, Scud.Missile, (float)Scud.Launcher.MinRangeM);
        var missile = new ScudMissileEntity(order.Placer, plan, m => ScudBlast(m.Plan.Target, m.Owner, m.Id));
        if (_match.Entities.Spawn(missile) is not { } id)
        {
            GD.PrintErr("server: no room for the Scud missile (entity cap)");
            return;
        }
        missile.Id = id;
        _scudLaunches++;
        BroadcastEvent(EventKind.ScudLaunch, ScudCodec.Encode(new ScudLaunchMsg(id, car.Id, order.Placer, from, order.Target, plan.Seconds)));
        GD.Print($"server: Scud missile {id} left truck {car.Id} for ({order.Target.X:F0},{order.Target.Y:F0},{order.Target.Z:F0}) placed by #{order.Placer}, {plan.Seconds:F1} s, apex {plan.Apex.Y:F0} m");
    }

    /// <summary>
    /// THE Scud blast: the whole of what a Scud missile's impact does, at <paramref name="position"/> (the ground), credited to <paramref name="attackerId"/> (the player who placed the lock;
    /// the rocket cart's shooter when a loaded cart is shot). Everything inside the lethal radius dies, in a vehicle or not and spawn protected or not, except what a blast shelter covers
    /// (<see cref="MapAreas.Shelters"/>, the same rule as every blast); beyond it the damage falls off to the damage radius and then walls, a hull and shelters work as for any blast
    /// (<see cref="Splash.Share"/>). Vehicles whose hull is in the destroy radius go up, those beyond it take hit points. Every glass pane within the glass radius shatters, the clients draw the
    /// biggest blast wave and dust the game has, shake the camera map-wide and leave a scorch. Kills are ordinary kills (friendly fire as the match mode has it). Aircraft are not touched.
    /// </summary>
    /// <param name="missileId">The world entity that flew here (0 for a blast with none); the clients end its picture with it.</param>
    public void ScudBlast(NVec3 position, ushort attackerId, ushort missileId = 0)
    {
        var b = Scud.Blast;
        _scudBlasts++;
        var at = new Vector3(position.X, position.Y, position.Z);
        BroadcastEvent(EventKind.ScudImpact, ScudCodec.Encode(new ScudImpactMsg(missileId, attackerId, position)));
        BlastGlass(at + Vector3.Up * 1f, (float)b.GlassRadiusM, 1_000_000f); // every pane in reach shatters: the centre damage is far above any pane's hit points

        var killed = 0; var hurt = 0;
        var targets = _targets.Live();
        foreach (var t in targets)
        {
            var centre = HitMath.Centre(t);
            var dist = HitMath.DistanceToBody(position, t);
            var hit = ScudBlastRules.PlayerHit(b, dist);
            if (!hit.Hurts || !_areas.Shelters.Reaches(position, centre)) continue; // a bunker room or the church shelters those inside
            var away = centre - position; away.Y = MathF.Max(away.Y, 2f);
            var push = away == NVec3.Zero ? NVec3.UnitY : NVec3.Normalize(away);
            var k = push * (ScudKnockMps * Math.Clamp(1f - dist / (float)b.DamageRadiusM, 0.1f, 1f));
            if (hit.Lethal)
            {
                _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k)); // the body is thrown
                if (t.Id == BigfootRules.TargetId) _bigfoot.Hit(attackerId, ScudWeapon); // the Bigfoot takes it as an explosive hit, not as a kill (issue #264)
                else
                {
                    _hunts.Pending = new WildHit(ScudWeapon, KillCause.Standard, null, null); // a wild animal in the blast is a hunt with the Scud
                    var res = _match.ApplyDamage(t.Id, attackerId, ScudWeapon, hit.Damage, ignoreProtection: true);
                    _hunts.Pending = null;
                    _deathPush.Clear(t.Id, res);
                    if (res == DamageResult.Killed) killed++;
                }
                continue;
            }
            var share = _splash.Share(position, t); // walls, a hull over a covered seat
            if (share <= 0) continue;
            var dmg = hit.Damage * share;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k));
            var dealt = Deal(attackerId, ScudWeapon, t.Id, dmg, false, false);
            _deathPush.Clear(t.Id, dealt);
            if (dealt == DamageResult.Ignored) continue; // spawn protected
            if (dealt == DamageResult.Killed) killed++; else hurt++;
            if (dealt != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v)) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z) * 0.3f);
        }
        _targets.Return(targets);

        var destroyed = 0;
        foreach (var car in _fleet.Cars.ToList())
        {
            if (car.Destroyed) continue;
            var dist = HullDistanceNow(car, position);
            var dmg = ScudBlastRules.VehicleDamage(b, dist, car.Hp);
            if (dmg <= 0) continue;
            // the ones nearest go first, the wave reaching the farther ones a moment later
            var delay = dist <= b.VehicleDestroyRadiusM ? MathF.Min(1.5f, ScudBlastRules.ArrivalSeconds(b, dist)) : 0f;
            if (car.Cargo?.Detonates(car.Def) == true) delay = ScudBlastRules.ChainDelay(b, dist); // a loaded rocket cart goes up in its turn, once (a doomed vehicle keeps its first date)
            DamageVehicle(car, dmg, attackerId, ScudWeapon, delay);
            if (dist <= b.VehicleDestroyRadiusM) destroyed++;
        }
        _scudKills += killed;
        GD.Print($"server: Scud blast at ({position.X:F0},{position.Y:F0},{position.Z:F0}) by #{attackerId}: {killed} killed, {hurt} hurt, {destroyed} vehicles destroyed");
    }

    // ---- the admin's command ----

    /// <summary>"/scud spawn | reload | blast | state": the test commands (admins only, see <see cref="ChatCommands"/>).</summary>
    void ChatScud(ServerClient c, IReadOnlyList<string> args)
    {
        switch (args[0])
        {
            case "spawn":
            {
                if (c.Car != null) { ChatTo(c, ChatLineKind.Error, "Leave the vehicle first."); return; }
                var def = Vehicles?.Vehicles.FirstOrDefault(v => v.Launcher != null);
                if (def == null) { ChatTo(c, ChatLineKind.Error, "There is no Scud truck in vehicles.json."); return; }
                var yaw = c.Body.Yaw;
                var p = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * (def.HalfLength + 5f);
                if (SpawnScudTruck(N(p), yaw) is { } car) ChatTo(c, ChatLineKind.Info, $"A Scud truck stands in front of you (vehicle {car.Id}).");
                else ChatTo(c, ChatLineKind.Error, "The Scud truck could not be spawned.");
                break;
            }
            case "reload":
            {
                var truck = ScudTruckNear(c);
                if (truck == null) { ChatTo(c, ChatLineKind.Error, "No Scud truck within 40 m, and you do not ride one."); return; }
                var reloaded = ReloadScudNow(truck);
                var state = ScudStateOf(truck);
                if (reloaded || state == ScudLauncherState.Stowed) ChatTo(c, ChatLineKind.Info, "The Scud truck is loaded.");
                else ChatTo(c, ChatLineKind.Error, $"Nothing to reload: the launcher is {state} (a rack that is up has to lower first).");
                break;
            }
            case "blast":
            {
                var yaw = c.Body.Yaw;
                var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * 200f;
                if (Query.Ground(MoverKind.Player, at, 80f, 300f) is { } ground) at = ground.Position;
                ScudBlast(N(at), c.Id);
                ChatTo(c, ChatLineKind.Info, "A Scud blast 200 m in front of you.");
                break;
            }
            case "state":
            {
                var truck = ScudTruckNear(c);
                ChatTo(c, ChatLineKind.Info, truck == null ? "No Scud truck within 40 m, and you do not ride one." : $"Scud truck {truck.Id}: launcher {ScudStateOf(truck)}.");
                break;
            }
        }
    }

    /// <summary>The Scud truck the player rides, else the nearest within 40 m.</summary>
    ServerCar? ScudTruckNear(ServerClient c)
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
}

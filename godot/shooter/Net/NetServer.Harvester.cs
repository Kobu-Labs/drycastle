using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The harvester event on the server (#331, GLOSSARY "Event"). Now and then, or on <c>/event harvester</c>, a harvester (vehicles.json <c>harvester</c>, an ordinary <see cref="ServerCar"/> with an
/// open cab) appears on a crop field near the players (<see cref="CropFields"/>, from the ground patches of patches.json) and drives it in passes (<see cref="FieldPlanner"/>, <see cref="HarvesterBrain"/>),
/// then leaves along the edge. Its driver is a seated bot (<see cref="Client.HarvesterDriver"/>): shot dead it stays seated and rides (ADR 0026) and anyone may take the seat once it rests.
/// Players the machine runs over or the reel draws in die (credited to the driver: the kill feed and the score); the corpse is drawn in at the front and a bale leaves the unloader with its Items
/// (<see cref="MachineCorpse"/>, a corpse owned by <see cref="HarvesterRules.BaleOwner"/>). A shot sets any bale alight, the field's own too (<see cref="HayBale"/>); a burning bale ignites players beside it,
/// credited to nobody (<see cref="BurnSet.HazardSource"/>). The cut itself is cosmetic and drawn by each client from the compact job state (<see cref="HarvesterCodec.StateMsg"/>), never per plant.
/// The rules are pure and tested in <c>ShooterCore/Events</c>; this file is the world they stand in. One <see cref="ServerCar"/> serves every job of a server run: it is hidden (destroyed, silently) between them.
/// </summary>
public partial class NetServer
{
    /// <summary>Every number of the event; the host may replace it.</summary>
    public HarvesterConfig Harvester = new();

    HarvesterDirector? _harvDirector;
    List<CropField>? _cropFields;

    ServerCar? _harvCar;
    Client? _harvDriver;
    HarvesterBrain? _harvBrain;
    CropField? _harvField;
    int _harvFieldIndex;
    double _harvSendAt;
    /// <summary>Drivers that lost the job (dead, or the job ended): taken out of the match once their corpse exists.</summary>
    readonly List<Client> _harvOldDrivers = new();
    /// <summary>Players the harvester killed this tick, from the kill event to the corpse: their bodies go into the machine instead of being thrown.</summary>
    readonly HashSet<ushort> _harvVictims = new();
    readonly List<(MachineCorpse Machine, NVec3 From)> _machineCorpses = new();
    /// <summary>The Items of the victims inside the machine, by their corpse id, until the bale leaves the unloader.</summary>
    readonly Dictionary<ushort, List<ItemStack>> _machineItems = new();
    HayBale[]? _staticBales;
    readonly Dictionary<ushort, HayBale> _madeBales = new();
    readonly List<(ushort Id, NVec3 Feet)> _baleFolk = new();

    /// <summary>Called once from <c>_Ready</c> with the other events: the director and the handler table's kind.</summary>
    void InitHarvester() => _harvDirector = new HarvesterDirector(Harvester, _rng);

    /// <summary>The crop fields of the map, once (the ground patches of crop types; nothing without a map).</summary>
    List<CropField> CropFieldList => _cropFields ??= Map == null
        ? new List<CropField>()
        : CropFields.From(Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())));

    bool HarvesterRunning => _harvBrain != null;

    // ---- the lifecycle ----

    /// <summary>"/event harvester": one now, ignoring the cooldown; refuses while one is out. The answer says where it appeared.</summary>
    void StartHarvester(Client admin)
    {
        if (!TryStartHarvester(forced: true, out var said)) { ChatTo(admin, ChatLineKind.Error, said); return; }
        ChatTo(admin, ChatLineKind.Info, said);
    }

    /// <summary>
    /// Starts a job: a field (near the players; for the command also the nearest one), its plan, the machine and its driver at the field's entry. False with the reason; the automatic spawn just waits for its next roll.
    /// <paramref name="said"/> is the command's answer: field id and the position in map metres.
    /// </summary>
    bool TryStartHarvester(bool forced, out string said)
    {
        said = "";
        if (Vehicles == null || Vehicles.Vehicles.FindIndex(v => v.Id == HarvesterRules.VehicleId) is var kind && kind < 0) { said = "There is no harvester in vehicles.json."; return false; }
        if (_harvDirector!.Active)
        {
            said = _harvField != null && _harvCar != null ? HarvesterRules.RefusedRunning(_harvField.Id, N(_harvCar.GlobalPosition)) : _harvDirector.WhyNotForced()!;
            return false;
        }
        if (_events.WhyNotStart(EventBook.Harvester) is { } busy) { said = busy; return false; }
        if (_match.Phase != MatchPhase.Running) { said = "No round is running."; return false; }
        if (_harvCar is { Destroyed: false } old && old.Seat.Occupied) { said = HarvesterRules.RefusedInUse; return false; }

        var fields = CropFieldList;
        var players = _byId.Values.Where(o => !o.IsBot && _match.Get(o.Id)?.Life == PlayerLife.Alive).Select(o => new NVec2(o.Body.GlobalPosition.X, o.Body.GlobalPosition.Z)).ToList();
        CropField? field = null; HarvestPlan? plan = null;
        var pool = fields.ToList();
        for (var tries = 0; tries < 8 && plan == null; tries++)
        {
            field = CropFields.Pick(pool, players, Harvester.NearPlayerM, Harvester.MinFieldAreaM2, _rng, orNearest: forced);
            if (field == null) break;
            plan = FieldPlanner.Plan(field.Ring, Harvester);
            if (plan == null) pool.Remove(field); // a sliver that fits no pass: another field
        }
        if (field == null || plan == null) { said = forced ? HarvesterRules.RefusedNoField : ""; return false; }

        var ground = Map?.GroundHeight(plan.Entry.X, plan.Entry.Y) ?? float.NaN;
        if (float.IsNaN(ground)) ground = Map?.GroundHeightOrMean(plan.Entry.X, plan.Entry.Y) ?? 0f;
        var space = World.GetWorld3D().DirectSpaceState;
        var def = Vehicles.Vehicles[kind];
        var y = ServerCar.RestHeight(space, new Vector3(plan.Entry.X, ground, plan.Entry.Y), plan.EntryYaw, def.HalfWidth, def.HalfLength, ground) ?? ground;
        var origin = new Vector3(plan.Entry.X, y + 0.02f, plan.Entry.Y);

        var car = _harvCar ?? MakeHarvesterCar((byte)kind, def, origin, plan.EntryYaw);
        car.OriginPos = origin; car.OriginYaw = plan.EntryYaw;
        car.Reset(); // back from where it was hidden: repaired, at the entry
        car.Seat.Clear(); car.Seat.Unblock(0); // a corpse that rode the last job's seat is not in the way
        if (AddHarvesterDriver() is not { } driver) { said = "No player id is free for the harvester's driver."; return false; }
        if (!car.Seat.TryEnter(driver.Id, 0)) { Leave(driver); said = "The harvester's driver could not take the seat."; return false; }
        _harvCar = car; _harvDriver = driver;
        driver.Car = car; driver.CarSeat = 0; driver.Throttle = driver.Steer = 0; driver.Handbrake = false; driver.Body.InVehicle = true;
        driver.Body.Teleport(SeatPos(car, 0));
        _match.SetPosition(driver.Id, N(driver.Body.GlobalPosition));
        _harvBrain = new HarvesterBrain(plan, Harvester);
        _harvField = field; _harvFieldIndex = CropFieldList.IndexOf(field);
        _harvDirector.Started();
        _events.Start(EventBook.Harvester, _elapsed);
        _harvSendAt = 0;
        var at = N(car.GlobalPosition);
        said = HarvesterRules.SpawnedText(field.Id, at);
        ChatToAll(ChatLineKind.System, "", $"A harvester has started on field {field.Id}. Keep clear of the reel.");
        GD.Print($"server: harvester started on {field.Id} ({field.Type}, {field.Area:F0} m2), {plan.Passes.Count} passes of {plan.TotalCutM:F0} m, at ({at.X:F1},{at.Y:F1},{at.Z:F1}), driver #{driver.Id}");
        return true;
    }

    ServerCar MakeHarvesterCar(byte kind, VehicleDef def, Vector3 origin, float yaw)
    {
        var car = new ServerCar
        {
            Name = $"{def.Id}{_cars.Count}", Id = (ushort)(VehicleRecord.FirstId + _cars.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = origin, OriginYaw = yaw, FlightWater = Water, FlightBounds = MapFlightBounds,
        };
        World.AddChild(car);
        _cars.Add(car);
        return car;
    }

    /// <summary>The AI driver: a bot-like player with no brain's say (<see cref="Client.HarvesterDriver"/>), so it can be shot, scores kills and leaves a corpse like any seated player. Null when no id is free.</summary>
    Client? AddHarvesterDriver()
    {
        var id = _ids.Allocate(i => _clients.Values.Any(o => o.Id == i) || _byId.ContainsKey(i));
        if (id == 0) return null;
        var c = new Client { Id = id, Name = "Bot Harvester", ConnectedAt = _elapsed, Brain = new BotBrain(BotTuning.Default, id), HarvesterDriver = true };
        var profile = GuestProfile();
        profile.Active = 0;
        AttachProfile(c, profile);
        Join(c);
        return c;
    }

    /// <summary>The job is over, one way or another: the event ends (won when it left the field), the cooldown starts, and a harvester that left is hidden.</summary>
    void FinishHarvester(HarvesterPhase end, string why)
    {
        if (_harvBrain == null) return;
        if (end == HarvesterPhase.Stopped) _harvBrain.Stop();
        SendHarvesterState(force: true, end);
        var car = _harvCar;
        if (_harvDriver is { } d) _harvOldDrivers.Add(d);
        if (end == HarvesterPhase.Gone && car != null) RetireHarvester(car);
        _events.End(EventBook.Harvester, end == HarvesterPhase.Gone ? EventPhase.Won : EventPhase.Despawned, _elapsed);
        _harvDirector!.Finished();
        GD.Print($"server: harvester {why}; no new one for {Harvester.CooldownS / 60f:F0} min");
        _harvBrain = null; _harvDriver = null; _harvField = null;
    }

    /// <summary>Hides the machine until the next job: out of its seat, destroyed with no blast and no return time (<see cref="ServerCar.Destroy"/>).</summary>
    void RetireHarvester(ServerCar car)
    {
        foreach (var o in Occupants(car)) { if (o.HarvesterDriver) _harvOldDrivers.Add(o); LeaveSeat(o, true); }
        car.Seat.Clear();
        if (!car.Destroyed) car.Destroy(double.MaxValue);
    }

    /// <summary>"/event current end": the harvester leaves at once, as a win.</summary>
    void EndHarvester(Client c)
    {
        if (!HarvesterRunning) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the harvester event.");
        FinishHarvester(HarvesterPhase.Gone, $"ended by {c.Name}");
    }

    /// <summary>"/event current cancel": the harvester is removed, no win.</summary>
    void CancelHarvester(Client c)
    {
        if (!HarvesterRunning) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} cancelled the harvester event.");
        if (_harvCar is { } car) RetireHarvester(car);
        FinishHarvester(HarvesterPhase.Stopped, $"cancelled by {c.Name}");
    }

    /// <summary>A new round clears the world: the machine is hidden again (the car reset brought it back to its old field), the bales are gone, the first harvester waits its delay.</summary>
    void HarvesterRoundReset()
    {
        if (_harvCar is { } car) RetireHarvester(car);
        if (_harvBrain != null) FinishHarvester(HarvesterPhase.Stopped, "the round ended");
        foreach (var o in _harvOldDrivers.ToList()) if (_byId.ContainsKey(o.Id)) Leave(o);
        _harvOldDrivers.Clear();
        _harvVictims.Clear(); _machineCorpses.Clear(); _machineItems.Clear(); _madeBales.Clear(); _staticBales = null;
        _harvDirector!.Reset();
    }

    /// <summary>A joiner learns the job that runs and the static bales that burn or burnt (the made bales are corpses: world entities).</summary>
    void SendHarvesterOnJoin(Client c)
    {
        if (_harvBrain != null && _harvField != null) Send(c, new EventMsg((byte)EventKind.HarvesterState, HarvesterCodec.EncodeState(HarvesterStateOf(_harvBrain.Phase))));
        if (_staticBales == null) return;
        for (var i = 0; i < _staticBales.Length; i++)
            if (_staticBales[i].State != BaleState.Intact)
                Send(c, new EventMsg((byte)EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(_staticBales[i].State == BaleState.Burning ? HarvesterCodec.FxKind.BaleLit : HarvesterCodec.FxKind.BaleAsh, _staticBales[i].Position, HarvesterCodec.StaticBaleId(i)))));
    }

    HarvesterCodec.StateMsg HarvesterStateOf(HarvesterPhase phase)
    {
        var p = _harvBrain!.Progress;
        var driven = _harvDriver != null && _harvCar?.Seat.Occupant(0) == _harvDriver.Id;
        return new HarvesterCodec.StateMsg(phase, _harvCar?.Id ?? 0, (ushort)_harvFieldIndex, (byte)(p.Pass < 0 ? 255 : Math.Min(254, p.Pass)), p.Along, phase == HarvesterPhase.Cut && _harvBrain.Progress.Pass >= 0, driven,
            Harvester.HeaderWidthM, Harvester.EdgeMarginM, Harvester.PassOverlapM);
    }

    void SendHarvesterState(bool force, HarvesterPhase? phase = null)
    {
        if (_harvBrain == null || (!force && _elapsed < _harvSendAt)) return;
        _harvSendAt = _elapsed + 0.5;
        BroadcastEvent(EventKind.HarvesterState, HarvesterCodec.EncodeState(HarvesterStateOf(phase ?? _harvBrain.Phase)));
    }

    // ---- the tick ----

    /// <summary>Before the players are simulated and the vehicles step: the automatic spawn, the AI's driving, the end of a job, the drivers that are no more.</summary>
    void HarvesterTick(float dt)
    {
        if (_harvDirector == null) return;
        for (var i = _harvOldDrivers.Count - 1; i >= 0; i--)
        {
            var o = _harvOldDrivers[i];
            if (!_byId.ContainsKey(o.Id)) { _harvOldDrivers.RemoveAt(i); continue; }
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive) continue; // still aboard (the job was cancelled): retiring sent it out of the seat
            Leave(o); _harvOldDrivers.RemoveAt(i);
        }
        if (_harvBrain == null)
        {
            var humans = _byId.Values.Any(o => !o.IsBot);
            if (_match.Phase == MatchPhase.Running && _harvDirector.Tick(dt, humans, _events.Current != null)) TryStartHarvester(forced: false, out _);
            return;
        }
        var car = _harvCar!; var driver = _harvDriver!;
        if (_match.Phase != MatchPhase.Running || !_byId.Values.Any(o => !o.IsBot)) { CancelHarvesterQuiet(); return; } // the round ended or everyone left
        if (car.Destroyed) { FinishHarvester(HarvesterPhase.Stopped, "its machine was destroyed"); return; }
        if (_match.Get(driver.Id)?.Life != PlayerLife.Alive || car.Seat.Occupant(0) != driver.Id)
        {
            FinishHarvester(HarvesterPhase.Stopped, "its driver was killed");
            ChatToAll(ChatLineKind.System, "", "The harvester's driver is dead. The machine is up for grabs.");
            return;
        }
        var cmd = _harvBrain.Tick(dt, new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw, car.State.Speed);
        driver.Throttle = cmd.Throttle; driver.Steer = cmd.Steer; driver.Handbrake = false;
        car.Lights = false;
        if (_harvBrain.Phase == HarvesterPhase.Gone) { FinishHarvester(HarvesterPhase.Gone, "left the field"); return; }
        SendHarvesterState(force: false);
    }

    void CancelHarvesterQuiet()
    {
        if (_harvCar is { } car) RetireHarvester(car);
        FinishHarvester(HarvesterPhase.Stopped, "no round or no player");
    }

    /// <summary>After the vehicles moved: what the machine runs over and what its reel draws in, the corpses on their way into it, the bales' fire.</summary>
    void HarvesterAfterVehicles(float dt)
    {
        if (_harvCar is { Destroyed: false } car && car.Def.Id == HarvesterRules.VehicleId && _match.Phase == MatchPhase.Running) HarvesterContacts(car);
        StepMachineCorpses(dt);
        StepBales(dt);
    }

    /// <summary>The credit of the machine's kills: its driver (the AI's, or a player who took the seat), else nobody.</summary>
    ushort HarvesterAttacker(ServerCar car) => OccupantAt(car, 0) is { } d && _match.Get(d.Id)?.Life == PlayerLife.Alive ? d.Id : HarvesterRules.NobodyAttacker;

    void HarvesterContacts(ServerCar car)
    {
        if (!RunOver.Contacts(car.State.Speed)) return;
        var attacker = HarvesterAttacker(car);
        var fwd = new NVec3(car.Forward.X, 0, car.Forward.Z);
        var right = new NVec3(fwd.Z * -1f, 0, fwd.X);
        var pos = N(car.GlobalPosition);
        // the reel: a player on foot in front of the header is drawn in
        var targets = LiveTargets();
        try
        {
            foreach (var t in targets)
            {
                if (!_byId.TryGetValue(t.Id, out var o) || o.Car != null || car.Seat.SeatOf(t.Id) >= 0) continue;
                var d = t.Feet - pos;
                var along = NVec3.Dot(d, fwd); var lateral = NVec3.Dot(d, right);
                if (along < car.HalfLength - 1.4f || along > car.HalfLength + Harvester.ReelReachM || MathF.Abs(lateral) > car.HalfWidth || d.Y < -0.6f || d.Y > 2.5f) continue;
                if (car.HitCooldown.TryGetValue(t.Id, out var until) && _elapsed < until) continue;
                car.HitCooldown[t.Id] = _elapsed + RunOverCooldown;
                var r = Deal(attacker, SpecialWeapons.Harvester, t.Id, Harvester.ReelDamage, false, false);
                if (r != DamageResult.Ignored) GD.Print($"server: the harvester's reel drew in #{t.Id} ({r})");
            }
        }
        finally { ReturnTargets(targets); }
        // the rest of the body: whoever the machine drives over
        RunOverTargets(new RunOverSweep(MoverKind.GroundVehicle, N(car.PrevPos), pos, car.State.Yaw, car.HalfWidth, car.HalfLength, fwd, car.State.Speed, Harvester.ReelDamage, attacker, car.HitCooldown), id => car.Seat.SeatOf(id) >= 0);
    }

    // ---- the corpse in the machine ----

    /// <summary>A kill event (<c>PlayerKilled</c>): when the harvester made it, the body is not thrown, it goes into the machine.</summary>
    void HarvesterOnKill(PlayerKilled k)
    {
        if (_harvCar is not { Destroyed: false } car) return;
        var byMachine = k.Weapon == SpecialWeapons.Harvester
            || (k.Weapon == SpecialWeapons.Vehicle && _byId.TryGetValue(k.Killer, out var killer) && killer.Car == car && killer.CarSeat == 0);
        if (!byMachine) return;
        _harvVictims.Add(k.Victim);
        _deathPush.Remove(k.Victim); // no ragdoll throw: the body lies where it fell until the machine draws it in
    }

    /// <summary>A corpse was made (<see cref="SpawnCorpse"/>): when the harvester killed its player the corpse begins its way into the machine.</summary>
    void HarvesterOnCorpse(ushort victim, ushort corpseId, Corpse corpse)
    {
        if (!_harvVictims.Remove(victim) || _harvCar is not { Destroyed: false }) return;
        corpse.Attach(corpse.Position); // seated: it cannot be looted and is moved by the machine
        _machineCorpses.Add((new MachineCorpse(corpseId, Harvester), corpse.Position));
        HarvesterFx(HarvesterCodec.FxKind.Drawn, corpse.Position, corpseId);
    }

    void StepMachineCorpses(float dt)
    {
        if (_machineCorpses.Count == 0) return;
        var car = _harvCar;
        for (var i = _machineCorpses.Count - 1; i >= 0; i--)
        {
            var (m, from) = _machineCorpses[i];
            var machineGone = car == null || car.Destroyed; // it blew up or was hidden: whatever is in it is lost, a body still outside is left where it is
            if (m.Phase == MachineCorpsePhase.Drawn)
            {
                var body = _corpses.Get(m.Corpse);
                if (body == null || machineGone) // taken by the cap or the round, or no machine any more
                {
                    if (body is { Seated: true }) body.Throw(NVec3.Zero);
                    _machineCorpses.RemoveAt(i);
                    continue;
                }
                var fwd0 = new NVec3(car!.Forward.X, 0, car.Forward.Z);
                var intake0 = N(car.GlobalPosition) + fwd0 * (car.HalfLength - 0.2f) + NVec3.UnitY * 0.9f;
                var k = m.DrawFraction;
                body.Follow(NVec3.Lerp(from, intake0, k * k * (3f - 2f * k)));
            }
            else if (machineGone) { _machineCorpses.RemoveAt(i); _machineItems.Remove(m.Corpse); continue; }
            switch (m.Tick(dt))
            {
                case MachineCorpsePhase.Inside: // swallowed: the body is gone from the world, its Items ride inside
                    if (_corpses.Get(m.Corpse) is { } swallowed)
                    {
                        _machineItems[m.Corpse] = swallowed.Items.ToList();
                        HarvesterFx(HarvesterCodec.FxKind.Drawn, swallowed.Position, m.Corpse);
                        _match.Entities.Despawn(m.Corpse, WorldDespawnReason.Removed);
                    }
                    break;
                case MachineCorpsePhase.Baled:
                    EjectBale(car!, _machineItems.Remove(m.Corpse, out var items) ? items : new List<ItemStack>());
                    _machineCorpses.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>The bale leaves the unloader (rear, on the left of the machine) with the victim's Items and lies on the ground there for as long as a corpse does.</summary>
    void EjectBale(ServerCar car, List<ItemStack> items)
    {
        var fwd = new NVec3(car.Forward.X, 0, car.Forward.Z);
        var right = new NVec3(-fwd.Z, 0, fwd.X);
        var at = N(car.GlobalPosition) - fwd * (car.HalfLength + 1.6f) - right * 1.6f;
        var ground = Map?.GroundHeight(at.X, at.Z) ?? float.NaN;
        at.Y = float.IsNaN(ground) ? car.GlobalPosition.Y : ground;
        var knife = Math.Max(0, Weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee)); // a weapon nobody can swap into a hotbar position: the bale is for its Items
        var bale = new Corpse(HarvesterRules.BaleOwner, at, car.State.Yaw, knife, 0, items, Harvester.BaleLifetimeS);
        var id = _corpses.Add(bale);
        if (id is not { } cid) return;
        if (items.Count > 0) BroadcastCorpseItems(cid);
        _madeBales[cid] = new HayBale(at, Harvester, cid);
        HarvesterFx(HarvesterCodec.FxKind.BaleOut, at, cid);
        GD.Print($"server: harvester bale {cid} at ({at.X:F1},{at.Y:F1},{at.Z:F1}) with {items.Count} items");
    }

    // ---- bale fire ----

    HayBale[] StaticBales => _staticBales ??= (Map?.CropBales ?? Array.Empty<Vector3>()).Select(p => new HayBale(N(p), Harvester)).ToArray();

    /// <summary>A shot along <paramref name="from"/> + t * <paramref name="dir"/> for <paramref name="length"/> m (it stopped there: a wall, a player): any bale it passes alight (the field's own and a harvester's).</summary>
    void BaleShot(NVec3 from, NVec3 dir, float length)
    {
        if (!(length > 0)) return;
        foreach (var kv in _madeBales)
            if (kv.Value.State == BaleState.Intact && _corpses.Get(kv.Key) != null && kv.Value.HitBy(from, dir, length) && kv.Value.Ignite()) HarvesterFx(HarvesterCodec.FxKind.BaleLit, kv.Value.Position, kv.Key);
        var bales = StaticBales;
        for (var i = 0; i < bales.Length; i++)
            if (bales[i].State == BaleState.Intact && bales[i].HitBy(from, dir, length) && bales[i].Ignite()) HarvesterFx(HarvesterCodec.FxKind.BaleLit, bales[i].Position, HarvesterCodec.StaticBaleId(i));
    }

    /// <summary>Burns the burning bales and sets ablaze every player beside one (credited to nobody); a bale that burnt out is ash for everyone.</summary>
    void StepBales(float dt)
    {
        var any = false;
        foreach (var kv in _madeBales) if (kv.Value.Burning) { any = true; break; }
        if (!any && _staticBales != null) foreach (var b in _staticBales) if (b.Burning) { any = true; break; }
        if (!any) { PruneMadeBales(); return; }
        _baleFolk.Clear();
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive) _baleFolk.Add((o.Id, N(o.Body.GlobalPosition)));
        void Fire(HayBale b, ushort id)
        {
            foreach (var victim in b.Scorched(_baleFolk)) _burns.Ignite(victim, BurnSet.HazardSource, Harvester.BaleBurn);
            if (b.Tick(dt)) HarvesterFx(HarvesterCodec.FxKind.BaleAsh, b.Position, id);
        }
        foreach (var kv in _madeBales) if (kv.Value.Burning) Fire(kv.Value, kv.Key);
        if (_staticBales != null) for (var i = 0; i < _staticBales.Length; i++) if (_staticBales[i].Burning) Fire(_staticBales[i], HarvesterCodec.StaticBaleId(i));
        PruneMadeBales();
    }

    /// <summary>A bale that despawned like a corpse (its lifetime, the cap, the round) is forgotten.</summary>
    void PruneMadeBales()
    {
        if (_madeBales.Count == 0) return;
        List<ushort>? gone = null;
        foreach (var id in _madeBales.Keys) if (_corpses.Get(id) == null) (gone ??= new()).Add(id);
        if (gone != null) foreach (var id in gone) _madeBales.Remove(id);
    }

    void HarvesterFx(HarvesterCodec.FxKind kind, NVec3 at, ushort id) =>
        BroadcastEvent(EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(kind, at, id)));
}

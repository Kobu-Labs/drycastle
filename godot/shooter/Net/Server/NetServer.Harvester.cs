using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The harvester event on the server (#331, GLOSSARY "Event"). Now and then, or on <c>/event harvester</c>, a harvester (vehicles.json <c>harvester</c>, an ordinary <see cref="ServerCar"/> with an
/// open cab) appears on a crop field near the players (<see cref="CropFields"/>, from the ground patches of patches.json) and drives it in passes (<see cref="FieldPlanner"/>, <see cref="HarvesterBrain"/>),
/// then leaves along the edge. Its driver is a seated bot (<see cref="ServerClient.HarvesterDriver"/>): shot dead it stays seated and rides (ADR 0026) and anyone may take the seat once it rests.
/// Players the machine runs over or the reel draws in die (credited to the driver: the kill feed and the score); the corpse is drawn in at the front and a bale leaves the unloader with its Items
/// (<see cref="MachineCorpse"/>, a corpse owned by <see cref="HarvesterRules.BaleOwner"/>). A shot sets any bale alight, the field's own too (<see cref="HayBale"/>); a burning bale ignites players beside it,
/// credited to nobody (<see cref="BurnSet.HazardSource"/>). The cut itself is cosmetic and drawn by each client from the compact job state (<see cref="HarvesterCodec.StateMsg"/>), never per plant.
/// The rules are pure and tested in <c>ShooterCore/Events</c>; this file is the world they stand in. One <see cref="ServerCar"/> serves every job of a server run: it is hidden (destroyed, silently) between them.
/// </summary>
public partial class NetServer
{
    /// <summary>Every number of the event; the host may replace it.</summary>
    public HarvesterConfig Harvester = new();

    /// <summary>The numbers of the vehicles' crop damage (#328): what a ground vehicle crushes driving through a crop field.</summary>
    public CropCrushConfig CropCrush = new();

    HarvesterDirector? _harvDirector;
    List<CropField>? _cropFields;

    ServerCar? _harvCar;
    ServerClient? _harvDriver;
    HarvesterBrain? _harvBrain;
    CropField? _harvField;
    int _harvFieldIndex;
    double _harvSendAt;
    /// <summary>Drivers that lost the job (dead, or the job ended): taken out of the match once their corpse exists.</summary>
    readonly List<ServerClient> _harvOldDrivers = new();
    /// <summary>Players the harvester killed this tick, from the kill event to the corpse: their bodies go into the machine instead of being thrown.</summary>
    readonly Dictionary<ushort, ServerCar> _harvVictims = new();
    readonly List<(MachineCorpse Machine, NVec3 From, ServerCar Car)> _machineCorpses = new();
    /// <summary>The Items of the victims inside the machine, by their corpse id, until the bale leaves the unloader.</summary>
    readonly Dictionary<ushort, List<ItemStack>> _machineItems = new();
    HayBale[]? _staticBales;
    readonly Dictionary<ushort, HayBale> _madeBales = new();
    readonly List<(ushort Id, NVec3 Feet)> _baleFolk = new();

    /// <summary>What the reels have cut on one crop field and the bales dropped there (<see cref="FieldSwath"/>); the clients derive the same from the stretches sent (<see cref="EventKind.HarvesterCut"/>).</summary>
    sealed class SwathField
    {
        public required FieldSwath Swath;
        public double LastCut;
        public readonly List<HayBale> Bales = new();
        /// <summary>Kept points not sent yet (the first one was, when it is a continuation).</summary>
        public readonly List<SwathPt> Open = new();
    }

    readonly Dictionary<int, SwathField> _swaths = new();
    readonly Dictionary<ushort, SwathSampler> _samplers = new();
    List<(NVec2 Min, NVec2 Max)>? _cropBounds;
    int _swathTotal;
    double _swathFlushAt;

    /// <summary>Crop crush (#328, #353): the masks per crop field and each vehicle's path (<see cref="CropCrushMask"/>, the same code a client stamps its drawn vehicles with), the cells not sent yet, a scratch list.</summary>
    CropCrushMask? _crushMask;
    readonly Dictionary<int, List<int>> _crushPending = new();
    readonly List<int> _crushFresh = new();
    double _crushFlushAt;

    /// <summary>Called once from <c>_Ready</c> with the other events: the director and the handler table's kind.</summary>
    void InitHarvester() => _harvDirector = new HarvesterDirector(Harvester, _rng);

    /// <summary>The crop fields of the map, once (the ground patches of crop types; nothing without a map).</summary>
    List<CropField> CropFieldList => _cropFields ??= Map == null
        ? new List<CropField>()
        : CropFields.From(Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())));

    /// <summary>The fields vehicles leave tracks on (the harvestable ones and the bare ploughed ones); the crush mask's indices are these.</summary>
    List<CropField> CrushFieldList => _crushFields ??= Map == null
        ? new List<CropField>()
        : CropFields.From(Map.PatchPaints.Select(p => (p.Id, p.Type, (IReadOnlyList<NVec2>)p.Ring.Select(v => new NVec2(v.X, v.Y)).ToList())), includeBare: true);
    List<CropField>? _crushFields;

    bool HarvesterRunning => _harvBrain != null;

    // ---- the lifecycle ----

    /// <summary>"/event harvester": one now, ignoring the cooldown; refuses while one is out. The answer says where it appeared.</summary>
    void StartHarvester(ServerClient admin)
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
            Name = $"{def.Id}{_fleet.Count}", Id = (ushort)(VehicleRecord.FirstId + _fleet.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = origin, OriginYaw = yaw, FlightWater = Water, FlightBounds = MapFlightBounds,
        };
        World.AddChild(car);
        _fleet.Add(car);
        return car;
    }

    /// <summary>The AI driver: a bot-like player with no brain's say (<see cref="ServerClient.HarvesterDriver"/>), so it can be shot, scores kills and leaves a corpse like any seated player. Null when no id is free.</summary>
    ServerClient? AddHarvesterDriver()
    {
        var id = _ids.Allocate(i => _clients.Values.Any(o => o.Id == i) || _byId.ContainsKey(i));
        if (id == 0) return null;
        var c = new ServerClient { Id = id, Name = "Bot Harvester", ConnectedAt = _elapsed, Brain = new BotBrain(BotTuning.Default, id), HarvesterDriver = true };
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
        foreach (var o in _fleet.Occupants(car)) { if (o.HarvesterDriver) _harvOldDrivers.Add(o); LeaveSeat(o, true); }
        car.Seat.Clear();
        if (!car.Destroyed) car.Destroy(double.MaxValue);
    }

    /// <summary>"/event current end": the harvester leaves at once, as a win.</summary>
    void EndHarvester(ServerClient c)
    {
        if (!HarvesterRunning) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the harvester event.");
        FinishHarvester(HarvesterPhase.Gone, $"ended by {c.Name}");
    }

    /// <summary>"/event current cancel": the harvester is removed, no win.</summary>
    void CancelHarvester(ServerClient c)
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
        _swaths.Clear(); _samplers.Clear(); _swathTotal = 0;
        _crushMask?.Clear(); _crushPending.Clear(); // the marks do not outlive the round
        _harvDirector!.Reset();
    }

    /// <summary>A joiner learns the job that runs and the static bales that burn or burnt (the made bales are corpses: world entities).</summary>
    void SendHarvesterOnJoin(ServerClient c)
    {
        if (_harvBrain != null && _harvField != null) Send(c, new EventMsg((byte)EventKind.HarvesterState, HarvesterCodec.EncodeState(HarvesterStateOf(_harvBrain.Phase))));
        foreach (var (field, sf) in _swaths) // the cut so far, field by field (bounded: SwathCodec, HarvesterConfig.MaxSwathPoints)
        {
            var age = (ushort)Math.Clamp(_elapsed - sf.LastCut, 0, ushort.MaxValue - 1);
            for (var i = 0; i < sf.Swath.Strokes.Count; i++)
                foreach (var bytes in SwathCodec.Pack((ushort)field, Harvester.HeaderWidthM, age, i < sf.Swath.FirstCounted, sf.Swath.Strokes[i], replay: true))
                    Send(c, new EventMsg((byte)EventKind.HarvesterCut, bytes));
        }
        foreach (var (field, grid) in _crushMask?.Grids ?? new Dictionary<int, CrushGrid?>()) // the crushed ground: the whole mask of each field as runs
            if (grid != null && grid.Count > 0)
                foreach (var bytes in CrushCodec.PackRuns((ushort)field, grid.Runs(), CropCrush.MaxMessageBytes)) Send(c, new EventMsg((byte)EventKind.CropCrush, bytes));
        if (_staticBales != null)
            for (var i = 0; i < _staticBales.Length; i++) SendBaleFxOnJoin(c, _staticBales[i], HarvesterCodec.StaticBaleId(i));
        foreach (var sf in _swaths.Values)
            for (var i = 0; i < sf.Bales.Count; i++) SendBaleFxOnJoin(c, sf.Bales[i], HarvesterCodec.DroppedBaleId(i));
    }

    void SendBaleFxOnJoin(ServerClient c, HayBale b, ushort id)
    {
        if (b.State == BaleState.Intact) return;
        Send(c, new EventMsg((byte)EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(b.State == BaleState.Burning ? HarvesterCodec.FxKind.BaleLit : HarvesterCodec.FxKind.BaleAsh, b.Position, id))));
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
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Def.Id != HarvesterRules.VehicleId || car.Seat.Occupant(0) == 0) continue; // any harvester with somebody at the wheel: the event's, a stolen one, a spawned one
            if (_match.Phase == MatchPhase.Running) HarvesterContacts(car);
            StepSwath(car);
        }
        StepCrush();
        foreach (var car in _fleet.Cars) if (car.Def.Id == HarvesterRules.VehicleId && (car.Destroyed || car.Seat.Occupant(0) == 0)) _samplers.Remove(car.Id);
        FlushSwaths();
        StepMachineCorpses(dt);
        FollowMadeBales();
        StepBales(dt);
    }

    /// <summary>The credit of the machine's kills: its driver (the AI's, or a player who took the seat), else nobody.</summary>
    ushort HarvesterAttacker(ServerCar car) => _fleet.OccupantAt(car, 0) is { } d && _match.Get(d.Id)?.Life == PlayerLife.Alive ? d.Id : HarvesterRules.NobodyAttacker;

    /// <summary>The reel is the only part that harms (<see cref="HarvesterConfig.Reel"/>): a player on foot inside its box is drawn in while the machine moves forward. The body kills nobody (vehicles.json run_over is false).</summary>
    void HarvesterContacts(ServerCar car)
    {
        if (car.State.Speed < Harvester.CutMinSpeedMps) return;
        var attacker = HarvesterAttacker(car);
        var pos = N(car.GlobalPosition);
        var targets = LiveTargets();
        try
        {
            foreach (var t in targets)
            {
                if (!_byId.TryGetValue(t.Id, out var o) || o.Car != null || car.Seat.SeatOf(t.Id) >= 0) continue;
                if (!Harvester.Reel.Contains(pos, car.State.Yaw, t.Feet + NVec3.UnitY * 0.1f)) continue;
                if (car.HitCooldown.TryGetValue(t.Id, out var until) && _elapsed < until) continue;
                car.HitCooldown[t.Id] = _elapsed + RunOverCooldown;
                _harvVictims[t.Id] = car;
                var r = Deal(attacker, SpecialWeapons.Harvester, t.Id, Harvester.ReelDamage, false, false);
                if (r != DamageResult.Ignored) GD.Print($"server: the harvester's reel drew in #{t.Id} ({r})");
                else _harvVictims.Remove(t.Id);
            }
        }
        finally { ReturnTargets(targets); }
    }

    // ---- the corpse in the machine ----

    /// <summary>A kill event (<c>PlayerKilled</c>): when the harvester made it, the body is not thrown, it goes into the machine.</summary>
    void HarvesterOnKill(PlayerKilled k)
    {
        if (k.Weapon != SpecialWeapons.Harvester) return; // only the reel
        if (!_harvVictims.TryGetValue(k.Victim, out var car) || car.Destroyed) { _harvVictims.Remove(k.Victim); return; }
        _deathPush.Remove(k.Victim); // no ragdoll throw: the body lies where it fell until the machine draws it in
    }

    /// <summary>A corpse was made (<see cref="SpawnCorpse"/>): when the harvester killed its player the corpse begins its way into the machine.</summary>
    void HarvesterOnCorpse(ushort victim, ushort corpseId, Corpse corpse)
    {
        if (!_harvVictims.Remove(victim, out var car) || car.Destroyed) return;
        corpse.Attach(corpse.Position); // seated: it cannot be looted and is moved by the machine
        _machineCorpses.Add((new MachineCorpse(corpseId, Harvester, victim), corpse.Position, car));
        HarvesterFx(HarvesterCodec.FxKind.Drawn, corpse.Position, corpseId, victim);
    }

    void StepMachineCorpses(float dt)
    {
        if (_machineCorpses.Count == 0) return;
        for (var i = _machineCorpses.Count - 1; i >= 0; i--)
        {
            var (m, from, car) = _machineCorpses[i];
            var machineGone = car.Destroyed; // it blew up or was hidden: whatever is in it is lost, a body still outside is left where it is
            if (m.Phase == MachineCorpsePhase.Drawn)
            {
                var body = _corpses.Get(m.Corpse);
                if (body == null || machineGone) // taken by the cap or the round, or no machine any more
                {
                    if (body is { Seated: true }) body.Throw(NVec3.Zero);
                    _machineCorpses.RemoveAt(i);
                    continue;
                }
                var fwd0 = new NVec3(car.Forward.X, 0, car.Forward.Z);
                var intake0 = N(car.GlobalPosition) + fwd0 * Harvester.HeaderForwardM + NVec3.UnitY * Harvester.ReelAxisUpM; // the reel's axis: the clients spin the body round it
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
                        HarvesterFx(HarvesterCodec.FxKind.Swallowed, swallowed.Position, m.Corpse, m.Victim);
                        _match.Entities.Despawn(m.Corpse, WorldDespawnReason.Removed);
                    }
                    break;
                case MachineCorpsePhase.Baled:
                    EjectBale(car, _machineItems.Remove(m.Corpse, out var items) ? items : new List<ItemStack>(), m.Victim);
                    _machineCorpses.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>
    /// The bale leaves the unloader (rear, on the left of the machine, <see cref="HarvesterConfig.UnloaderHeightM"/> up) with the victim's Items, thrown backwards and up on top of the machine's velocity
    /// (<see cref="HarvesterCatch.BaleVelocity"/>): it flies, lands and rolls to a stop (<see cref="Corpse.Launch"/> with <c>rolls</c>, stepped by <see cref="StepCorpses"/>). One entity, the body inside it
    /// is drawn by the clients (<paramref name="victim"/> rides in the effect). It lies for as long as a corpse does; its <see cref="HayBale"/> follows it (<see cref="FollowMadeBales"/>).
    /// </summary>
    void EjectBale(ServerCar car, List<ItemStack> items, ushort victim)
    {
        var fwd = new NVec3(car.Forward.X, 0, car.Forward.Z);
        if (fwd.LengthSquared() < 1e-6f) fwd = -NVec3.UnitZ; else fwd = NVec3.Normalize(fwd);
        var at = HarvesterCatch.UnloaderAt(N(car.GlobalPosition), fwd, car.HalfLength, Harvester);
        var ground = Map?.GroundHeight(at.X, at.Z) ?? float.NaN;
        if (!float.IsNaN(ground)) at.Y = MathF.Max(at.Y, ground + 0.2f); // never spawned inside the ground
        var knife = Math.Max(0, Weapons.Weapons.FindIndex(w => w.Kind == WeaponKind.Melee)); // a weapon nobody can swap into a hotbar position: the bale is for its Items
        var bale = new Corpse(HarvesterRules.BaleOwner, at, car.State.Yaw, knife, 0, items, Harvester.BaleLifetimeS);
        bale.Launch(HarvesterCatch.BaleVelocity(fwd, CarVelocity(car), Harvester), rolls: true);
        var id = _corpses.Add(bale);
        if (id is not { } cid) return;
        if (items.Count > 0) BroadcastCorpseItems(cid);
        _madeBales[cid] = new HayBale(bale.Position, Harvester, cid);
        HarvesterFx(HarvesterCodec.FxKind.BaleOut, at, cid, victim);
        GD.Print($"server: harvester bale {cid} of #{victim} thrown from ({at.X:F1},{at.Y:F1},{at.Z:F1}) with {items.Count} items");
    }

    /// <summary>A thrown bale's <see cref="HayBale"/> lies where its corpse is (fire, scorch and shots follow it until it has stopped).</summary>
    void FollowMadeBales()
    {
        foreach (var kv in _madeBales)
            if (_corpses.Get(kv.Key) is { } body && kv.Value.Position != body.Position) kv.Value.MoveTo(body.Position);
    }

    // ---- the cut: the real path of the reel ----

    /// <summary>The crop field (index in the field list, the wire id) a point lies on, or -1.</summary>
    int CropFieldAt(NVec2 p)
    {
        var fields = CropFieldList;
        if (_cropBounds == null || _cropBounds.Count != fields.Count)
            _cropBounds = fields.Select(f => (new NVec2(f.Ring.Min(v => v.X), f.Ring.Min(v => v.Y)), new NVec2(f.Ring.Max(v => v.X), f.Ring.Max(v => v.Y)))).ToList();
        for (var i = 0; i < fields.Count; i++)
        {
            var (lo, hi) = _cropBounds[i];
            if (p.X < lo.X || p.X > hi.X || p.Y < lo.Y || p.Y > hi.Y) continue;
            if (Poly.Contains(fields[i].Ring, p)) return i;
        }
        return -1;
    }

    /// <summary>While somebody drives a harvester forward over a crop field its reel cuts a swath: the real path, sampled every couple of metres, whoever drives. The kept stretches are sent in batches.</summary>
    void StepSwath(ServerCar car)
    {
        if (Map == null) return;
        if (!_samplers.TryGetValue(car.Id, out var sampler)) _samplers[car.Id] = sampler = new SwathSampler(Harvester);
        var reel = Harvester.Reel.Centre(new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw);
        var active = car.State.Speed >= Harvester.CutMinSpeedMps;
        var field = active ? CropFieldAt(reel) : -1;
        if (!sampler.Step(field, reel, active, out var a, out var b)) return;
        if (!_swaths.TryGetValue(field, out var sf)) _swaths[field] = sf = new SwathField { Swath = new FieldSwath(Harvester, CropFieldList[field].DropsBales) };
        var before = sf.Swath.PointCount;
        if (!sf.Swath.TryAdd(a, b, _swathTotal)) { FlushSwath(field, sf); sf.Open.Clear(); return; }
        _swathTotal += sf.Swath.PointCount - before;
        sf.LastCut = _elapsed;
        if (sf.Open.Count == 0 || sf.Open[^1] != a) { FlushSwath(field, sf); sf.Open.Clear(); sf.Open.Add(a); }
        sf.Open.Add(b);
        for (var i = sf.Bales.Count; i < sf.Swath.Bales.Count; i++)
        {
            var (pos, _) = sf.Swath.Bales[i];
            var ground = Map.GroundHeight(pos.X, pos.Y);
            if (float.IsNaN(ground)) ground = Map.GroundHeightOrMean(pos.X, pos.Y);
            sf.Bales.Add(new HayBale(new NVec3(pos.X, ground, pos.Y), Harvester, (ushort)i));
        }
    }

    // ---- crops crushed by vehicles (#328) ----

    /// <summary>Every ground vehicle that moves (with a driver, or rolling fast) over a crop field stamps its body's width into the field's mask, from its leading end, a sample every couple of metres (<see cref="CropCrushMask"/>); the new cells go out <see cref="CropCrushConfig.FlushS"/> apart. A client stamped the same from what it draws; this mask is the authority. Cosmetic.</summary>
    void StepCrush()
    {
        if (Map == null) return;
        var mask = _crushMask ??= new CropCrushMask(CropCrush, CrushFieldList);
        foreach (var car in _fleet.Cars)
        {
            var ground = !car.Destroyed && car.Def.Flight == null && !car.Afloat;
            var body = new CrushBody(new NVec2(car.GlobalPosition.X, car.GlobalPosition.Z), car.State.Yaw, car.HalfWidth, car.HalfLength, car.State.Speed, car.Seat.Occupied, ground);
            _crushFresh.Clear();
            var field = mask.Step(car.Id, body, _crushFresh);
            if (field < 0) continue;
            if (!_crushPending.TryGetValue(field, out var list)) _crushPending[field] = list = new List<int>();
            list.AddRange(_crushFresh);
        }
        if (_crushPending.Count == 0 || _elapsed < _crushFlushAt) return;
        _crushFlushAt = _elapsed + CropCrush.FlushS;
        foreach (var (field, list) in _crushPending)
        {
            list.Sort();
            foreach (var bytes in CrushCodec.PackDelta((ushort)field, list, CropCrush.MaxMessageBytes)) BroadcastEvent(EventKind.CropCrush, bytes);
        }
        _crushPending.Clear();
    }

    void FlushSwath(int field, SwathField sf)
    {
        if (sf.Open.Count < 2) return;
        foreach (var bytes in SwathCodec.Pack((ushort)field, Harvester.HeaderWidthM, 0, false, sf.Open)) BroadcastEvent(EventKind.HarvesterCut, bytes);
        sf.Open.RemoveRange(0, sf.Open.Count - 1);
    }

    /// <summary>Twice a second: the kept stretches go out; a field's bales fade <see cref="HarvesterConfig.DroppedBaleFadeS"/> after its last cut (every client counts the same from the age it was told).</summary>
    void FlushSwaths()
    {
        if (_swaths.Count == 0 || _elapsed < _swathFlushAt) return;
        _swathFlushAt = _elapsed + 0.5;
        foreach (var (field, sf) in _swaths)
        {
            FlushSwath(field, sf);
            if (sf.Bales.Count > 0 && _elapsed - sf.LastCut >= Harvester.DroppedBaleFadeS) { sf.Bales.Clear(); sf.Swath.FadeBales(); sf.Open.Clear(); }
        }
    }

    // ---- bale fire ----

    HayBale[] StaticBales => _staticBales ??= (Map?.CropBales ?? Array.Empty<Vector3>()).Select(p => new HayBale(N(p), Harvester)).ToArray();

    /// <summary>A shot along <paramref name="from"/> + t * <paramref name="dir"/> for <paramref name="length"/> m (it stopped there: a wall, a player): any bale it passes alight (the field's own and a harvester's).</summary>
    void BaleShot(NVec3 from, NVec3 dir, float length)
    {
        if (!(length > 0)) return;
        foreach (var kv in _madeBales)
            if (kv.Value.State == BaleState.Intact && _corpses.Get(kv.Key) != null && kv.Value.HitBy(from, dir, length) && kv.Value.Ignite()) HarvesterFx(HarvesterCodec.FxKind.BaleLit, kv.Value.Position, kv.Key);
        foreach (var sf in _swaths.Values)
            for (var i = 0; i < sf.Bales.Count; i++)
                if (sf.Bales[i].State == BaleState.Intact && sf.Bales[i].HitBy(from, dir, length) && sf.Bales[i].Ignite()) HarvesterFx(HarvesterCodec.FxKind.BaleLit, sf.Bales[i].Position, HarvesterCodec.DroppedBaleId(i));
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
        if (!any) foreach (var sf in _swaths.Values) foreach (var b in sf.Bales) if (b.Burning) { any = true; break; }
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
        foreach (var sf in _swaths.Values) for (var i = 0; i < sf.Bales.Count; i++) if (sf.Bales[i].Burning) Fire(sf.Bales[i], HarvesterCodec.DroppedBaleId(i));
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

    void HarvesterFx(HarvesterCodec.FxKind kind, NVec3 at, ushort id, ushort victim = 0) =>
        BroadcastEvent(EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(kind, at, id, victim)));
}

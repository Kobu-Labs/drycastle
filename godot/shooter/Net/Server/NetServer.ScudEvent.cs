using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Scud event on the server (docs/events/scud.md, GLOSSARY "Scud event"): <c>/event scud</c> hides a Scud truck at a baked point of a <c>scud</c> zone (or, on a map with none, on open ground away from
/// the players, <see cref="PickScudSpot"/>), puts a loaded rocket cart beside each bunker, and announces a top secret weapon on the intel board with a SEARCH AREA (a circle the truck is somewhere inside, whose
/// centre is the truck plus a random vector, <see cref="ScudSearch"/>). The first human to board the truck wins it (<see cref="ScudEventRun"/>): XP, the entry and the circle withdrawn, the event slot free; the
/// truck stays an ordinary vehicle until it is destroyed and the carts at the bunkers (replaced after a cooldown, <see cref="ScudCartSlot"/>) with it. Nobody boards it in time, or it is destroyed unfound: the
/// truck and the carts go and nothing is paid. A new round ends all of it (the truck never returns, ADR 0033), as the harvester's job ends.
/// </summary>
public partial class NetServer
{
    readonly ScudEventRun _scudRun = new();
    ServerCar? _scudTruck;
    readonly List<ScudBunkerSlot> _scudSlots = new();

    /// <summary>One bunker of the map and the cart that stands for it.</summary>
    sealed class ScudBunkerSlot
    {
        public required string BunkerId;
        public required NVec2 Centre;
        public required float Radius, FloorY;
        public float? Door;
        public readonly ScudCartSlot Slot = new();
        public ServerCar? Cart;
    }

    List<RoadLine>? _scudRoads;

    // ---- the sheet and the places ----

    /// <summary>The tactical map's world sheet as the clients draw it (<see cref="TacticalMapMath.Sheet"/> over the parcels' box and the terrain's): origin, side, south z. Null without a map.</summary>
    (NVec2 Origin, float Side)? ScudSheet()
    {
        if (Map == null) return null;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        void Merge(float x0, float z0, float x1, float z1) { minX = MathF.Min(minX, x0); minZ = MathF.Min(minZ, z0); maxX = MathF.Max(maxX, x1); maxZ = MathF.Max(maxZ, z1); }
        foreach (var p in Map.Parcels.Values) Merge(p.Bounds.Position.X, p.Bounds.Position.Y, p.Bounds.End.X, p.Bounds.End.Y);
        if (Map.TerrainBox is { } t) Merge(t.Position.X, t.Position.Z, t.End.X, t.End.Z);
        if (minX > maxX) return null;
        return TacticalMapMath.Sheet(minX, minZ, maxX, maxZ);
    }

    /// <summary>Whether a vehicle-sized box of the ground vehicle's world (buildings, trees, rocks, closed doors) is free where it would stand, grown by the data's clearance.</summary>
    bool ScudFootprintClear(Vector3 ground, float yaw, float halfW, float halfL)
    {
        var margin = (float)Scud.Event.SpotClearanceM;
        const float lift = 0.7f, height = 3.0f; // over the ground's bumps, up past the roof
        var shape = new BoxShape3D { Size = new Vector3((halfW + margin) * 2, height, (halfL + margin) * 2) };
        var q = new PhysicsShapeQueryParameters3D
        {
            Shape = shape, CollisionMask = CollisionRules.StaticMask(MoverKind.GroundVehicle),
            Transform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, 0)), new Vector3(ground.X, ground.Y + lift + height / 2, ground.Z)),
        };
        return World.GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count == 0;
    }

    /// <summary>The origin height for a vehicle of this size at (x, z) facing <paramref name="yaw"/>: on real ground under the whole footprint, with nothing solid on it and no vehicle within a metre of it. Null otherwise.</summary>
    float? ScudStandingHeight(float x, float z, float yaw, float halfW, float halfL)
    {
        var terrain = Map?.GroundHeight(x, z) ?? float.NaN;
        if (float.IsNaN(terrain)) return null;
        var space = World.GetWorld3D().DirectSpaceState;
        if (ServerCar.RestHeight(space, new Vector3(x, terrain, z), yaw, halfW, halfL, terrain) is not { } y) return null;
        if (!ScudFootprintClear(new Vector3(x, y, z), yaw, halfW, halfL)) return null;
        var box = new BodyBox(x, z, halfW, halfL, yaw);
        foreach (var other in _cars)
            if (!other.Destroyed && !other.Retired && ScudReloadRules.Gap(box, BoxOf(other)) < 1f) return null;
        return y;
    }

    /// <summary>
    /// Where the truck stands: a random baked point of a <c>scud</c> zone, else (a map with none drawn) a random player spawn point, which is open ground in the village; at least
    /// <see cref="ScudEventData.MinPlayerDistanceM"/> from every human player, facing along the nearest road (any way when none is near), with its footprint clear of solids. Null when none of a few dozen tries fits.
    /// </summary>
    (NVec3 Pos, float Yaw, bool FromZone)? PickScudSpot(VehicleDef def)
    {
        var players = _byId.Values.Where(o => !o.IsBot && _match.Get(o.Id)?.Life == PlayerLife.Alive).Select(o => N(o.Body.GlobalPosition)).ToList();
        var minD = (float)Scud.Event.MinPlayerDistanceM;
        for (var tries = 0; tries < 80; tries++)
        {
            var zone = tries < 40 ? PickEventSpawn(EventBook.Scud) : null;
            NVec3 p;
            if (zone is { } z) p = z;
            else if (Spawns.Count > 0) p = Spawns[_rng.Next(Spawns.Count)];
            else break;
            if (players.Any(q => Dist2D(q, p) < minD)) continue;
            _scudRoads ??= Map != null ? RoadNetwork.Parse(Map.RoadsJson) : new List<RoadLine>();
            var yaw = RoadHeading.YawAt(_scudRoads, p.X, p.Z) ?? (float)(_rng.NextDouble() * MathF.Tau - MathF.PI);
            if (ScudStandingHeight(p.X, p.Z, yaw, def.HalfWidth, def.HalfLength) is not { } y) continue;
            return (new NVec3(p.X, y, p.Z), yaw, zone != null);
        }
        return null;
    }

    // ---- the bunkers and their carts ----

    /// <summary>The map's bunkers as the carts see them: the middle and the reach of the ground floor's room, and the bearing of its doorway when a probe finds one (null when none).</summary>
    List<ScudBunkerSlot> ScudBunkers()
    {
        var list = new List<ScudBunkerSlot>();
        foreach (var room in MapShelters.Rooms.Where(r => r.Level == 0 && r.BunkerId != "" && r.BunkerId != ChurchShell.RoomId && r.Ring.Length >= 3))
        {
            if (list.Any(b => b.BunkerId == room.BunkerId)) continue;
            var centre = NVec2.Zero;
            foreach (var v in room.Ring) centre += v;
            centre /= room.Ring.Length;
            var radius = room.Ring.Max(v => NVec2.Distance(v, centre)) + 0.5f;
            list.Add(new ScudBunkerSlot { BunkerId = room.BunkerId, Centre = centre, Radius = radius, FloorY = room.Y0, Door = BunkerDoorBearing(centre, radius, room.Y0) });
        }
        return list;
    }

    /// <summary>
    /// The way a bunker's doorway faces, from the physics world (bunkers.json does not say): rays from the middle of the ground floor at three heights, in 24 directions; the direction whose neighbours are also free
    /// (open all the way out past the footprint) is the doorway. Firing slits are too small to be open at all three heights. Null when every direction is walled in.
    /// </summary>
    float? BunkerDoorBearing(NVec2 centre, float radius, float floorY)
    {
        const int n = 24;
        var space = World.GetWorld3D().DirectSpaceState;
        var mask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
        var free = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var a = i * MathF.Tau / n;
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var open = true;
            foreach (var h in new[] { 0.5f, 1.1f, 1.7f })
            {
                var from = new Vector3(centre.X, floorY + h, centre.Y);
                var q = PhysicsRayQueryParameters3D.Create(from, from + dir * (radius + 8f));
                q.CollisionMask = mask;
                if (space.IntersectRay(q).Count > 0) { open = false; break; }
            }
            free[i] = open;
        }
        var best = -1; var bestScore = 0;
        for (var i = 0; i < n; i++)
        {
            if (!free[i]) continue;
            var score = 1 + (free[(i + 1) % n] ? 1 : 0) + (free[(i + n - 1) % n] ? 1 : 0);
            if (score > bestScore) { best = i; bestScore = score; }
        }
        return best < 0 ? null : best * MathF.Tau / n;
    }

    /// <summary>Puts a loaded cart at the bunker: the first of <see cref="ScudCartSpots"/> with ground, free of solids and of other vehicles. False when none fits (the slot tries again later).</summary>
    bool TryPlaceScudCart(ScudBunkerSlot b)
    {
        var def = Vehicles?.Vehicles.FirstOrDefault(v => v.Trailer is { HasCargo: true });
        if (def == null) return false;
        if (b.Cart is { Destroyed: false, Retired: false, Tractor: null } old && old.Cargo is { Loaded: false }) old.Retire(); // the emptied cart this one replaces: gone, unless somebody is towing it
        foreach (var spot in ScudCartSpots.Candidates(b.Centre, b.Radius, b.Door, (float)Scud.Event.CartStandoffM, def.HalfLength))
        {
            if (ScudStandingHeight(spot.Origin.X, spot.Origin.Y, spot.Yaw, def.HalfWidth, def.HalfLength) is not { } y) continue;
            b.Cart = SpawnRocketCart(new NVec3(spot.Origin.X, y, spot.Origin.Y), spot.Yaw, loaded: true);
            GD.Print($"server: Scud event: a loaded rocket cart {b.Cart.Id} stands at bunker {b.BunkerId} ({spot.Origin.X:F1},{spot.Origin.Y:F1})");
            return true;
        }
        GD.PrintErr($"server: Scud event: no free place for a rocket cart at bunker {b.BunkerId}; it tries again in {ScudCartSlot.RetrySeconds:F0} s");
        return false;
    }

    // ---- the lifecycle ----

    /// <summary>"/event scud": the truck is hidden, the carts stand at the bunkers and the intel board names the search area. The answer to the admin says where the truck really is (the only place that does).</summary>
    void StartScudEvent(ServerClient admin)
    {
        if (_events.WhyNotStart(EventBook.Scud) is { } busy) { ChatTo(admin, ChatLineKind.Error, busy); return; }
        if (_scudRun.TruckOut) { ChatTo(admin, ChatLineKind.Error, "A Scud truck from the last event is still out. It has to be destroyed first."); return; }
        if (_match.Phase != MatchPhase.Running) { ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        var def = Vehicles?.Vehicles.FirstOrDefault(v => v.Launcher != null);
        if (def == null || Vehicles!.Vehicles.All(v => v.Trailer is not { HasCargo: true })) { ChatTo(admin, ChatLineKind.Error, "vehicles.json has no Scud truck or rocket cart."); return; }
        if (PickScudSpot(def) is not { } spot) { ChatTo(admin, ChatLineKind.Error, "No free ground far enough from the players was found for the Scud truck."); return; }
        var truck = SpawnScudTruck(spot.Pos, spot.Yaw);
        if (truck == null) { ChatTo(admin, ChatLineKind.Error, "The Scud truck could not be spawned."); return; }

        var sheet = ScudSheet();
        var extent = sheet?.Side ?? 3000f;
        var radius = ScudSearch.RadiusM(Scud.Event.SearchRadiusCells, extent);
        var area = ScudSearch.Make(new NVec2(spot.Pos.X, spot.Pos.Z), radius, _rng);
        _scudTruck = truck;
        _scudRun.Begin(area, (float)(Scud.Event.DespawnMinutes * 60.0));
        _events.Start(EventBook.Scud, _elapsed);
        AnnounceIntel(ScudEventRun.Intel(_elapsed, area));
        ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Scud event. A top secret weapon has been located: read the intel board at the base.");

        _scudSlots.Clear();
        _scudSlots.AddRange(ScudBunkers());
        foreach (var b in _scudSlots) b.Slot.Open(TryPlaceScudCart(b));

        var grid = sheet is { } s ? TacticalMapMath.ReferenceOf(area.Centre, s.Origin.X, s.Origin.Y + s.Side, s.Side) : $"{area.Centre.X:0},{area.Centre.Y:0}";
        var cells = sheet != null ? ScudSearch.Cells(radius, extent) : Scud.Event.SearchRadiusCells;
        ChatTo(admin, ChatLineKind.Info, $"Scud event: search area centred on {grid}, radius {cells} cells ({radius:0} m). The truck is really at x {spot.Pos.X:0}, z {spot.Pos.Z:0} (" + (spot.FromZone ? "a baked scud zone point" : "FALLBACK: open ground, the map has no scud zone") + $"); {_scudSlots.Count} bunker cart(s). Nobody else is told.");
        GD.Print($"server: Scud event started by {admin.Name} #{admin.Id}: truck {truck.Id} at ({spot.Pos.X:F1},{spot.Pos.Y:F1},{spot.Pos.Z:F1}), {(spot.FromZone ? "scud zone point" : "FALLBACK spawn point")}, area centre ({area.Centre.X:F0},{area.Centre.Y:F0}) radius {radius:F0} m, {_scudSlots.Count} bunker carts, despawns in {Scud.Event.DespawnMinutes:F0} min");
    }

    /// <summary>"/event current end": the truck counts as found by the caller, who is paid.</summary>
    void EndScudEvent(ServerClient c)
    {
        if (!_scudRun.Searching) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} ended the Scud event as a win.");
        if (_scudRun.Win(c.Id)) OnScudFound(c.Id, c.Name);
    }

    /// <summary>"/event current cancel": the truck and the carts are removed, nobody is paid.</summary>
    void CancelScudEvent(ServerClient c)
    {
        if (!_scudRun.Searching) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} cancelled the Scud event.");
        _scudRun.Despawn();
        FinishScud($"cancelled by {c.Name}", retireTruck: true);
    }

    /// <summary>Once a tick: who boards the truck, the time, the truck's end, and the bunkers' carts.</summary>
    void ScudEventTick(float dt)
    {
        if (!_scudRun.TruckOut) return;
        var truck = _scudTruck;
        var alive = truck is { Destroyed: false, Retired: false };
        var boarder = alive ? FirstHumanAboard(truck!) : (ushort)0;
        switch (_scudRun.Tick(dt, boarder, alive))
        {
            case ScudEventOutcome.Found: OnScudFound(boarder, _byId.TryGetValue(boarder, out var f) ? f.Name : "Somebody"); break;
            case ScudEventOutcome.TimedOut:
                ChatToAll(ChatLineKind.System, "", "The top secret weapon was moved out of the area. Nobody found it.");
                FinishScud("nobody boarded it in time", retireTruck: true);
                break;
            case ScudEventOutcome.TruckLost:
                if (_events.Current?.Kind == EventBook.Scud) ChatToAll(ChatLineKind.System, "", "The top secret weapon was destroyed before anyone found it.");
                else ChatToAll(ChatLineKind.System, "", "The Scud truck is destroyed. The rocket carts at the bunkers are gone.");
                FinishScud("the truck was destroyed", retireTruck: false);
                break;
        }
        if (_scudRun.TruckOut) TickScudSlots(dt);
    }

    ushort FirstHumanAboard(ServerCar truck)
    {
        for (var s = 0; s < truck.Seat.Count; s++)
            if (OccupantAt(truck, s) is { IsBot: false } o) return o.Id;
        return 0;
    }

    /// <summary>The first player aboard (or the admin's end): the reward, the entry and the circle gone, the event slot free. The truck and the carts stay.</summary>
    void OnScudFound(ushort finder, string name)
    {
        _events.End(EventBook.Scud, EventPhase.Won, _elapsed);
        WithdrawIntel(ScudEventRun.IntelId);
        var xp = (int)Scud.Event.XpReward;
        if (finder != 0 && _byId.ContainsKey(finder) && Scores(finder) && xp > 0) AwardXp(new List<XpGrant> { new(finder, xp, XpReason.EventWin) });
        ChatToAll(ChatLineKind.System, "", $"Event won: {name} found the Scud truck! It stays out until it is destroyed, and so do the rocket carts at the bunkers.");
        if (_byId.TryGetValue(finder, out var c)) Send(c, new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(ScudNoticeCode.EventFound)));
        GD.Print($"server: Scud event won by #{finder} {name}: +{xp} XP");
    }

    /// <summary>The truck is out of the event (despawned, cancelled or destroyed): the intel entry and the carts go, the slot is free, the truck is retired when it is still there.</summary>
    void FinishScud(string why, bool retireTruck)
    {
        _events.End(EventBook.Scud, EventPhase.Despawned, _elapsed);
        WithdrawIntel(ScudEventRun.IntelId);
        foreach (var b in _scudSlots) b.Slot.Close();
        RemoveRocketCarts();
        if (retireTruck && _scudTruck is { Destroyed: false } t)
        {
            foreach (var o in Occupants(t)) LeaveSeat(o, true);
            t.Seat.Clear();
            t.Retire();
        }
        _scudTruck = null;
        GD.Print($"server: Scud event over: {why}");
    }

    void TickScudSlots(float dt)
    {
        var cooldown = (float)Scud.Event.CartCooldownSeconds;
        foreach (var b in _scudSlots)
        {
            var loaded = b.Cart is { Destroyed: false, Retired: false } c && c.Cargo is { Loaded: true } && !c.Life.Doomed;
            if (b.Slot.Tick(dt, loaded, cooldown) == CartSlotAction.Place) b.Slot.Placed(TryPlaceScudCart(b));
        }
    }

    /// <summary>A new round: the truck and the carts are retired with the world (<see cref="ScudRoundReset"/>), the event is over with no reward and the board is cleared of it.</summary>
    void ScudEventRoundReset()
    {
        if (!_scudRun.TruckOut) return;
        var searching = _scudRun.Searching;
        _scudRun.Despawn();
        if (searching) { _events.End(EventBook.Scud, EventPhase.Despawned, _elapsed); WithdrawIntel(ScudEventRun.IntelId); }
        foreach (var b in _scudSlots) b.Slot.Close();
        _scudSlots.Clear();
        _scudTruck = null;
        GD.Print("server: Scud event ended by the new round");
    }
}

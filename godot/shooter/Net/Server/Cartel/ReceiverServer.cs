using Godot;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Cartel;

/// <summary>Where a Heist's Receiver is to stand, chosen and claimed but not yet put down (a start the van or the counts refuse leaves nothing in the world).</summary>
/// <param name="At">The origin: the pallet's base centre, the helicopter on its pad's ground, the boat on the water's surface.</param>
/// <param name="Exit">A helicopter's way out: the ground point (x, z) past the nearest map edge it flies to.</param>
/// <param name="Route">A boat's way out, the placed route (the mooring is <paramref name="At"/>), at the water's surface.</param>
internal sealed record ReceiverPlan(ReceiverKind Kind, NVec3 At, float Yaw, NVec2? Exit, IReadOnlyList<NVec3>? Route);

/// <summary>
/// The Receivers on the server (GLOSSARY "Receiver"; docs/cartel.md "Receivers"): where a Heist's drop is, put down for the Job and taken away with it. <see cref="Plan"/> picks the kind at random among the kinds the map
/// has (cartel.json <c>receivers.weights</c>; a kind with no free place falls through to the next) and claims its place in the Job book (a hollow house under the Trap house key, a landing spot or mooring under its
/// zone key); <see cref="Put"/> spawns the <see cref="Receiver"/> (world entity 59; its own volume in the Duffel book: not a vehicle of vehicles.json, which would bring seats, a driver, damage and a body to push
/// around, none of which a Receiver has). The interact key at its Cartel man (<see cref="Use"/>) is the leader's send-off; the party is told what the key would do (<see cref="EventKind.ReceiverReach"/>).
/// A helicopter or boat that was sent off outlives its Job by the length of its way out: <see cref="Release"/> keeps it and the Duffels on it until it is gone.
/// </summary>
internal sealed class ReceiverServer
{
    readonly ServerContext _ctx;
    readonly CartelFile _data;
    readonly DuffelBook _book;
    readonly TrapHouseServer _houses;
    readonly Func<CartelMap> _map;
    readonly Func<FlightBounds> _bounds;
    readonly Water _water;
    readonly Func<float, float, float> _ground;
    readonly float _marginM;
    readonly Random _rng;
    readonly List<Receiver> _live = new();

    /// <summary>The running Heist the player belongs to (set by the Heist server, which needs this one to begin).</summary>
    public Func<ushort, HeistRun?> RunOf { get; set; } = _ => null;

    public ReceiverServer(ServerContext ctx, CartelFile data, DuffelBook book, TrapHouseServer houses, Func<CartelMap> map, Func<FlightBounds> bounds, Water water, Func<float, float, float> ground, float cargoMarginM, Random rng)
    {
        _ctx = ctx; _data = data; _book = book; _houses = houses; _map = map; _bounds = bounds; _water = water; _ground = ground; _marginM = cargoMarginM; _rng = rng;
        ReceiverRules.Register(WorldEntityRegistry.Shared);
    }

    public ReceiversData Data => _data.Receivers;
    public IReadOnlyList<Receiver> Live => _live;

    /// <summary>The kinds this map has at all (a landing spot or a mooring drawn, a hollow house to take).</summary>
    List<ReceiverKind> Has()
    {
        var has = new List<ReceiverKind>();
        if (_houses.Data != null) has.Add(ReceiverKind.StashHouse);
        if (_map().Helis.Count > 0) has.Add(ReceiverKind.Helicopter);
        if (_map().Boats.Count > 0) has.Add(ReceiverKind.Boat);
        return has;
    }

    /// <summary>
    /// The drop of <paramref name="job"/>: a kind drawn by the weights among those the map has, the first of them that has a place free now at least <paramref name="data"/>'s drop_min_m from
    /// <paramref name="objective"/> (and drop_apart_m from another Job's). Null with why not when none has.
    /// </summary>
    public ReceiverPlan? Plan(Job job, JobBook book, NVec3 objective, string objectiveName, HeistData data, out string? refusal)
    {
        refusal = null;
        var has = Has();
        if (has.Count == 0) { refusal = "This map has no drop for a heist: no hollow house, no landing spot, no mooring."; return null; }
        var order = ReceiverRules.Order(_data.Receivers.Weights, has, _rng);
        foreach (var kind in order)
        {
            var plan = kind switch
            {
                ReceiverKind.StashHouse => PlanStash(job, book, objective, data),
                ReceiverKind.Helicopter => PlanHelicopter(job, book, objective, objectiveName, data),
                _ => PlanBoat(job, book, objective, objectiveName, data),
            };
            if (plan != null) return plan;
        }
        refusal = $"No drop {data.DropMinM:F0} m or more from {objectiveName} is free now: another job holds the places there are.";
        return null;
    }

    ReceiverPlan? PlanStash(Job job, JobBook book, NVec3 objective, HeistData data)
    {
        var def = _data.Receivers.StashHouse;
        var min2 = data.DropMinM * data.DropMinM;
        foreach (var cand in _houses.Candidates().OrderBy(_ => _rng.Next()))
        {
            var centre = TrapHouseHeistRules.Centre(cand.House);
            if (NVec2.DistanceSquared(centre, new NVec2(objective.X, objective.Z)) < min2 || book.IsReserved(TrapHouseHeistRules.Key(cand.House.Id))) continue;
            if (StashPlacement.Plan(cand.House, cand.RoadGround, def.SizeM, def.Figure.Pos, g => _ground(g.X, g.Y)) is not { } spot) continue;
            if (!book.Reserve(job, TrapHouseHeistRules.Key(cand.House.Id))) continue;
            GD.Print($"server: {job}: stash house {cand.House.Id}, pallet {(spot.Inside ? "inside" : "outside")} by door {spot.Door}");
            return new ReceiverPlan(ReceiverKind.StashHouse, spot.At, spot.YawRad, null, null);
        }
        return null;
    }

    ReceiverPlan? PlanHelicopter(Job job, JobBook book, NVec3 objective, string objectiveName, HeistData data)
    {
        var map = _map();
        if (CartelSpots.Claim(job, book, map, CartelZones.HeliKind, _rng, objective, objectiveName, data.DropMinM, data.DropApartM, out _) is not { } at) return null;
        var spot = map.Helis[IndexOf(map.HeliPoints, at)];
        var y = _ground(spot.At.X, spot.At.Y);
        GD.Print($"server: {job}: helicopter on the landing spot at ({spot.At.X:F0}, {spot.At.Y:F0})");
        return new ReceiverPlan(ReceiverKind.Helicopter, new NVec3(spot.At.X, y, spot.At.Y), spot.YawRad, ExitOf(spot.At), null);
    }

    ReceiverPlan? PlanBoat(Job job, JobBook book, NVec3 objective, string objectiveName, HeistData data)
    {
        var map = _map();
        if (CartelSpots.Claim(job, book, map, CartelZones.BoatKind, _rng, objective, objectiveName, data.DropMinM, data.DropApartM, out _) is not { } at) return null;
        var spot = map.Boats[IndexOf(map.BoatPoints, at)];
        var y = SurfaceAt(spot.At);
        GD.Print($"server: {job}: boat at the mooring at ({spot.At.X:F0}, {spot.At.Y:F0}), water at {y:F2}, {spot.Route.Count} route point(s)");
        return new ReceiverPlan(ReceiverKind.Boat, new NVec3(spot.At.X, y, spot.At.Y), spot.YawRad, null, spot.Route.Select(p => new NVec3(p.X, y, p.Y)).ToList());
    }

    static int IndexOf(IReadOnlyList<NVec3> pool, NVec3 p)
    {
        for (var i = 0; i < pool.Count; i++) if (pool[i] == p) return i;
        return 0;
    }

    /// <summary>The surface of the water body under the point, or the ground there when it is on none.</summary>
    float SurfaceAt(NVec2 p)
    {
        foreach (var b in _water.Bodies) if (b.Contains(p.X, p.Y)) return b.SurfaceY;
        return _ground(p.X, p.Y);
    }

    /// <summary>Where a helicopter that lifts off at <paramref name="from"/> leaves the map: over the nearest edge, exit_margin_m past it.</summary>
    NVec2 ExitOf(NVec2 from)
    {
        var b = _bounds();
        var m = _data.Receivers.Leave.ExitMarginM;
        var west = from.X - b.MinX; var east = b.MaxX - from.X; var north = from.Y - b.MinZ; var south = b.MaxZ - from.Y;
        var near = Math.Min(Math.Min(west, east), Math.Min(north, south));
        if (near == west) return new NVec2(b.MinX - m, from.Y);
        if (near == east) return new NVec2(b.MaxX + m, from.Y);
        return near == north ? new NVec2(from.X, b.MinZ - m) : new NVec2(from.X, b.MaxZ + m);
    }

    /// <summary>
    /// Puts the planned Receiver down for the Job: a world entity and a cargo volume of the Duffel book that takes only the party's Duffels, <paramref name="max"/> of them, and is sent off with at least
    /// <paramref name="min"/>. Null when the world is full.
    /// </summary>
    public Receiver? Put(ReceiverPlan plan, IReadOnlySet<ushort> party, int min, int max)
    {
        var leave = _data.Receivers.Leave;
        ReceiverPath? path = plan.Kind switch
        {
            ReceiverKind.Helicopter when plan.Exit is { } exit => ReceiverPath.Helicopter(plan.At, exit, leave),
            ReceiverKind.Boat when plan.Route is { Count: > 0 } route => ReceiverPath.Boat(route.Prepend(plan.At).ToList(), leave),
            _ => null,
        };
        var r = new Receiver(plan.Kind, _data.Receivers, plan.At, plan.Yaw, party, min, max, path, _marginM);
        if (_ctx.Match.Entities.Spawn(r) is not { } id) return null;
        r.Id = id;
        _book.AddVolume(r);
        r.Gone += OnGone;
        _live.Add(r);
        GD.Print($"server: receiver {id}: {ReceiverRules.NameOf(plan.Kind)} at ({plan.At.X:F0}, {plan.At.Y:F1}, {plan.At.Z:F0}), takes {r.Capacity}, leaves with {min} or more");
        return r;
    }

    /// <summary>It left (its way ended): what lay on it goes and it despawns (the entity manager does that, it ended).</summary>
    void OnGone(Receiver r)
    {
        _book.RemoveLoadedIn(r);
        _book.RemoveVolume(r);
        _live.Remove(r);
    }

    /// <summary>
    /// The Job ended. A Receiver still receiving goes at once with what lay on it; one that was sent off stays to finish its way (its Duffels with it), the Job's other Duffels are the caller's to remove.
    /// </summary>
    public void Release(Receiver r)
    {
        if (r.Leaving || r.Phase == ReceiverPhase.Gone) return;
        OnGone(r);
        _ctx.Match.Entities.Despawn(r.Id, WorldDespawnReason.Removed);
    }

    // ---- the key and the prompt ----

    /// <summary>The Receiver of the player's running Heist that the player stands at the Cartel man of, or null.</summary>
    Receiver? AtManOf(ServerClient c)
    {
        if (_live.Count == 0 || c.IsBot || !_ctx.IsAlive(c.Id) || c.Car != null) return null;
        return RunOf(c.Id) is { Receiver: { } r } && r.InReach(c.Body.GlobalPosition.ToNumerics()) ? r : null;
    }

    /// <summary>The player's interact key would be a Receiver's (the key competition: it wins the key over the plantation's hold).</summary>
    public bool InReach(ServerClient c) => AtManOf(c) != null;

    /// <summary>The interact key's press on foot: the leader sends the Receiver off with at least the minimum on it; under it the press is answered with why not; a mate is told only the leader does. True when it was at a Cartel man.</summary>
    public bool Use(ServerClient c)
    {
        if (AtManOf(c) is not { } r || RunOf(c.Id) is not { } run) return false;
        var man = ReceiverRules.Man(r.Kind);
        switch (ReceiverRules.Ask(run.Job.Leader == c.Id, r.Loaded, r.Min))
        {
            case ReceiverAsk.Leave:
                if (r.Send()) GD.Print($"server: {c.Name} #{c.Id} told the {man} to leave with {r.SentWith} duffels ({run.Job})");
                break;
            case ReceiverAsk.NeedMore: _ctx.ChatTo(c, ChatLineKind.Info, $"The {man} wants at least {r.Min} duffels: {r.Loaded} on board."); break;
            default: _ctx.ChatTo(c, ChatLineKind.Info, $"Only your leader can tell the {man} to go."); break;
        }
        return true;
    }

    /// <summary>Each tick: every human of a Heist's party is told what their key would do at the Cartel man when that changes.</summary>
    public void Tick()
    {
        foreach (var c in _ctx.Roster)
        {
            if (c.IsBot || !c.Joined) continue;
            var r = AtManOf(c);
            var run = r != null ? RunOf(c.Id) : null;
            var ask = r != null && run != null ? ReceiverRules.Ask(run.Job.Leader == c.Id, r.Loaded, r.Min) : ReceiverAsk.None;
            var key = r == null ? 0u : (uint)r.Id << 16 | (uint)ask << 8 | (uint)r.Loaded;
            if (key == c.ReceiverReach) continue;
            c.ReceiverReach = key;
            _ctx.SendEvent(c, EventKind.ReceiverReach, r == null ? ReceiverCodec.EncodeReach(0, ReceiverKind.StashHouse, ReceiverAsk.None, 0, 0) : ReceiverCodec.EncodeReach(r.Id, r.Kind, ask, r.Loaded, r.Min));
        }
    }

    /// <summary>A new round: the match cleared its entities; nothing is live.</summary>
    public void RoundStart()
    {
        foreach (var r in _live.ToList()) { _book.RemoveVolume(r); }
        _live.Clear();
        foreach (var c in _ctx.Roster) c.ReceiverReach = 0;
    }
}

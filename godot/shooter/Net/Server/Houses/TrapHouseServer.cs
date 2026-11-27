using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The one keeper of the Trap houses (#496, ADR 0046, docs/cartel.md "Trap house") on the server: which hollow houses stand dressed, with what. <see cref="Dress"/> plans a house's props
/// (<see cref="TrapHousePlanner"/>, trap_house.json), puts them in the world (<see cref="TrapHousesNode"/>: the solid ones are boxes on the World layer) and tells everyone
/// (<see cref="EventKind.TrapHouseDressed"/>); <see cref="Strip"/> takes them away (<see cref="EventKind.TrapHouseStripped"/>). A joiner is told every dressed house. A new round
/// strips them all (the Jobs that held them failed with it). The Trap house Job asks <see cref="PickAndDress"/> for its house and strips it when it ends; the spots of the dressing
/// (defenders' posts, the safe, the Duffel spots) are its own to use. A map without hollow houses, or the arena: nothing here does anything.
/// </summary>
internal sealed class TrapHouseServer
{
    readonly ServerContext _ctx;
    readonly bool _arenaMode;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Random _rng = new();
    readonly Dictionary<string, TrapHouseDressing> _dressed = new();
    RoadGraph? _roads;

    public TrapHouseServer(ServerContext ctx, bool arenaMode, Drycastle.MapLoader.MapLoader? map)
    {
        _ctx = ctx; _arenaMode = arenaMode; _map = map;
    }

    TrapHousesNode? Node => _arenaMode ? null : TrapHousesNode.Current;

    /// <summary>Whether this map has a house that could be dressed.</summary>
    public bool On => Node != null;

    /// <summary>The dressed houses by building id, with their dressing (props and the spots).</summary>
    public IReadOnlyDictionary<string, TrapHouseDressing> Dressed => _dressed;

    /// <summary>The roads a Cartel van drives (trap_house.json choice.min_road_width_m), parsed at the first ask.</summary>
    RoadGraph Roads(TrapHouseFile data) => _roads ??= _map != null && _map.RoadsJson.Length > 0 ? RoadGraph.Parse(_map.RoadsJson, data.Choice.MinRoadWidthM) : RoadGraph.Empty;

    /// <summary>Where the living players' feet are (no house with one inside is taken).</summary>
    List<NVec3> Feet() => _ctx.Players.Values.Where(c => c.Joined && _ctx.IsAlive(c.Id)).Select(c => c.Body.GlobalPosition.ToNumerics()).ToList();

    /// <summary>The houses a Trap house Job may take now: road access for the van, not dressed, not in <paramref name="inUse"/> (another Job's), nobody inside.</summary>
    public List<TrapHouseCandidate> Candidates(ICollection<string> inUse)
    {
        if (Node is not { } node) return new List<TrapHouseCandidate>();
        var taken = new HashSet<string>(inUse);
        taken.UnionWith(_dressed.Keys);
        return TrapHouseChoice.Candidates(node.Houses, Roads(node.Data), node.Data.Choice.RoadMaxM, taken, Feet());
    }

    /// <summary>
    /// The Job's house: a random one of <see cref="Candidates"/> whose dressing has at least <paramref name="minDuffelSpots"/> Duffel spots, dressed now with a random seed. Null when
    /// none qualifies (the Job cannot start).
    /// </summary>
    public (TrapHouseCandidate House, TrapHouseDressing Dressing)? PickAndDress(ICollection<string> inUse, int minDuffelSpots = 1)
    {
        if (Node is not { } node) return null;
        foreach (var c in Candidates(inUse).OrderBy(_ => _rng.Next()))
        {
            var d = TrapHousePlanner.Plan(c.House, node.Data, _rng.Next());
            if (d.Duffels.Count < minDuffelSpots) continue;
            Apply(node, d);
            return (c, d);
        }
        return null;
    }

    /// <summary>Dresses house <paramref name="house"/> (a building id) by <paramref name="seed"/>, for everyone; a house already dressed is dressed again. Null for a house with no floor plan.</summary>
    public TrapHouseDressing? Dress(string house, int seed)
    {
        if (Node is not { } node || !node.Plans.TryGetValue(house, out var plan)) return null;
        var d = TrapHousePlanner.Plan(plan, node.Data, seed);
        Apply(node, d);
        return d;
    }

    /// <summary>A dressing goes into the world and to everyone. Planning a house takes a few milliseconds (the largest about 20): once at a Job's start, not every tick.</summary>
    void Apply(TrapHousesNode node, TrapHouseDressing d)
    {
        node.Show(d.House, d.Props);
        _dressed[d.House] = d;
        _ctx.Broadcast(EventKind.TrapHouseDressed, TrapHouseCodec.EncodeDressed(d.House, d.Props));
        GD.Print($"server: trap house {d.House} dressed ({d.Props.Count} props, {d.Posts.Count} posts, {d.Duffels.Count} duffel spots)");
    }

    /// <summary>House <paramref name="house"/>'s props are taken away, for everyone. False when it was not dressed.</summary>
    public bool Strip(string house)
    {
        if (!_dressed.Remove(house)) return false;
        Node?.Hide(house);
        _ctx.Broadcast(EventKind.TrapHouseStripped, TrapHouseCodec.EncodeStripped(house));
        return true;
    }

    /// <summary>Every dressed house stripped; the count.</summary>
    public int StripAll()
    {
        var n = 0;
        foreach (var house in _dressed.Keys.ToList()) if (Strip(house)) n++;
        return n;
    }

    /// <summary>A new round: no Job outlives it, so no Trap house does.</summary>
    public void RoundStart() => StripAll();

    /// <summary>The joiner's catch-up: every dressed house with its props.</summary>
    public void SendOnJoin(ServerClient c)
    {
        foreach (var (house, d) in _dressed) _ctx.SendEvent(c, EventKind.TrapHouseDressed, TrapHouseCodec.EncodeDressed(house, d.Props));
    }

    /// <summary>"/traphouse [nearest|&lt;house id&gt;|off]": dress the hollow house nearest the admin (or that one) as a Trap house now, for everyone, or strip every one.</summary>
    public void ChatTrapHouse(ServerClient c, IReadOnlyList<string> args)
    {
        if (Node is not { } node) { _ctx.ChatTo(c, ChatLineKind.Error, "This map has no hollow house with a floor plan to dress."); return; }
        var what = args.Count > 0 ? args[0] : "nearest";
        if (what.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            var n = StripAll();
            _ctx.ChatTo(c, ChatLineKind.Info, n == 0 ? "No house is dressed." : $"Stripped {n} trap house{(n == 1 ? "" : "s")}.");
            return;
        }
        HouseFloorPlan? plan;
        if (what.Equals("nearest", StringComparison.OrdinalIgnoreCase))
        {
            var p = c.Body.GlobalPosition;
            var at = new NVec2(p.X, -p.Z);
            plan = node.Houses.MinBy(h => h.Contains(p.ToNumerics()) ? 0 : h.Shell.Min(v => NVec2.Distance(v, at)));
        }
        else if (!node.Plans.TryGetValue(what, out plan))
        {
            _ctx.ChatTo(c, ChatLineKind.Error, $"No hollow house '{what}' with a floor plan on this map.");
            return;
        }
        if (plan == null || Dress(plan.Id, _rng.Next()) is not { } d) { _ctx.ChatTo(c, ChatLineKind.Error, "No house to dress."); return; }
        var solid = d.Props.Count(x => node.Data.Props[x.Kind].Solid);
        var safe = d.Safe is { } s ? $"the safe in {s.Room} ({plan.Room(s.Room)?.Type})" : "no safe";
        _ctx.ChatTo(c, ChatLineKind.Info, $"Trap house {plan.Id} ({plan.Parcel}) dressed: {d.Props.Count} props ({solid} solid), {d.Posts.Count} posts, {d.Duffels.Count} Duffel spots, {safe}. "
            + $"{Candidates(Array.Empty<string>()).Count} houses qualify for a Job now. /traphouse off strips it.");
    }
}

using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// House destruction's events (docs/house-destruction.md Contract 4, <see cref="HouseCodec"/>) to the client's houses (<see cref="HouseWorldNode"/>): 242 a carve op, 254 a vehicle's cut,
/// 243 a house's removed cells (join catch-up, resync), 244 the rubble mode and the round reset. A desync is answered with 249 HouseResync. The houses' debris
/// (<see cref="DebrisNode"/>) lives under <see cref="ClientContext.Owned"/>, so a leave frees it and the next server's first event makes a new one. Also the Trap houses' props (66, 67; #496).
/// </summary>
public sealed class HousesClient
{
    readonly ClientContext _ctx;
    readonly Action<ushort> _resync;
    /// <summary>The glass client's shard rims of the hollow houses' panes (set by the net client once it has one).</summary>
    public Action<int, bool>? PaneFrameChanged { get; set; }

    public HousesClient(ClientContext ctx)
    {
        _ctx = ctx;
        _resync = house => ctx.Send(EventKind.HouseResync, HouseCodec.EncodeResync(house));
        _due = cut => cut.Car == OwnCar() || cut.Tick <= ctx.RenderTick + 0.5; // another vehicle's hole opens as it is drawn reaching it (100 ms behind), the own one's at once
    }

    readonly Func<HouseCutMsg, bool> _due;

    /// <summary>The vehicle this player drives (0: none).</summary>
    ushort OwnCar()
    {
        if (!_ctx.Driving) return 0;
        foreach (var v in _ctx.Cars.All) if (v.Driver == _ctx.MyId) return v.Id;
        return 0;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind is EventKind.TrapHouseDressed or EventKind.TrapHouseStripped) { TrapHouse(kind, payload); return; }
        if (kind is not (EventKind.HouseCarve or EventKind.HouseCut or EventKind.HouseCells or EventKind.HouseSettings) || HouseWorldNode.Current is not { } houses) return;
        Wire(houses);
        switch (kind)
        {
            case EventKind.HouseCarve when HouseCodec.DecodeCarve(payload) is { } carve:
                houses.OnCarve(carve);
                break;
            case EventKind.HouseCut when HouseCodec.DecodeCut(payload) is { } cut:
                houses.OnCut(cut);
                break;
            case EventKind.HouseCells when HouseCodec.DecodeCells(payload) is { } cells:
                houses.OnCells(cells);
                break;
            case EventKind.HouseSettings when HouseCodec.DecodeSettings(payload) is { } settings:
                houses.OnSettings(settings);
                break;
        }
    }

    /// <summary>The resync sender and the debris (top level at the origin, its dust under Owned as RocketFx's), made again once a leave has freed them.</summary>
    void Wire(HouseWorldNode houses)
    {
        houses.RequestResync = _resync;
        houses.Due = _due;
        houses.PaneFrameChanged = PaneFrameChanged;
        if (houses.Debris is DebrisNode d && GodotObject.IsInstanceValid(d) && d.IsInsideTree()) return;
        var owned = _ctx.Owned;
        var debris = DebrisNode.Create(houses.Config, owned);
        owned.AddChild(debris);
        houses.Debris = debris;
    }

    /// <summary>The Trap houses (#496): a dressed house's props appear (also a joiner's catch-up), a stripped one's go.</summary>
    static void TrapHouse(EventKind kind, byte[] payload)
    {
        if (Drycastle.Shooter.Buildings.TrapHousesNode.Current is not { } traps) return;
        if (kind == EventKind.TrapHouseDressed && Drycastle.ShooterCore.Cartel.TrapHouseCodec.DecodeDressed(payload) is { } dressed) traps.Show(dressed.House, dressed.Props);
        else if (kind == EventKind.TrapHouseStripped && Drycastle.ShooterCore.Cartel.TrapHouseCodec.DecodeStripped(payload) is { } house) traps.Hide(house);
    }

    /// <summary>Leaving the server: the houses whole again and the debris gone (the next server sends its own state).</summary>
    public void Teardown()
    {
        Drycastle.Shooter.Buildings.TrapHousesNode.Current?.HideAll(); // the next server says which houses it has dressed
        if (HouseWorldNode.Current is not { } houses) return;
        houses.ResetAll();
        houses.Debris = null; // freed with Owned
        Drycastle.Shooter.Buildings.HouseDoorsNode.Current?.RehangAll(); // the next server says which doors it has blown
    }
}

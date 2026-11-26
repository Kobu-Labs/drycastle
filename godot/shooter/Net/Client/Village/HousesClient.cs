using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// House destruction's events (docs/house-destruction.md Contract 4, <see cref="HouseCodec"/>) to the client's houses (<see cref="HouseWorldNode"/>): 242 a carve op,
/// 243 a house's removed cells (join catch-up, resync), 244 the rubble mode and the round reset. A desync is answered with 249 HouseResync. The houses' debris
/// (<see cref="DebrisNode"/>) lives under <see cref="ClientContext.Owned"/>, so a leave frees it and the next server's first event makes a new one.
/// </summary>
public sealed class HousesClient
{
    readonly ClientContext _ctx;
    readonly Action<ushort> _resync;

    public HousesClient(ClientContext ctx)
    {
        _ctx = ctx;
        _resync = house => ctx.Send(EventKind.HouseResync, HouseCodec.EncodeResync(house));
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind is not (EventKind.HouseCarve or EventKind.HouseCells or EventKind.HouseSettings) || HouseWorldNode.Current is not { } houses) return;
        Wire(houses);
        switch (kind)
        {
            case EventKind.HouseCarve when HouseCodec.DecodeCarve(payload) is { } carve:
                houses.OnCarve(carve);
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
        if (houses.Debris is DebrisNode d && GodotObject.IsInstanceValid(d) && d.IsInsideTree()) return;
        var owned = _ctx.Owned;
        var debris = DebrisNode.Create(houses.Config, owned);
        owned.AddChild(debris);
        houses.Debris = debris;
    }

    /// <summary>Leaving the server: the houses whole again and the debris gone (the next server sends its own state).</summary>
    public void Teardown()
    {
        if (HouseWorldNode.Current is not { } houses) return;
        houses.ResetAll();
        houses.Debris = null; // freed with Owned
    }
}

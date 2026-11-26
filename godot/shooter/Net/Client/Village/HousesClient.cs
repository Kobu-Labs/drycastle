using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// House destruction's events (docs/house-destruction.md Contract 4) to the client's houses (<see cref="HouseWorldNode"/>): 242 a carve op, 243 a house's
/// removed cells (join catch-up, resync), 244 the rubble mode and the round reset. A desync is answered with 249 HouseResync.
/// </summary>
public sealed class HousesClient
{
    readonly Action<ushort> _resync;

    public HousesClient(ClientContext ctx)
    {
        _resync = house => ctx.Send(EventKind.HouseResync, HouseReplicaWire.EncodeResync(house));
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (HouseWorldNode.Current is not { } houses) return;
        switch (kind)
        {
            case EventKind.HouseCarve when HouseReplicaWire.DecodeCarve(payload) is { } carve:
                houses.RequestResync = _resync;
                houses.OnCarve(carve);
                break;
            case EventKind.HouseCells when HouseReplicaWire.DecodeCells(payload) is { } cells:
                houses.OnCells(cells);
                break;
            case EventKind.HouseSettings when HouseReplicaWire.DecodeSettings(payload) is { } settings:
                houses.RequestResync = _resync;
                houses.OnSettings(settings);
                break;
        }
    }

    /// <summary>Leaving the server: the houses whole again (the next server sends its own state).</summary>
    public void Teardown() => HouseWorldNode.Current?.ResetAll();
}

using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// The village doors the server opens and closes: the church doors (ADR 0019), the Obecny Urad's door, the bus garage's doors and the hollow houses' (ADR 0044). The server's state sets the box and swings
/// the leaves; a joiner sees an open door swing open once. At a door players work, a player on foot sees the prompt; nothing is sent, the server sees the interact key
/// in the inputs and decides (<c>InputServer</c>).
/// </summary>
public sealed class DoorsClient
{
    readonly ClientContext _ctx;

    public DoorsClient(ClientContext ctx) { _ctx = ctx; }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.ChurchDoor when ChurchDoorCodec.Decode(payload) is { } church:
                ChurchDoorNode.Current?.SetOpen(church, animate: true);
                break;
            case EventKind.UradDoor when UradDoorCodec.Decode(payload) is { } urad:
                UradDoorNode.Current?.SetOpen(urad, animate: true);
                break;
            case EventKind.GarageDoor when GarageDoorCodec.Decode(payload) is { } garage:
                GarageDoorNode.Current?.SetOpen(garage, animate: true);
                break;
            case EventKind.HouseDoor when HouseDoorCodec.Decode(payload) is { } house:
                HouseDoorsNode.Current?.Show(house.Door, house.Open, animate: true);
                break;
        }
    }

    /// <summary>On foot at a doorway a player works (the bus garage's, a hollow house's): the interact key moves the door (it goes before a vehicle in reach, as on the server).</summary>
    public string? DoorPrompt(bool driving)
    {
        if (driving || _ctx.Dead || _ctx.Me is not { } me) return null;
        var p = me.GlobalPosition;
        var at = new NVec3(p.X, p.Y, p.Z);
        if (GarageDoorNode.Current is { } bus && bus.Garage.InReach(at))
            return Strings.F(bus.IsOpen ? "hud.garage.close" : "hud.garage.open", InputBindings.Name("interact"));
        if (HouseDoorsNode.Current is not { } houses || houses.Doors.Nearest(at) is var door && door < 0) return null;
        var open = houses.IsOpen(door);
        var key = houses.Doors.All[door].Kind == HouseDoorKind.Garage ? open ? "hud.housegarage.close" : "hud.housegarage.open" : open ? "hud.door.close" : "hud.door.open";
        return Strings.F(key, InputBindings.Name("interact"));
    }
}

using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// The village doors the server opens and closes: the church doors (ADR 0019), the Obecny Urad's door and the bus garage's doors. The server's state sets the box and swings
/// the leaves; a joiner sees an open door swing open once. For the garage a player on foot at the doorway sees the prompt; nothing is sent, the server sees the interact key
/// in the inputs and decides (<c>NetServer.GarageUse</c>).
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
        }
    }

    /// <summary>On foot at the garage doorway: the interact key works the doors (it goes before a vehicle in reach, as on the server).</summary>
    public string? GarageDoorPrompt(bool driving)
    {
        if (driving || _ctx.Dead || _ctx.Me is not { } me || GarageDoorNode.Current is not { } doors) return null;
        var p = me.GlobalPosition;
        if (!doors.Garage.InReach(new NVec3(p.X, p.Y, p.Z))) return null;
        return Strings.F(doors.IsOpen ? "hud.garage.close" : "hud.garage.open", InputBindings.Name("interact"));
    }
}

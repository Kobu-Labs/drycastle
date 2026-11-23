using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The client's side of the bus garage's doors: the server's state (<see cref="EventKind.GarageDoor"/>) sets the box and swings the leaves, and a player on foot at the
/// doorway sees the prompt. Nothing is sent: the server sees the interact key in the inputs and decides (<c>GarageServer.Use</c>).
/// </summary>
public partial class NetClient
{
    /// <summary>The garage doors' event: a joiner sees open doors swing open once.</summary>
    void OnGarageDoorEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.GarageDoor || GarageDoorCodec.Decode(payload) is not { } open) return;
        GarageDoorNode.Current?.SetOpen(open, animate: true);
    }

    /// <summary>On foot at the doorway: the interact key works the doors (it goes before a vehicle in reach, as on the server).</summary>
    string? GarageDoorPrompt()
    {
        if (_driving || Dead || _me is not { } me || GarageDoorNode.Current is not { } doors) return null;
        var p = me.GlobalPosition;
        if (!doors.Garage.InReach(new NVec3(p.X, p.Y, p.Z))) return null;
        return Strings.F(doors.IsOpen ? "hud.garage.close" : "hud.garage.open", InputBindings.Name("interact"));
    }
}

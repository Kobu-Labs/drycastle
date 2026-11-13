using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The church doors on the client (ADR 0019): the server's state sets the doors' box and swings the leaves.</summary>
public partial class NetClient
{
    void OnChurchEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.ChurchDoor || ChurchDoorCodec.Decode(payload) is not { } open) return;
        ChurchDoorNode.Current?.SetOpen(open, animate: true); // a joiner sees open doors swing open once
    }
}

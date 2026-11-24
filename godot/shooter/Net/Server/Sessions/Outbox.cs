using Godot;
using Drycastle.Shooter.Net.Server.Diagnostics;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Sessions;

/// <summary>
/// What the server sends: a reliable message to one player, an event to every joined client, a packet on an unreliable channel. Every packet goes through here, so the bytes
/// each client was sent are counted once, the perf audit sees each kind, and the network impairment of the loss test (<see cref="NetSim"/>) applies to all of them.
/// </summary>
internal sealed class Outbox
{
    readonly Dictionary<ENetPacketPeer, ServerClient> _clients;
    readonly NetSim? _sim;
    readonly PerfHarness _perf;
    readonly Action<EventKind, byte[]> _onEvent;

    /// <param name="onEvent">What else hears every broadcast event (the rozhlas live feed mixes the shots, launches and blasts inside the Urad).</param>
    public Outbox(Dictionary<ENetPacketPeer, ServerClient> clients, NetSim? sim, PerfHarness perf, Action<EventKind, byte[]> onEvent)
    {
        _clients = clients; _sim = sim; _perf = perf; _onEvent = onEvent;
    }

    /// <summary>A reliable message to one player. A bot has no connection: nothing is encoded or sent.</summary>
    public void Send(ServerClient c, ReliableMsg m)
    {
        if (c.Peer is { } peer) Tx(peer, Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>A packet on one of the unreliable channels to one player; a bot gets nothing.</summary>
    public void SendUnreliable(ServerClient c, int channel, byte[] packet)
    {
        if (c.Peer is { } peer) Tx(peer, channel, packet, 0);
    }

    /// <summary>Broadcast an event envelope to every joined client (shot, hit, kill, ... from later issues).</summary>
    public void Broadcast(EventKind kind, byte[] payload)
    {
        var profT = Prof.Begin(Prof.S.BroadcastEvent);
        ReliableMsg m = new EventMsg((byte)kind, payload);
        _perf.EventSent(kind, payload);
        _onEvent(kind, payload);
        var bytes = Wire.Encode(m); // encoded once: every recipient gets the same bytes (Tx copies them into the packet)
        // effects that may be lost go unreliable on their own channel, so they never hold back the reliable events (A-03)
        var effect = Wire.IsEffect(kind);
        foreach (var c in _clients.Values)
            if (c.Joined) Tx(c.Peer!, effect ? Wire.ChanEffects : Wire.ChanReliable, bytes, effect ? 0 : (int)ENetPacketPeer.FlagReliable);
        Prof.End(Prof.S.BroadcastEvent, profT);
    }

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (_clients.TryGetValue(peer, out var c)) c.BytesOut += data.Length;
        _perf.Audit?.Sent(chan, data);
        if (_sim != null) _sim.Send(peer, chan, data, flags); else peer.Send(chan, data, flags);
    }
}

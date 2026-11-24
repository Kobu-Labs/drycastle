using Godot;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The ENet connection to the server (ADR 0002): connects, sends the reliable events, the inputs and the loudspeaker's mic frames (through the loss test's <see cref="NetSim"/> when one is set),
/// and reads what arrives. What it means is for the client: it only hands over the hello to send, the packets and the end of the link.
/// </summary>
internal sealed class ServerLink
{
    public ENetConnection Conn { get; } = new();
    public ENetPacketPeer? Peer { get; private set; }
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim;

    /// <summary>Starts connecting; the failure as text, or null.</summary>
    public string? Connect(string host, int port)
    {
        var err = Conn.CreateHost(1, Wire.Channels, 0, 0);
        if (err != Error.Ok) return $"CreateHost failed: {err}";
        Peer = Conn.ConnectToHost(host, port, Wire.Channels, 0);
        return Peer == null ? "ConnectToHost failed" : null;
    }

    /// <summary>Says goodbye and drops the peer.</summary>
    public void Disconnect()
    {
        if (Peer == null) return;
        Peer.PeerDisconnect();
        Conn.Flush();
        Peer = null;
    }

    public void Destroy() => Conn.Destroy();

    /// <summary>Delayed packets of the net simulation go out at frame rate, not only on physics ticks.</summary>
    public void Pump()
    {
        if (Sim != null && Sim.Pump()) Conn.Flush();
    }

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (Sim != null) Sim.Send(peer, chan, data, flags); else peer.Send(chan, data, flags);
    }

    /// <summary>Sends one reliable event to the server (nothing when not connected).</summary>
    public void Send(EventKind kind, byte[] payload)
    {
        if (Peer == null) return;
        Tx(Peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)kind, payload)), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>Sends a reliable message at once, flushed; false when not connected.</summary>
    public bool SendNow(ReliableMsg m)
    {
        if (Peer == null) return false;
        Tx(Peer, Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);
        Conn.Flush();
        return true;
    }

    /// <summary>Sends the encoded inputs, flushed.</summary>
    public void SendInputs(byte[] data)
    {
        Tx(Peer!, Wire.ChanData, data, 0);
        Conn.Flush();
    }

    /// <summary>The loudspeaker holder's mic frame (client to server, unreliable).</summary>
    public void SendMic(byte[] packet)
    {
        if (Peer != null) Tx(Peer, Wire.ChanRozhlasFeed, packet, 0);
    }

    /// <summary>Reads what the network brought: <paramref name="hello"/> is sent when the connection is made, <paramref name="gone"/> is called when the server ends it (and reading stops).</summary>
    public void Poll(Func<HelloMsg> hello, Action gone, Action<byte[]> received)
    {
        for (var guard = 0; guard < 10000; guard++)
        {
            var r = Conn.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev is ENetConnection.EventType.None or ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect:
                    Tx(peer, Wire.ChanReliable, Wire.Encode(hello()), (int)ENetPacketPeer.FlagReliable);
                    Conn.Flush();
                    break;
                case ENetConnection.EventType.Disconnect:
                    gone();
                    return;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0) received(peer.GetPacket());
                    break;
            }
        }
    }
}

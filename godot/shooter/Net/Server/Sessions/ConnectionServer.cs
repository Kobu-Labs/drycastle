using Godot;
using Drycastle.Shooter.Net.Server.Diagnostics;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.Net.Server.Sessions;

/// <summary>
/// The server's connections: the ENet events (a peer connects, disconnects or sends packets, at most <see cref="PacketsPerTick"/> read per peer and tick), the hello that
/// opens a session (protocol and map checks, one live session per token) and the connections that are dropped or turned away.
/// </summary>
internal sealed class ConnectionServer
{
    /// <summary>Players the server takes; the ENet host has one more slot, so a connection past this is told the server is full.</summary>
    public const int MaxPeers = 32;
    /// <summary>How long a connection may stay without a hello before it is dropped (#197); it holds one of <see cref="MaxPeers"/> slots.</summary>
    public const double HelloDeadlineSeconds = 5;
    /// <summary>Packets one peer may send in one tick; excess packets are read and dropped. Inputs arrive at one packet per tick, reliable messages a handful.</summary>
    const int PacketsPerTick = 64;

    readonly ServerContext _ctx;
    readonly ENetConnection _host;
    readonly Dictionary<ENetPacketPeer, ServerClient> _clients;
    readonly PlayerRoster _roster;
    readonly ServerHealth _health;
    readonly SandboxServer _sandbox;
    readonly ProfileServer _profiles;
    readonly Drycastle.MapLoader.MapLoader? _map;

    public ConnectionServer(ServerContext ctx, ENetConnection host, Dictionary<ENetPacketPeer, ServerClient> clients, PlayerRoster roster, ServerHealth health,
        SandboxServer sandbox, ProfileServer profiles, Drycastle.MapLoader.MapLoader? map)
    {
        _ctx = ctx; _host = host; _clients = clients; _roster = roster; _health = health; _sandbox = sandbox; _profiles = profiles; _map = map;
    }

    /// <summary>Network in: the connects, disconnects and packets of this tick; each packet that is within the peer's budget goes to <paramref name="onPacket"/>.</summary>
    public void Poll(Action<ENetPacketPeer, byte[]> onPacket)
    {
        foreach (var cl in _clients.Values) cl.PacketsThisTick = 0;
        for (var guard = 0; guard < 10000; guard++)
        {
            var r = _host.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev is ENetConnection.EventType.None or ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect:
                    if (_clients.Count >= MaxPeers) Reject(new ServerClient { Peer = peer }, $"The server is full ({MaxPeers} players). Try again later.");
                    else _clients[peer] = new ServerClient { Peer = peer, ConnectedAt = _ctx.Now };
                    break;
                case ENetConnection.EventType.Disconnect: Drop(peer); break;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0)
                    {
                        var pkt = peer.GetPacket();
                        if (_clients.TryGetValue(peer, out var rc) && ++rc.PacketsThisTick > PacketsPerTick) { _health.DroppedPackets++; continue; }
                        onPacket(peer, pkt);
                    }
                    break;
            }
        }
    }

    /// <summary>The first message of a connection: a session starts when the protocol and the map build match and there is a free player id; the profile load then decides when it joins.</summary>
    public void OnHello(ServerClient c, HelloMsg h)
    {
        if (h.Version != Wire.ProtocolVersion)
        {
            Reject(c, $"Protocol mismatch: this server speaks protocol {Wire.ProtocolVersion}, your game speaks {h.Version}. Update the game to the server's build.");
            return;
        }
        // the map build decides spawns, tree and parcel solids, glass indices and water: a different build would desync silently (E-06)
        if (_map != null && _map.BuildHash != 0 && h.MapHash != _map.BuildHash)
        {
            Reject(c, $"Map mismatch: this server runs map {_map.MapVersion} (build {_map.BuildHash:x16}), and your game has a different map build. Install the server's map folder, then start again.");
            return;
        }
        c.Id = _roster.NewPlayerId();
        if (c.Id == 0) { Reject(c, "The server is full."); return; }
        var name = LoadoutRules.SanitizePlayerName(h.Name);
        c.Name = name.Length == 0 ? $"player{c.Id}" : name;
        _sandbox.OnHello(c, h.Flags);
        c.Token = h.Token;
        DropSessionsWithToken(c); // a quick reconnect wins over its ghost, which would otherwise run on the same profile until its peer times out
        _profiles.StartLoad(c, h.Token); // joins once the profile is in (or the wait times out)
        GD.Print($"server: {c.Name} #{c.Id} hello, loading profile");
    }

    /// <summary>Drops the connections that sent no hello in time.</summary>
    public void DropSilentPeers()
    {
        foreach (var c in _clients.Values.Where(c => c.Phase is SessionPhase.Connected or SessionPhase.Rejected && _ctx.Now - c.ConnectedAt > HelloDeadlineSeconds).ToList())
        {
            GD.Print("server: dropping a connection that sent no hello");
            Drop(c.Peer!);
            c.Peer!.PeerDisconnectLater(0);
        }
    }

    void Drop(ENetPacketPeer peer)
    {
        if (!_clients.Remove(peer, out var c)) return;
        if (c.Joined) _roster.Leave(c);
    }

    /// <summary>Drops every other client that uses <paramref name="keep"/>'s token, so a profile never has two live sessions.</summary>
    void DropSessionsWithToken(ServerClient keep)
    {
        if (keep.Token.Length == 0) return;
        foreach (var o in _clients.Values.Where(o => o != keep && o.Token == keep.Token).ToList())
        {
            GD.Print($"server: {o.Name} #{o.Id} replaced by a new session with the same token");
            Drop(o.Peer!);
            o.Peer!.PeerDisconnectLater(0);
        }
    }

    void Reject(ServerClient c, string reason)
    {
        c.Session.Enter(SessionPhase.Rejected);
        GD.Print($"server: rejecting {(c.Name == "" ? "a client" : c.Name)}: {reason}");
        _ctx.Send(c, new ErrorMsg(reason));
        _host.Flush();
        c.Peer!.PeerDisconnectLater(0); // after the error has gone out
    }
}

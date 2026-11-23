using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// What a server feature (<c>LampsServer</c>, <c>MineralsServer</c>, ...) takes from the rest of the server: who is playing, the match, the clock and the two
/// ways to send. It reaches nothing else of <see cref="NetServer"/>; a feature that needs more is given it by its constructor, or by the feature that owns it.
/// </summary>
internal sealed class ServerContext
{
    readonly Func<double> _now;
    readonly Func<uint> _tick;
    readonly Action<EventKind, byte[]> _broadcast;
    readonly Action<ServerClient, ReliableMsg> _send;

    public ServerContext(MatchSim match, IReadOnlyDictionary<ushort, ServerClient> players, IEnumerable<ServerClient> connections,
        Func<double> now, Func<uint> tick, Action<EventKind, byte[]> broadcast, Action<ServerClient, ReliableMsg> send)
    {
        Match = match; Players = players; Connections = connections; _now = now; _tick = tick; _broadcast = broadcast; _send = send;
    }

    public MatchSim Match { get; }

    /// <summary>The joined players by id, bots included.</summary>
    public IReadOnlyDictionary<ushort, ServerClient> Players { get; }

    /// <summary>Every connection, joined or not (the values of the server's peer table).</summary>
    public IEnumerable<ServerClient> Connections { get; }

    /// <summary>Server seconds since the first tick.</summary>
    public double Now => _now();

    public uint Tick => _tick();

    /// <summary>An event to every joined client.</summary>
    public void Broadcast(EventKind kind, byte[] payload) => _broadcast(kind, payload);

    /// <summary>A reliable message to one player; a bot has no connection and gets nothing.</summary>
    public void Send(ServerClient c, ReliableMsg m) => _send(c, m);

    /// <summary>A reliable event to one player.</summary>
    public void SendEvent(ServerClient c, EventKind kind, byte[] payload) => _send(c, new EventMsg((byte)kind, payload));

    /// <summary>Whether the player is alive in the match (not dead, not waiting to respawn).</summary>
    public bool IsAlive(ushort id) => Match.Get(id)?.Life == PlayerLife.Alive;
}

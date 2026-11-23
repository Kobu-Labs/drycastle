using Godot;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>The scoreboard: sent once a tick at most, however many kills the tick had (A-04), and about once a second regardless, because the time left and the pings move.</summary>
internal sealed class ScoreboardServer
{
    readonly ServerContext _ctx;
    bool _dirty;

    public ScoreboardServer(ServerContext ctx) => _ctx = ctx;

    /// <summary>The table changed: it goes out when the tick's events are in (<see cref="Flush"/>).</summary>
    public void Changed() => _dirty = true;

    /// <summary>Sends the table when it changed this tick, and about once a second regardless. A kill used to resend the whole table at once, so a tick with three kills sent it three times.</summary>
    public void Flush()
    {
        var due = _dirty || _ctx.Tick % Wire.TickRate == 0;
        _dirty = false;
        if (due && _ctx.Connections.Any()) _ctx.Broadcast(EventKind.MatchState, MatchCodec.Encode(State()));
    }

    MatchStateMsg State()
    {
        var match = _ctx.Match;
        var rows = match.Ranking().Select(p => new ScoreRow(p.Id, p.Sandbox ? p.Name + " [SANDBOX]" : p.Name, p.Kills, p.Deaths, Ping(p.Id), p.Team, p.Sandbox, p.Bot, p.Squad)).ToList();
        return new MatchStateMsg(match.Phase, match.TimeRemaining, match.Winners, rows, match.Mode, match.TeamScore(Teams.A), match.TeamScore(Teams.B), match.SquadList);
    }

    ushort Ping(ushort id) =>
        _ctx.Players.TryGetValue(id, out var c) && c.Peer is { } peer ? (ushort)Math.Clamp(peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime), 0, ushort.MaxValue) : (ushort)0; // a bot has no ping
}

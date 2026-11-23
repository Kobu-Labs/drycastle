using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The Teams screen's side of the client (#312): who may open it, and the admin's requests. The server answers through the scoreboard rows (team, pin and bot per player).</summary>
public sealed class TeamsClient
{
    readonly ClientContext _ctx;
    readonly ChatClient _chat;

    public TeamsClient(ClientContext ctx, ChatClient chat) { _ctx = ctx; _chat = chat; }

    /// <summary>An admin in a team deathmatch: the Pause menu offers Teams. The server checks both again.</summary>
    public bool CanManage => _chat.Context.IsAdmin && _ctx.Match.IsTeamMode;

    public void Assign(TeamAssign a)
    {
        if (!_ctx.Connected() || !CanManage) return;
        _ctx.Send(EventKind.TeamAssign, TeamAssignCodec.Encode(a));
    }
}

using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.HudFeeds;

/// <summary>The Squads screen's side of the client (#379): who may open it, and the player's requests. The server answers through the scoreboard rows (squad per player, squad names) and, for a refusal, a chat line.</summary>
public sealed class SquadsClient
{
    const double AnswerWindow = 3.0;

    readonly ClientContext _ctx;
    double _askedAt = double.NegativeInfinity;

    public SquadsClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The server refused a squad request just sent (its error chat line, within <see cref="AnswerWindow"/>), for the Squads screen to show.</summary>
    public event Action<string>? Refused;

    /// <summary>Team deathmatch, a human on a team: the Pause menu offers Squads. The server checks it again.</summary>
    public bool CanUse => !_ctx.Bot && _ctx.Match.IsTeamMode && _ctx.Match.TeamOf(_ctx.MyId) != Teams.None;

    public void Assign(SquadAssign a)
    {
        if (!_ctx.Connected() || !CanUse) return;
        _askedAt = _ctx.Now;
        _ctx.Send(EventKind.SquadAssign, SquadAssignCodec.Encode(a));
    }

    /// <summary>An error chat line that answers a squad request is also shown on the Squads screen.</summary>
    public void OnChatLine(ChatLine line)
    {
        if (line.Kind == ChatLineKind.Error && _ctx.Now - _askedAt < AnswerWindow) { _askedAt = double.NegativeInfinity; Refused?.Invoke(line.Text); }
    }
}

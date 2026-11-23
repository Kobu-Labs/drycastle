using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter;

/// <summary>The Squads screen's side of the client (#379): who may open it, and the player's requests. The server answers through the scoreboard rows (squad per player, squad names) and, for a refusal, a chat line.</summary>
public partial class NetClient
{
    /// <summary>Team deathmatch, a human on a team: the Pause menu offers Squads. The server checks it again.</summary>
    public bool CanUseSquads => !Bot && Match.IsTeamMode && Match.TeamOf(_myId) != Teams.None;

    /// <summary>The server refused a squad request just sent (its error chat line, within <see cref="SquadAnswerWindow"/>), for the Squads screen to show.</summary>
    public event Action<string>? SquadRefused;

    const double SquadAnswerWindow = 3.0;
    double _squadAskedAt = double.NegativeInfinity;

    public void SendSquadAssign(SquadAssign a)
    {
        if (_peer == null || !CanUseSquads) return;
        _squadAskedAt = Now;
        SendSandboxEvent(EventKind.SquadAssign, SquadAssignCodec.Encode(a));
    }

    /// <summary>An error chat line that answers a squad request is also shown on the Squads screen.</summary>
    void SquadAnswer(ChatLine line)
    {
        if (line.Kind == ChatLineKind.Error && Now - _squadAskedAt < SquadAnswerWindow) { _squadAskedAt = double.NegativeInfinity; SquadRefused?.Invoke(line.Text); }
    }
}

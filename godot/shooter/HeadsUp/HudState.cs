using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>What several HUD widgets read of the match and the local player (the top bar, the scoreboard, the kill feed, the XP panel): the owner sets it through <see cref="Hud"/>.</summary>
internal sealed class HudState
{
    /// <summary>Id of the local player (scoreboard highlight, score line).</summary>
    public ushort MyId { get; set; }
    /// <summary>True while the regular HUD is hidden for a control session (see <see cref="Hud.SetControlMode"/>).</summary>
    public bool ControlMode { get; set; }
    public MatchPhase Phase { get; set; } = MatchPhase.Waiting;
    public IReadOnlyList<ScoreRow> Rows { get; set; } = Array.Empty<ScoreRow>();
    public IReadOnlyList<ushort> Winners { get; set; } = Array.Empty<ushort>();
    public Func<ushort, string> NameOf { get; set; } = id => Strings.F("player.unknown", id);
    /// <summary>Team deathmatch: the client's match state, read for teams and team scores; null or free-for-all hides every team cue.</summary>
    public ClientMatchState? Team { get; set; }

    public bool TeamMode => Team is { IsTeamMode: true };
    public byte TeamOf(ushort id) => Team?.TeamOf(id) ?? Teams.None;
    public byte MyTeam => TeamOf(MyId);

    /// <summary>Friend or enemy colour of <paramref name="team"/> as seen by the local player (the HUD text colour when neutral).</summary>
    public Color? RelationColor(byte team) => HudLogic.RelationOf(MyTeam, team) switch
    {
        HudLogic.Relation.Friend => ShooterTheme.Friend,
        HudLogic.Relation.Enemy => ShooterTheme.Enemy,
        _ => null,
    };

    /// <summary>A name with its team tag in team deathmatch (so the team is readable without colour), plain otherwise.</summary>
    public string Tagged(string name, byte team) => TeamMode && team != Teams.None ? Strings.F("score.team.tag", Teams.Tag(team), name) : name;
}

using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.HeadsUp.RoundFlow;

/// <summary>The top centre panel: the match timer (counted down on the HUD clock) and the score line.</summary>
internal sealed class TopBar
{
    readonly HudState _state;
    readonly Label _timer, _scoreLine;
    float _timeLeft;
    double _timeAt;
    bool _running;
    int _shown = -1;

    public TopBar(Control root, HudState state)
    {
        _state = state;
        var p = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 0, 0, ShooterTheme.Margin, 224, 64);
        root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        _timer = HudUi.L("", "HudHead", HorizontalAlignment.Center); v.AddChild(_timer);
        _scoreLine = HudUi.L("", "HudSmall", HorizontalAlignment.Center); _scoreLine.ClipText = true; v.AddChild(_scoreLine);
    }

    /// <summary>The phase, rows and team are already in the state; <paramref name="timeRemaining"/> is the seconds left.</summary>
    public void Set(float timeRemaining)
    {
        _timeLeft = timeRemaining; _timeAt = HudUi.Now; _running = _state.Phase == MatchPhase.Running; _shown = -1;
        var line = HudLogic.Score(_state.Rows, _state.MyId);
        HudUi.Set(_scoreLine, _state.Phase switch
        {
            MatchPhase.Waiting => Strings.T("hud.phase.waiting"),
            MatchPhase.Ending => Strings.T("hud.phase.ending"),
            _ when _state.TeamMode => TeamScoreText(),
            _ => line.IAmLeader ? Strings.F("hud.score.leading", line.MyKills) : Strings.F("hud.score.behind", line.MyKills, line.LeaderName, line.LeaderKills),
        });
        UpdateTimer();
    }

    string TeamScoreText()
    {
        var team = _state.Team!;
        var (mine, theirs, side) = HudLogic.TeamScores(_state.MyTeam, team.ScoreA, team.ScoreB);
        return Strings.F("hud.score.team", Teams.Tag(side), mine, theirs);
    }

    public void Tick()
    {
        if (_running) UpdateTimer();
    }

    void UpdateTimer()
    {
        var left = _running ? Math.Max(0f, _timeLeft - (float)(HudUi.Now - _timeAt)) : _timeLeft;
        var shown = HudLogic.CeilSeconds(left);
        if (shown == _shown) return;
        _shown = shown;
        HudUi.Set(_timer, _state.Phase == MatchPhase.Waiting ? Strings.T("hud.timer.waiting") : _state.OpenWorld && _state.Phase == MatchPhase.Running ? Strings.T("hud.timer.open") : HudLogic.TimeText(left));
    }
}

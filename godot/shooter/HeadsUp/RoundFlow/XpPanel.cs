using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.HeadsUp.RoundFlow;

/// <summary>The post-match XP panel (ticket #118): under the scoreboard in the ending phase, one line per reason with its count and XP.</summary>
internal sealed class XpPanel
{
    readonly HudState _state;
    readonly PanelContainer _panel;
    readonly VBoxContainer _box;
    XpSummaryInfo? _info;

    public XpPanel(Control root, HudState state)
    {
        _state = state;
        _panel = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin - 200, 720, 0);
        _panel.GrowVertical = Control.GrowDirection.Begin; // bottom-anchored: grows upward as lines are added
        _panel.Visible = false;
        _box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(_box);
        root.AddChild(_panel);
    }

    /// <summary>The panel, for the overlays that must stay below it.</summary>
    public Control Panel => _panel;

    /// <summary>The round's XP summary from the server; shown in the ending phase.</summary>
    public void Set(XpSummaryInfo? summary)
    {
        _info = summary;
        Rebuild();
        Refresh();
    }

    public void Refresh() => _panel.Visible = _state.Phase == MatchPhase.Ending && _info != null;

    void Rebuild()
    {
        foreach (var c in _box.GetChildren()) c.QueueFree();
        if (_info is not { } info) return;
        _box.AddChild(HudUi.L(Strings.T("xp.title"), "HudHead"));
        // one line per reason: "Kill x3   +300 XP"
        foreach (var g in info.Lines.GroupBy(l => l.Reason))
        {
            var name = Strings.T("xp.reason." + g.Key) + (g.Count() > 1 ? $" x{g.Count()}" : "");
            _box.AddChild(HudUi.L(Strings.F("xp.line", name, g.Sum(l => l.Amount)), "HudDim"));
        }
        _box.AddChild(HudUi.L(Strings.F("xp.total", info.Total), "HudBig", HorizontalAlignment.Center));
        _box.AddChild(HudUi.L(Strings.F("xp.level", info.Level), "HudDim", HorizontalAlignment.Center));
    }
}

using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The scoreboard (hold Tab, and on the end screen): one table in free-for-all, two side by side in team deathmatch (own team left, so the sides never swap with the lead)
/// and the players without a team below. Every row is built once; a refill happens only when the rows or the screen changed.
/// </summary>
internal sealed class Scoreboard
{
    const int Rows = 16;
    const float Width = 800, WidthTeams = 2 * 640 + 2 * ShooterTheme.Space;

    /// <summary>One table: optional team heading, column header, pre-built rows, empty line.</summary>
    sealed class Table
    {
        public VBoxContainer Root = null!;
        public Label Head = null!, Empty = null!;
        public readonly PanelContainer[] Row = new PanelContainer[Rows];
        public readonly Label[][] Cells = new Label[Rows][];
    }

    readonly Control _root;
    readonly HudState _state;
    readonly PanelContainer _panel;
    readonly Label _title, _foot;
    /// <summary>Left table (everyone in free-for-all, own team in team deathmatch), right table (other team), and players without a team below.</summary>
    readonly Table[] _tables = { new(), new(), new() };
    bool _shown, _dirty = true;

    public Scoreboard(Control root, HudState state)
    {
        _root = root; _state = state;
        _panel = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 0.5f, 0, 0, Width, 0);
        _panel.Visible = false;
        root.AddChild(_panel);
        // grow from the centre in both directions
        _panel.GrowHorizontal = Control.GrowDirection.Both; _panel.GrowVertical = Control.GrowDirection.Both;
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(v);
        _title = HudUi.L("", "HudBig", HorizontalAlignment.Center); _title.ClipText = true; v.AddChild(_title);
        var side = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        side.AddThemeConstantOverride("separation", 2 * ShooterTheme.Space);
        v.AddChild(side);
        side.AddChild(BuildTable(_tables[0])); side.AddChild(BuildTable(_tables[1]));
        v.AddChild(BuildTable(_tables[2]));
        _foot = HudUi.L("", "HudSmall", HorizontalAlignment.Center); v.AddChild(_foot);
    }

    /// <summary>The panel, for the overlays that must stay below it.</summary>
    public Control Panel => _panel;

    /// <summary>The rows or the screen's width changed: refill while shown.</summary>
    public void MarkDirty() => _dirty = true;

    static VBoxContainer BuildTable(Table t)
    {
        var v = t.Root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        t.Head = HudUi.L("", "HudHead", HorizontalAlignment.Center); t.Head.ClipText = true; v.AddChild(t.Head);
        v.AddChild(BuildRow(out _, true));
        for (var i = 0; i < Rows; i++)
        {
            t.Row[i] = BuildRow(out t.Cells[i], false);
            v.AddChild(t.Row[i]);
        }
        t.Empty = HudUi.L(Strings.T("score.empty"), "HudDim", HorizontalAlignment.Center); v.AddChild(t.Empty);
        return v;
    }

    static PanelContainer BuildRow(out Label[] cells, bool header)
    {
        var p = HudUi.P(header ? "HudBare" : "HudRow"); p.CustomMinimumSize = new Vector2(0, 32);
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddChild(h);
        cells = new[]
        {
            HudUi.L(header ? Strings.T("score.col.player") : "", header ? "HudSmall" : null), HudUi.L(header ? Strings.T("score.col.kills") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 80),
            HudUi.L(header ? Strings.T("score.col.deaths") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 80), HudUi.L(header ? Strings.T("score.col.ping") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 112),
        };
        cells[0].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; cells[0].ClipText = true;
        foreach (var c in cells) h.AddChild(c);
        if (!header) p.Visible = false;
        return p;
    }

    /// <summary>Per frame: shown while Tab is held (not typing in the chat box) and on the end screen.</summary>
    public void Tick(bool chatOpen)
    {
        var want = (Input.IsActionPressed("scoreboard") && !chatOpen) || _state.Phase == MatchPhase.Ending; // Tab completes in the chat box
        if (want != _shown || (want && _dirty)) Show(want);
    }

    void Show(bool show)
    {
        _shown = show; _dirty = false;
        _panel.Visible = show;
        if (!show) return;
        var ending = _state.Phase == MatchPhase.Ending;
        if (ending)
        {
            var winners = _state.Winners;
            var names = string.Join(", ", winners.Select(_state.NameOf));
            if (_state.TeamMode && winners.Count > 0) HudUi.Set(_title, Strings.F("score.ending.team", Teams.Tag(_state.TeamOf(winners[0]))));
            else HudUi.Set(_title, names == "" ? Strings.T("hud.phase.ending") : Strings.F(winners.Count > 1 ? "score.ending.tie" : "score.ending", names));
        }
        else HudUi.Set(_title, Strings.T("score.title"));
        HudUi.Set(_foot, Strings.T(ending ? "score.hint.next" : "score.hint.hold"));
        var w = Math.Min(_state.TeamMode ? WidthTeams : Width, _root.Size.X - 2 * ShooterTheme.Margin);
        _panel.OffsetLeft = -w / 2; _panel.OffsetRight = w / 2;
        if (!_state.TeamMode)
        {
            Fill(_tables[0], HudLogic.Sort(_state.Rows), Teams.None);
            _tables[1].Root.Visible = _tables[2].Root.Visible = false;
            return;
        }
        var split = HudLogic.SplitTeams(_state.Rows, _state.MyTeam);
        Fill(_tables[0], split.Left, split.LeftTeam);
        Fill(_tables[1], split.Right, Teams.Other(split.LeftTeam));
        _tables[2].Root.Visible = split.NoTeam.Count > 0;
        if (split.NoTeam.Count > 0) Fill(_tables[2], split.NoTeam, Teams.None);
    }

    /// <summary>Fills one table with already sorted rows; <paramref name="team"/> names its heading (free-for-all has none).</summary>
    void Fill(Table t, List<ScoreRow> sorted, byte team)
    {
        t.Root.Visible = true;
        t.Head.Visible = _state.TeamMode;
        if (_state.TeamMode)
        {
            var key = HudLogic.RelationOf(_state.MyTeam, team) switch { HudLogic.Relation.Friend => "score.team.mine", HudLogic.Relation.Enemy => "score.team.enemy", _ => "score.team.other" };
            var scores = _state.Team!;
            HudUi.Set(t.Head, team == Teams.None ? Strings.T("score.team.none") : Strings.F(key, Teams.Tag(team), team == Teams.A ? scores.ScoreA : scores.ScoreB));
            HudUi.Tint(t.Head, _state.RelationColor(team));
        }
        t.Empty.Visible = sorted.Count == 0;
        // keep the local row visible even when the table exceeds the pre-built rows
        if (sorted.Count > Rows)
        {
            var me = sorted.FindIndex(r => r.Id == _state.MyId);
            if (me >= Rows) sorted[Rows - 1] = sorted[me];
        }
        for (var i = 0; i < Rows; i++)
        {
            if (i >= sorted.Count) { t.Row[i].Visible = false; continue; }
            var r = sorted[i];
            var mine = r.Id == _state.MyId;
            t.Row[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
            var c = t.Cells[i];
            var who = mine ? Strings.F("score.you", r.Name) : r.Name; // the team is the table's heading
            if (r.Squad != 0 && _state.Team?.SquadNameOf(r.Squad) is { Length: > 0 } squad) who = Strings.F("squad.tag", who, squad); // the squad's members sit together, the name says which
            HudUi.Set(c[0], who);
            HudUi.Tint(c[0], _state.RelationColor(r.Team));
            HudUi.Set(c[1], r.Kills.ToString()); HudUi.Set(c[2], r.Deaths.ToString()); HudUi.Set(c[3], Strings.F("score.ping", r.Ping));
            t.Row[i].Visible = true;
        }
    }
}

using Godot;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter;

/// <summary>English strings of the Squads screen (#379).</summary>
public static class SquadsStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("pause.squads", "Squads"),
        ("squad.tag", "{0} [{1}]"),
        ("squads.title", "Squads"),
        ("squads.hint", "A squad is a small group inside your team, up to 4 players. Squad mates are blue on the minimap and the map, with their names, and their name tags show the squad. Join one or start your own; you can be in one squad. Esc goes back."),
        ("squads.status.in", "Your squad: {0}  ({1}/{2})"), ("squads.status.none", "You are not in a squad."),
        ("squads.leave", "Leave squad"), ("squads.leave.tip", "Leave your squad. It disappears when its last player leaves."),
        ("squads.name", "New squad name"), ("squads.name.tip", "Up to {0} characters. Empty: \"{1}\"."),
        ("squads.create", "Create squad"), ("squads.create.tip", "Start a squad with this name and join it. You leave your current squad."),
        ("squads.taken", "Your team already has a squad with that name."),
        ("squads.list", "Team {0} squads   {1}"),
        ("squads.empty", "Your team has no squads yet. Create one above."),
        ("squads.count", "{0}/{1}"), ("squads.full", "{0}/{1}  FULL"),
        ("squads.join", "Join"), ("squads.join.tip", "Join this squad. You leave your current squad."),
        ("squads.join.full", "This squad is full."),
        ("squads.mine", "Your squad"),
        ("squads.refused", "The squad request did not work: {0}"),
    };
}

/// <summary>
/// The Squads screen (#379, Pause, team deathmatch): the player's status, a name field with Create, Leave, and the own team's squads as cards (name, count out of 4, members, Join). Opt-in: nobody is put in a
/// squad. A request applies at once through the server and the cards follow the scoreboard, so joins, leaves and other players' squads show live. Refilled only when the squads change. Keyboard: Tab or Up and Down
/// move between the controls, Enter presses (in the name field it creates), Esc goes back. A full squad and the own squad have no Join; both say so in words.
/// </summary>
public sealed partial class SquadsScreen : MenuScreen
{
    readonly NetClient _client;
    IReadOnlyList<ScoreRow>? _seen;
    string _signature = "";
    byte _focusSquad;
    LineEdit _name = null!;
    Button _create = null!, _leave = null!;
    Label _status = null!, _error = null!, _listHead = null!, _empty = null!;
    VBoxContainer _list = null!;
    readonly Dictionary<byte, Button> _joins = new();

    public SquadsScreen(NetClient client) { _client = client; }

    protected override Control Build()
    {
        _client.SquadRefused += Refused;
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(760, 0);
        root.AddThemeConstantOverride("separation", ShooterTheme.Space);

        var head = new PanelContainer { ThemeTypeVariation = "HudPanel" };
        var hv = Ui.VBox();
        var title = Ui.HBox();
        title.AddChild(Ui.Lbl(Strings.T("squads.title"), "HudDisplay"));
        title.AddChild(Ui.Fill());
        title.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        hv.AddChild(title);
        hv.AddChild(Ui.Lbl(Strings.T("squads.hint"), "HudDim", 0, true));

        var me = _client.Match.Rows.FirstOrDefault(r => r.Id == _client.MyId).Name ?? "";
        var maxLen = Squads.MaxNameLength;
        var make = Ui.HBox();
        _name = new LineEdit
        {
            PlaceholderText = Squads.DefaultName(me), MaxLength = maxLen, CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Strings.F("squads.name.tip", maxLen, Squads.DefaultName(me)), ContextMenuEnabled = false,
        };
        _name.TextChanged += _ => UpdateControls();
        _name.TextSubmitted += _ => Create();
        make.AddChild(_name);
        _create = Ui.Btn(Strings.T("squads.create"), Create, "MenuPrimary", 220);
        _create.TooltipText = Strings.T("squads.create.tip");
        make.AddChild(_create);
        hv.AddChild(make);
        _error = Ui.Lbl("", "MenuError"); // always there, so the card does not jump when it says something
        hv.AddChild(_error);

        var mine = Ui.HBox();
        _status = Ui.Lbl("", "HudHead");
        mine.AddChild(_status);
        mine.AddChild(Ui.Fill());
        _leave = Ui.Btn(Strings.T("squads.leave"), () => _client.SendSquadAssign(new SquadAssign(SquadAction.Leave)), null, 220);
        _leave.TooltipText = Strings.T("squads.leave.tip");
        mine.AddChild(_leave);
        hv.AddChild(mine);
        head.AddChild(hv);
        root.AddChild(head);

        var table = new PanelContainer { ThemeTypeVariation = "HudPanel", CustomMinimumSize = new Vector2(0, 320) };
        var tv = Ui.VBox();
        _listHead = Ui.Lbl("", "HudHead");
        tv.AddChild(_listHead);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _list = Ui.VBox();
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _list.AddThemeConstantOverride("separation", ShooterTheme.Space);
        scroll.AddChild(_list);
        tv.AddChild(scroll);
        _empty = Ui.Lbl(Strings.T("squads.empty"), "HudDim", 0, true);
        tv.AddChild(_empty);
        table.AddChild(tv);
        root.AddChild(table);

        Refresh(true);
        return root;
    }

    public override void _ExitTree() => _client.SquadRefused -= Refused;

    void Refused(string text) => Shell.Toast(Strings.F("squads.refused", text), ToastKind.Error);

    protected override Control? FirstFocusTarget() => _joins.Values.FirstOrDefault(b => !b.Disabled) ?? (Control)_name;

    public override void _Process(double delta)
    {
        if (!_client.CanUseSquads) { Back(); return; } // the match changed mode, or the player left their team
        if (!ReferenceEquals(_seen, _client.Match.Rows)) Refresh(false);
    }

    /// <summary>What the player would get from Create now: the typed name, cleaned, or the default.</summary>
    string WantedName()
    {
        var me = _client.Match.Rows.FirstOrDefault(r => r.Id == _client.MyId).Name ?? "";
        return Squads.CleanName(_name.Text) is { Length: > 0 } c ? c : Squads.DefaultName(me);
    }

    void Create()
    {
        if (_create.Disabled) return;
        _client.SendSquadAssign(new SquadAssign(SquadAction.Create, 0, _name.Text));
        _name.Text = "";
    }

    void Join(byte squad)
    {
        _focusSquad = squad; // the card is rebuilt: the focus follows it
        _client.SendSquadAssign(new SquadAssign(SquadAction.Join, squad));
    }

    IEnumerable<IGrouping<byte, ScoreRow>> OwnSquads(IReadOnlyList<ScoreRow> rows)
    {
        var team = _client.Match.TeamOf(_client.MyId);
        return rows.Where(r => r.Team == team && r.Squad != 0).GroupBy(r => r.Squad).OrderBy(g => g.Key);
    }

    /// <summary>Create is off while the team has a squad of that name, and says why; Leave is off while the player is in none.</summary>
    void UpdateControls()
    {
        var rows = _client.Match.Rows;
        var wanted = WantedName();
        var taken = OwnSquads(rows).Any(g => _client.Match.SquadNameOf(g.Key).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        _create.Disabled = taken;
        _error.Text = taken ? Strings.T("squads.taken") : "";
        _leave.Disabled = _client.Match.SquadOf(_client.MyId) == 0;
    }

    /// <summary>Redraws the cards when the squads or their members or the own squad changed (every new scoreboard also carries pings and kills that do not matter here).</summary>
    void Refresh(bool force)
    {
        var rows = _client.Match.Rows;
        _seen = rows;
        var squads = OwnSquads(rows).ToList();
        var mySquad = _client.Match.SquadOf(_client.MyId);
        var sig = string.Join('|', squads.Select(g => $"{g.Key}:{_client.Match.SquadNameOf(g.Key)}:{string.Join(',', g.Select(r => $"{r.Id}={r.Name}"))}")) + $"#{mySquad}";
        if (!force && sig == _signature) { UpdateControls(); return; }
        _signature = sig;
        var focus = _focusSquad != 0 ? _focusSquad : FocusedSquad();
        var hadFocus = GetViewport()?.GuiGetFocusOwner() is { } owner && _list.IsAncestorOf(owner);
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        _joins.Clear();
        _status.Text = mySquad == 0 ? Strings.T("squads.status.none") : Strings.F("squads.status.in", _client.Match.SquadNameOf(mySquad), squads.First(g => g.Key == mySquad).Count(), Squads.MaxMembers);
        _listHead.Text = Strings.F("squads.list", Teams.Tag(_client.Match.TeamOf(_client.MyId)), squads.Count);
        _empty.Visible = squads.Count == 0;
        foreach (var g in squads) _list.AddChild(Card(g.Key, g.ToList(), mySquad));
        UpdateControls();
        if (hadFocus || _focusSquad != 0) (_joins.GetValueOrDefault(focus) is { Disabled: false } b ? b : (Control)_name).CallDeferred(Control.MethodName.GrabFocus);
        _focusSquad = 0;
    }

    byte FocusedSquad()
    {
        var owner = GetViewport()?.GuiGetFocusOwner();
        foreach (var (id, b) in _joins) if (b == owner) return id;
        return 0;
    }

    Control Card(byte squad, List<ScoreRow> members, byte mySquad)
    {
        var mine = squad == mySquad;
        var card = new PanelContainer { ThemeTypeVariation = mine ? "HudRowMine" : "HudRow" };
        var h = Ui.HBox();
        h.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        var text = Ui.VBox();
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(Ui.Lbl(_client.Match.SquadNameOf(squad), "HudHead"));
        var names = members.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Id == _client.MyId ? Strings.F("score.you", r.Name) : r.Name);
        text.AddChild(Ui.Lbl(string.Join(", ", names), "HudDim", 0, true));
        h.AddChild(text);
        var full = members.Count >= Squads.MaxMembers;
        h.AddChild(Ui.Lbl(Strings.F(full ? "squads.full" : "squads.count", members.Count, Squads.MaxMembers), full ? "HudHead" : "HudDim", 120));
        if (mine) h.AddChild(Ui.Lbl(Strings.T("squads.mine"), "HudDim", 160));
        else
        {
            var join = Ui.Btn(Strings.T("squads.join"), () => Join(squad), null, 160);
            join.Disabled = full;
            join.TooltipText = Strings.T(full ? "squads.join.full" : "squads.join.tip");
            _joins[squad] = join;
            h.AddChild(join);
        }
        card.AddChild(h);
        return card;
    }
}

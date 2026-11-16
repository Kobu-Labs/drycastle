using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>English strings of the Teams screen (#312).</summary>
public static class TeamsStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("pause.teams", "Teams"),
        ("teams.title", "Teams"),
        ("teams.hint", "Drag a player card onto a team to move them; drop it on the Unassigned table to take them out of the round. Keyboard: Up/Down to pick a player, A or B to move them to a team, U to take them out. Esc goes back."),
        ("teams.head", "Team {0}   {1} players   Score {2}"),
        ("teams.head.none", "Unassigned   {0} players"),
        ("teams.empty", "Nobody on this team. Drop a player here."),
        ("teams.empty.none", "Nobody is out of the round. Drop a player here to take them out until you put them on a team."),
        ("teams.you", "{0} (you)"), ("teams.tag.bot", "BOT"),
        ("teams.act.bots_a", "Bots to team A"), ("teams.act.bots_b", "Bots to team B"),
        ("teams.act.shuffle", "Shuffle"), ("teams.act.balance", "Balance"),
        ("teams.act.bots_a.tip", "Every bot goes to team A."),
        ("teams.act.bots_b.tip", "Every bot goes to team B."),
        ("teams.act.shuffle.tip", "Deal everyone on a team at random into two even teams. Unassigned players stay out."),
        ("teams.act.balance.tip", "Even the teams out now: bots move first, then the newest players. Unassigned players stay out."),
        ("teams.shuffle.title", "Shuffle the teams?"), ("teams.shuffle.body", "Every player on a team is dealt to a random team and respawns there. Turrets and killstreak progress are lost. Kills and deaths stay."),
        ("teams.shuffle.confirm", "Shuffle"),
    };
}

/// <summary>
/// The Teams screen (#312, Pause, admins in team deathmatch): the two teams and the unassigned players as tables, over the paused game with no backdrop. Drag a player card
/// anywhere onto another table, or use the keyboard (A, B or U on a focused card). The table that can take the card lights its border. A move applies at once (the player
/// respawns on the new side, or waits when unassigned) and is undone by moving them back. Quick actions sit above the tables; shuffle asks first. The tables follow the
/// scoreboard, so joins, leaves and other admins show live. Refilled only when who stands where changes.
/// </summary>
public sealed partial class TeamsScreen : MenuScreen
{
    readonly NetClient _client;
    TeamColumn _a = null!, _b = null!, _none = null!;
    IReadOnlyList<ScoreRow>? _seen;
    string _signature = "";
    ushort _focusId;
    Button? _first;

    public TeamsScreen(NetClient client) : base("HudBare") { _client = client; }

    protected override Control Build()
    {
        _a = new TeamColumn(Teams.A, Move);
        _b = new TeamColumn(Teams.B, Move);
        _none = new TeamColumn(Teams.None, Move);
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(1120, 0);
        root.AddThemeConstantOverride("separation", ShooterTheme.Space);

        var head = new PanelContainer { ThemeTypeVariation = "HudPanel" };
        var hv = Ui.VBox();
        var title = Ui.HBox();
        title.AddChild(Ui.Lbl(Strings.T("teams.title"), "HudDisplay"));
        title.AddChild(Ui.Fill());
        title.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        hv.AddChild(title);
        hv.AddChild(Ui.Lbl(Strings.T("teams.hint"), "HudDim", 0, true));
        var actions = new HFlowContainer();
        void Act(string key, Action run)
        {
            var b = Ui.Btn(Strings.T("teams.act." + key), run, null, 200);
            b.TooltipText = Strings.T("teams.act." + key + ".tip");
            _first ??= b;
            actions.AddChild(b);
        }
        Act("bots_a", () => Send(new TeamAssign(TeamAction.MoveBots, 0, Teams.A)));
        Act("bots_b", () => Send(new TeamAssign(TeamAction.MoveBots, 0, Teams.B)));
        Act("balance", () => Send(new TeamAssign(TeamAction.Balance)));
        Act("shuffle", () => Shell.Push(new DialogScreen(Strings.T("teams.shuffle.title"), Strings.T("teams.shuffle.body"),
            new[] { new DialogScreen.Choice(Strings.T("teams.shuffle.confirm"), () => Send(new TeamAssign(TeamAction.Shuffle)), "MenuDanger") })));
        hv.AddChild(actions);
        head.AddChild(hv);
        root.AddChild(head);

        var tables = Ui.HBox();
        tables.AddThemeConstantOverride("separation", ShooterTheme.Space);
        tables.AddChild(_a);
        tables.AddChild(_b);
        root.AddChild(tables);
        root.AddChild(_none);

        Refresh(true);
        return root;
    }

    protected override Control? FirstFocusTarget() => _a.FirstRow ?? _b.FirstRow ?? _none.FirstRow ?? _first;

    void Send(TeamAssign a) => _client.SendTeamAssign(a);

    void Move(ushort id, byte team)
    {
        _focusId = id; // the card reappears on another table: the focus follows it
        Send(new TeamAssign(TeamAction.Move, id, team));
    }

    public override void _Process(double delta)
    {
        if (!_client.CanManageTeams) { Back(); return; } // no longer an admin, or the match changed mode
        if (!ReferenceEquals(_seen, _client.Match.Rows)) Refresh(false);
    }

    /// <summary>Redraws the tables when who is where or named changed (every new scoreboard also carries pings and kills that do not matter here). Sandbox players have no team and are not listed.</summary>
    void Refresh(bool force)
    {
        var rows = _client.Match.Rows.Where(r => !r.Sandbox).ToList();
        _seen = _client.Match.Rows;
        var sig = string.Join('|', rows.Select(r => $"{r.Id}:{r.Team}:{(r.Bot ? 1 : 0)}:{r.Name}")) + $"#{_client.Match.ScoreA}:{_client.Match.ScoreB}";
        if (!force && sig == _signature) return;
        _signature = sig;
        var focus = _focusId != 0 ? _focusId : FocusedRowId();
        _a.Show(rows, _client.Match.ScoreA, _client.MyId);
        _b.Show(rows, _client.Match.ScoreB, _client.MyId);
        _none.Show(rows, 0, _client.MyId);
        if (focus != 0 && (_a.RowOf(focus) ?? _b.RowOf(focus) ?? _none.RowOf(focus)) is { } row) row.CallDeferred(Control.MethodName.GrabFocus);
        _focusId = 0;
    }

    ushort FocusedRowId() => GetViewport()?.GuiGetFocusOwner() is TeamCard r ? r.PlayerId : (ushort)0;

    /// <summary>One table: a heading and its player cards. The whole table is the drop target, and its border lights while a card that it can take is over it.</summary>
    sealed partial class TeamColumn : PanelContainer
    {
        public readonly byte Team;
        readonly Action<ushort, byte> _move;
        readonly Label _head;
        readonly VBoxContainer _list = new();
        readonly Label _empty;
        readonly Dictionary<ushort, TeamCard> _rows = new();

        public TeamColumn(byte team, Action<ushort, byte> move)
        {
            Team = team; _move = move;
            ThemeTypeVariation = "HudPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(540, team == Teams.None ? 200 : 360);
            var v = Ui.VBox();
            _head = Ui.Lbl("", "HudHead");
            v.AddChild(_head);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
            _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(_list);
            v.AddChild(scroll);
            _empty = Ui.Lbl(Strings.T(team == Teams.None ? "teams.empty.none" : "teams.empty"), "HudDim", 0, true);
            v.AddChild(_empty);
            AddChild(v);
            AddChild(new DropZone(team, _move)); // last, so it covers the table while a card is dragged
        }

        public Control? FirstRow => _list.GetChildCount() > 0 ? _list.GetChild<Control>(0) : null;
        public TeamCard? RowOf(ushort id) => _rows.GetValueOrDefault(id);

        public void Show(IReadOnlyList<ScoreRow> all, int score, ushort me)
        {
            foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
            _rows.Clear();
            var players = all.Where(r => r.Team == Team).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
            _head.Text = Team == Teams.None ? Strings.F("teams.head.none", players.Count) : Strings.F("teams.head", Teams.Tag(Team), players.Count, score);
            _empty.Visible = players.Count == 0;
            foreach (var p in players)
            {
                var row = new TeamCard(p, p.Id == me, Team, _move);
                _rows[p.Id] = row;
                _list.AddChild(row);
            }
        }
    }

    /// <summary>
    /// Covers its table. Ignores the mouse until a player card is dragged (so clicks and the scrolling reach the cards), then takes the drop from anywhere on the table,
    /// whatever card or empty space is under the cursor. Draws the accent border while it can take what is over it.
    /// </summary>
    sealed partial class DropZone : Control
    {
        readonly byte _team;
        readonly Action<ushort, byte> _move;
        bool _hot;

        public DropZone(byte team, Action<ushort, byte> move)
        {
            _team = team; _move = move;
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationDragBegin) MouseFilter = TeamCard.TryRead(GetViewport().GuiGetDragData(), out _, out _) ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
            else if (what == NotificationDragEnd) { MouseFilter = MouseFilterEnum.Ignore; SetHot(false); }
            else if (what == NotificationMouseExit) SetHot(false);
        }

        void SetHot(bool on) { if (_hot == on) return; _hot = on; QueueRedraw(); }

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            var ok = TeamCard.TryRead(data, out _, out var from) && from != _team;
            SetHot(ok);
            return ok;
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            SetHot(false);
            if (TeamCard.TryRead(data, out var id, out var from) && from != _team) _move(id, _team);
        }

        public override void _Draw()
        {
            if (_hot) DrawRect(new Rect2(Vector2.Zero, Size), ShooterTheme.Accent, false, ShooterTheme.BorderStrong);
        }
    }

    /// <summary>A player card: the whole card is dragged. Keyboard: A, B or U moves the focused card to team A, team B or Unassigned. Text tags say bot, so nothing is told by colour alone.</summary>
    sealed partial class TeamCard : Button
    {
        public readonly ushort PlayerId;
        readonly byte _team;
        readonly Action<ushort, byte> _move;
        const string PayloadKind = "team_card";

        public TeamCard(ScoreRow p, bool mine, byte team, Action<ushort, byte> move)
        {
            PlayerId = p.Id; _team = team; _move = move;
            var text = mine ? Strings.F("teams.you", p.Name) : p.Name;
            if (p.Bot) text += "   " + Strings.T("teams.tag.bot");
            Text = text;
            Alignment = HorizontalAlignment.Left;
            FocusMode = FocusModeEnum.All;
            CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2);
            MouseDefaultCursorShape = CursorShape.Drag;
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
            byte? to = k.Keycode switch { Key.A => Teams.A, Key.B => Teams.B, Key.U => Teams.None, _ => null };
            if (to is not { } t) return;
            if (t != _team) _move(PlayerId, t);
            AcceptEvent();
        }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            var card = new PanelContainer { ThemeTypeVariation = "HudRowMine", Modulate = new Color(1, 1, 1, ShooterTheme.DragPreviewAlpha), CustomMinimumSize = new Vector2(Size.X, 0) };
            card.AddChild(new Label { Text = Text });
            for (Node? n = this; n != null; n = n.GetParent()) if (n is Control { Theme: { } t }) { card.Theme = t; break; } // the preview lives outside the themed root
            SetDragPreview(card);
            return new Godot.Collections.Dictionary { ["kind"] = PayloadKind, ["id"] = (int)PlayerId, ["team"] = (int)_team };
        }

        public static bool TryRead(Variant data, out ushort id, out byte team)
        {
            id = 0; team = 0;
            if (data.VariantType != Variant.Type.Dictionary) return false;
            var d = data.AsGodotDictionary();
            if (!d.ContainsKey("kind") || d["kind"].AsString() != PayloadKind) return false;
            id = (ushort)d["id"].AsInt32(); team = (byte)d["team"].AsInt32();
            return true;
        }
    }
}

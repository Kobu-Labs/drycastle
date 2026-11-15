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
        ("teams.hint", "Drag a player to the other team. Keyboard: Up/Down to pick, Left/Right or Enter to move, U to let the balance move them again. Esc goes back."),
        ("teams.head", "Team {0}   {1} players   Score {2}"),
        ("teams.empty", "Nobody on this team. Drop a player here."),
        ("teams.you", "{0} (you)"), ("teams.tag.bot", "BOT"), ("teams.tag.pinned", "PINNED"),
        ("teams.row.tip", "Pinned: the round-start balance leaves this player where an admin put them. Right-click or U to unpin."),
        ("teams.act.bots_a", "Bots to team A"), ("teams.act.bots_b", "Bots to team B"),
        ("teams.act.swap", "Swap sides"), ("teams.act.shuffle", "Shuffle"), ("teams.act.balance", "Balance"), ("teams.act.unpin", "Unpin all"),
        ("teams.act.bots_a.tip", "Every bot goes to team A and stays there through the round-start balance."),
        ("teams.act.bots_b.tip", "Every bot goes to team B and stays there through the round-start balance."),
        ("teams.act.swap.tip", "Everyone changes side. Earned team score goes along."),
        ("teams.act.shuffle.tip", "Deal everyone at random into two even teams. Pins are dropped."),
        ("teams.act.balance.tip", "Even the teams out now: bots move first, then the newest players. Pinned players stay."),
        ("teams.act.unpin.tip", "Let the balance move every player again."),
        ("teams.swap.title", "Swap the teams?"), ("teams.swap.body", "Every player switches side and respawns on the new one. Turrets and killstreak progress are lost. Kills and deaths stay."),
        ("teams.shuffle.title", "Shuffle the teams?"), ("teams.shuffle.body", "Every player is dealt to a random team and respawns there. Turrets and killstreak progress are lost. Kills and deaths stay."),
        ("teams.swap.confirm", "Swap"), ("teams.shuffle.confirm", "Shuffle"),
    };
}

/// <summary>
/// The Teams screen (#312, Pause, admins in team deathmatch): the two teams as tables. Drag a player onto the other table, or use the keyboard (Left/Right or Enter on a
/// focused row). A move applies at once (the player respawns on the new side) and is undone by moving them back. Quick actions sit above the tables; the two that move
/// everyone ask first. The tables follow the scoreboard, so joins, leaves and other admins show live. Refilled only when who stands where changes.
/// </summary>
public sealed partial class TeamsScreen : MenuScreen
{
    readonly NetClient _client;
    TeamColumn _a = null!, _b = null!;
    IReadOnlyList<ScoreRow>? _seen;
    string _signature = "";
    ushort _focusId;
    Button? _first;

    public TeamsScreen(NetClient client) : base("MenuBackdrop") { _client = client; }

    protected override Control Build()
    {
        _a = new TeamColumn(Teams.A, Move, Unpin);
        _b = new TeamColumn(Teams.B, Move, Unpin);
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(1120, 0);
        root.AddChild(Ui.Lbl(Strings.T("teams.title"), "HudDisplay"));
        root.AddChild(Ui.Lbl(Strings.T("teams.hint"), "HudDim", 0, true));
        root.AddChild(Ui.Gap(ShooterTheme.Space));

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
        Act("swap", () => Confirm("swap", TeamAction.Swap));
        Act("shuffle", () => Confirm("shuffle", TeamAction.Shuffle));
        Act("unpin", () => Send(new TeamAssign(TeamAction.UnpinAll)));
        root.AddChild(actions);
        root.AddChild(Ui.Gap(ShooterTheme.Space));

        var tables = Ui.HBox();
        tables.AddChild(_a);
        tables.AddChild(_b);
        root.AddChild(tables);

        root.AddChild(Ui.Gap(ShooterTheme.Space));
        var footer = Ui.HBox();
        footer.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        footer.AddChild(Ui.Fill());
        root.AddChild(footer);

        Refresh(true);
        var card = new PanelContainer { ThemeTypeVariation = "MenuCard" };
        card.AddChild(root);
        return card;
    }

    protected override Control? FirstFocusTarget() => _a.FirstRow ?? _b.FirstRow ?? _first;

    void Send(TeamAssign a) => _client.SendTeamAssign(a);

    void Move(ushort id, byte team)
    {
        _focusId = id; // the row reappears on the other table: the focus follows it
        Send(new TeamAssign(TeamAction.Move, id, team));
    }

    void Unpin(ushort id) => Send(new TeamAssign(TeamAction.Unpin, id));

    void Confirm(string key, TeamAction action) => Shell.Push(new DialogScreen(Strings.T($"teams.{key}.title"), Strings.T($"teams.{key}.body"),
        new[] { new DialogScreen.Choice(Strings.T($"teams.{key}.confirm"), () => Send(new TeamAssign(action)), "MenuDanger") }));

    public override void _Process(double delta)
    {
        if (!_client.CanManageTeams) { Back(); return; } // no longer an admin, or the match changed mode
        if (!ReferenceEquals(_seen, _client.Match.Rows)) Refresh(false);
    }

    /// <summary>Redraws the tables when who is where, pinned or named changed (every new scoreboard also carries pings and kills that do not matter here).</summary>
    void Refresh(bool force)
    {
        var rows = _client.Match.Rows;
        _seen = rows;
        var sig = string.Join('|', rows.Where(r => r.Team != Teams.None).Select(r => $"{r.Id}:{r.Team}:{(r.Pinned ? 1 : 0)}:{r.Name}")) + $"#{_client.Match.ScoreA}:{_client.Match.ScoreB}";
        if (!force && sig == _signature) return;
        _signature = sig;
        var focus = _focusId != 0 ? _focusId : FocusedRowId();
        _a.Show(rows, _client.Match.ScoreA, _client.MyId);
        _b.Show(rows, _client.Match.ScoreB, _client.MyId);
        if (focus != 0 && (_a.RowOf(focus) ?? _b.RowOf(focus)) is { } row) row.CallDeferred(Control.MethodName.GrabFocus);
        _focusId = 0;
    }

    ushort FocusedRowId() => GetViewport()?.GuiGetFocusOwner() is TeamRow r ? r.PlayerId : (ushort)0;

    /// <summary>One side: a heading, and its players as rows. A drop target for a dragged row.</summary>
    sealed partial class TeamColumn : PanelContainer
    {
        public readonly byte Team;
        readonly Action<ushort, byte> _move;
        readonly Action<ushort> _unpin;
        readonly Label _head;
        readonly VBoxContainer _list = new();
        readonly Label _empty;
        readonly Dictionary<ushort, TeamRow> _rows = new();

        public TeamColumn(byte team, Action<ushort, byte> move, Action<ushort> unpin)
        {
            Team = team; _move = move; _unpin = unpin;
            ThemeTypeVariation = "HudPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(540, 420);
            var v = Ui.VBox();
            _head = Ui.Lbl("", "HudHead");
            v.AddChild(_head);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
            _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(_list);
            v.AddChild(scroll);
            _empty = Ui.Lbl(Strings.T("teams.empty"), "HudDim", 0, true);
            v.AddChild(_empty);
            AddChild(v);
        }

        public Control? FirstRow => _list.GetChildCount() > 0 ? _list.GetChild<Control>(0) : null;
        public TeamRow? RowOf(ushort id) => _rows.GetValueOrDefault(id);

        public void Show(IReadOnlyList<ScoreRow> all, int score, ushort me)
        {
            foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
            _rows.Clear();
            var players = all.Where(r => r.Team == Team).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
            _head.Text = Strings.F("teams.head", Teams.Tag(Team), players.Count, score);
            _empty.Visible = players.Count == 0;
            foreach (var p in players)
            {
                var row = new TeamRow(p, p.Id == me, Team, _move, _unpin);
                _rows[p.Id] = row;
                _list.AddChild(row);
            }
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) => TeamRow.TryRead(data, out _, out var from) && from != Team;

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (TeamRow.TryRead(data, out var id, out var from) && from != Team) _move(id, Team);
        }
    }

    /// <summary>A player on a table: a button you can drag, and move with the keyboard. Text tags say bot and pinned, so nothing is told by colour alone.</summary>
    sealed partial class TeamRow : Button
    {
        public readonly ushort PlayerId;
        readonly byte _team;
        readonly bool _pinned;
        readonly Action<ushort, byte> _move;
        readonly Action<ushort> _unpin;
        const string PayloadKind = "team_row";

        public TeamRow(ScoreRow p, bool mine, byte team, Action<ushort, byte> move, Action<ushort> unpin)
        {
            PlayerId = p.Id; _team = team; _pinned = p.Pinned; _move = move; _unpin = unpin;
            var text = mine ? Strings.F("teams.you", p.Name) : p.Name;
            if (p.Bot) text += "   " + Strings.T("teams.tag.bot");
            if (p.Pinned) text += "   " + Strings.T("teams.tag.pinned");
            Text = text;
            Alignment = HorizontalAlignment.Left;
            FocusMode = FocusModeEnum.All;
            CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2);
            MouseDefaultCursorShape = CursorShape.PointingHand;
            if (p.Pinned) TooltipText = Strings.T("teams.row.tip");
            Pressed += () => _move(PlayerId, Teams.Other(_team));
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Left or Key.Right:
                    var to = k.Keycode == Key.Left ? Teams.A : Teams.B;
                    if (to != _team) _move(PlayerId, to);
                    AcceptEvent();
                    break;
                case InputEventKey { Pressed: true, Echo: false, Keycode: Key.U }:
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                    if (_pinned) _unpin(PlayerId);
                    AcceptEvent();
                    break;
            }
        }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            var preview = new Label { Text = Text, Modulate = new Color(1, 1, 1, ShooterTheme.DragPreviewAlpha), ThemeTypeVariation = "HudBig" };
            for (Node? n = this; n != null; n = n.GetParent()) if (n is Control { Theme: { } t }) { preview.Theme = t; break; } // the preview lives outside the themed root
            SetDragPreview(preview);
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

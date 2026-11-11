using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The loadout editor (ticket #118; docs/standards/ui-ux.md sections 1, 2, 3, 4 and 5). Five slot tabs (the last one used
/// reopens), six pickers grouped by category with locked rows shown as "unlocks at level N", stat bars that compare the
/// draft weapon with the saved one, and Make active. Edits autosave (ticket #145): every change becomes the slot's draft,
/// which the connection saves after a short quiet period, and the status line shows Saving / Saved / retrying / rejected.
/// Undo (Ctrl+Z, or the button) steps back through the slot's edits. Leaving the screen flushes unsent edits. The server
/// owns the data: the screen redraws from the profile the server answers with. Rename is not offered yet (the layout ticket).
/// </summary>
public partial class LoadoutScreen : MenuScreen
{
    readonly Func<NetClient?> _client;
    readonly Func<GameData> _data;
    NetClient? _net;
    int _tab;
    Loadout _saved = Loadout.Empty(0);
    bool _hasProfile;
    string _shownStatus = "";
    int _statPicker; // 0 primary, 1 secondary: whose stats the bars show

    static readonly string[] PickCaptions = { "loadout.primary", "loadout.secondary", "loadout.perk", "loadout.streak", "loadout.streak", "loadout.streak" };

    readonly Button[] _tabs = new Button[Loadout.SlotCount];
    readonly OptionButton[] _picks = new OptionButton[6];
    readonly List<string?>[] _ids = { new(), new(), new(), new(), new(), new() }; // item index -> id (null for a group heading)
    readonly ProgressBar[] _bar = new ProgressBar[4], _barSaved = new ProgressBar[4];
    readonly Label[] _statName = new Label[4], _statVal = new Label[4];
    Label _level = null!, _statTitle = null!, _note = null!, _empty = null!, _hint = null!;
    Control _body = null!, _tabRow = null!;
    Button _undo = null!, _makeActive = null!, _emptyBack = null!, _customize = null!;

    /// <summary>Opens the camouflage customiser for a slot (#141). Set by the shooter; the button is hidden without it.</summary>
    public Func<int, MenuScreen>? CustomizeFactory { get; set; }

    /// <param name="client">The live connection (null on the main menu: the screen then says to join a server).</param>
    /// <param name="data">The data files, for the weapon and perk names and stats.</param>
    public LoadoutScreen(Func<NetClient?> client, Func<GameData> data) { _client = client; _data = data; }

    /// <summary>The connection's autosave (null offline). It outlives this screen, so a save in flight is still answered.</summary>
    LoadoutAutosave? Autosave => _net?.Autosave;

    /// <summary>What the editor shows for the open slot: the draft if there is one, else the server copy.</summary>
    Loadout Draft => Autosave?.Effective(_tab) ?? _saved;

    public override void _Ready()
    {
        base._Ready();
        _net = _client();
        _tab = LoadoutEditor.ClampTab(int.TryParse(GameSettings.LoadUi("loadout_tab", "0"), out var t) ? t : 0);
        if (_net != null) { _net.ProfileChanged += OnProfile; _net.LoadoutAcked += OnAck; }
        if (_net?.Profile is { } p) OnProfile(p);
        else Refresh();
    }

    public override void _ExitTree()
    {
        _net?.FlushAutosave(); // unsent edits go out now, not after the debounce
        if (_net != null) { _net.ProfileChanged -= OnProfile; _net.LoadoutAcked -= OnAck; }
        base._ExitTree();
    }

    /// <summary>The status line and the undo button follow the autosave state; cheap, so it runs each frame and redraws only on change.</summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        if (Autosave == null || !_hasProfile) return;
        var key = $"{Autosave.StateOf(_tab)}|{Autosave.RejectReason(_tab)}|{Autosave.CanUndo(_tab)}|{Autosave.HasUnconfirmed(_tab)}";
        if (key == _shownStatus) return;
        _shownStatus = key;
        Sync();
    }

    protected override Control Build()
    {
        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(1000, 0);

        var head = Ui.HBox();
        head.AddChild(Ui.Lbl(Strings.T("loadout.title"), "HudDisplay"));
        head.AddChild(Ui.Fill());
        _level = Ui.Lbl("", "HudDim");
        head.AddChild(_level);
        v.AddChild(head);

        // empty state: no connection yet, or the server has not answered
        _empty = Ui.Lbl("", "HudDim", 0, true);
        _emptyBack = Ui.Btn(Strings.T("dialog.back"), Back, null, 160);
        var emptyBox = Ui.VBox();
        emptyBox.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        emptyBox.AddChild(_empty);
        emptyBox.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        emptyBox.AddChild(_emptyBack);
        v.AddChild(emptyBox);

        _body = Ui.VBox();
        v.AddChild(_body);
        var body = (VBoxContainer)_body;

        var group = new ButtonGroup();
        _tabRow = Ui.HBox();
        for (var i = 0; i < Loadout.SlotCount; i++)
        {
            var slot = i;
            var b = new Button
            {
                ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2),
            };
            b.Pressed += () => RequestTab(slot);
            _tabs[i] = b;
            _tabRow.AddChild(b);
        }
        body.AddChild(_tabRow);
        body.AddChild(Ui.Gap(ShooterTheme.Space * 2));

        var columns = Ui.HBox();
        columns.AddChild(BuildPickers());
        columns.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        columns.AddChild(BuildStats());
        body.AddChild(columns);

        body.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        var bottom = Ui.HBox();
        _note = Ui.Lbl("", "HudDim", 0, true);
        _note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bottom.AddChild(_note);
        _undo = Ui.Btn(Strings.T("loadout.undo"), Undo, null, 200);
        _makeActive = Ui.Btn(Strings.T("loadout.make_active"), MakeActive, null, 200);
        bottom.AddChild(_undo);
        bottom.AddChild(_makeActive);
        body.AddChild(bottom);
        body.AddChild(Ui.Gap(ShooterTheme.Space));

        _hint = Ui.Lbl(Strings.T("loadout.hint"), "HudSmall");
        body.AddChild(_hint);
        var foot = Ui.HBox();
        _customize = Ui.Btn(Strings.T("loadout.customize"), () => { if (CustomizeFactory is { } f) Shell.Push(f(_tab)); }, null, 200);
        _customize.Visible = CustomizeFactory != null;
        foot.AddChild(_customize);
        foot.AddChild(Ui.Fill());
        foot.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        body.AddChild(foot);
        return v;
    }

    Control BuildPickers()
    {
        var col = Ui.VBox();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var i = 0; i < 6; i++)
        {
            var p = i;
            var row = Ui.HBox();
            row.AddChild(Ui.Lbl(Strings.T(PickCaptions[i]), "HudDim", 240));
            var ob = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All,
                CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2), MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            ob.ItemSelected += idx => OnPicked(p, (int)idx);
            // the weapon the stat bars show follows the last weapon picker the player touched
            ob.FocusEntered += () => { if (p < 2 && _statPicker != p) { _statPicker = p; Sync(); } };
            _picks[i] = ob;
            row.AddChild(ob);
            col.AddChild(row);
        }
        return col;
    }

    Control BuildStats()
    {
        var col = Ui.VBox();
        col.CustomMinimumSize = new Vector2(360, 0);
        _statTitle = Ui.Lbl("", "HudHead");
        col.AddChild(_statTitle);
        for (var i = 0; i < 4; i++)
        {
            var row = Ui.HBox();
            _statName[i] = Ui.Lbl("", "HudDim", 180);
            row.AddChild(_statName[i]);
            var bars = Ui.VBox();
            bars.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _bar[i] = Bar("StatBar", 14);
            _barSaved[i] = Bar("StatBarSaved", 6);
            bars.AddChild(_bar[i]);
            bars.AddChild(_barSaved[i]);
            row.AddChild(bars);
            _statVal[i] = Ui.Lbl("", null, 0);
            _statVal[i].CustomMinimumSize = new Vector2(ShooterTheme.Space * 20, 0);
            row.AddChild(_statVal[i]);
            col.AddChild(row);
        }
        return col;
    }

    static ProgressBar Bar(string variation, int height) => new()
    {
        MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, ThemeTypeVariation = variation,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    protected override Control? FirstFocusTarget() => _hasProfile ? _tabs[_tab] : _emptyBack;

    // ---- server data ----

    /// <summary>
    /// A new profile from the server: the open slot's server copy moves. The draft is not touched: it stays on screen until
    /// the server confirms it (the autosave holds it), so an echo of an older save cannot overwrite an edit in progress.
    /// </summary>
    void OnProfile(ProfileMsg p)
    {
        _hasProfile = true;
        _saved = ServerCopy(p, _tab);
        _level.Text = Strings.F("loadout.level", p.Level);
        for (var i = 0; i < Loadout.SlotCount; i++)
            _tabs[i].Text = Strings.F("loadout.slot", i + 1) + (p.ActiveSlot == i ? "   " + Strings.T("loadout.active") : "");
        Fill(p);
        Sync();
    }

    /// <summary>An answer to a save: a rejection is toasted (the status line keeps the reason). Accepted saves need no toast.</summary>
    void OnAck(LoadoutAckInfo ack)
    {
        if (!ack.Ok) Shell.Toast(Strings.F("loadout.invalid", ack.Reason), ToastKind.Error);
        Sync();
    }

    static Loadout ServerCopy(ProfileMsg p, int slot) => p.Loadouts.FirstOrDefault(l => l.Slot == slot) ?? Loadout.Empty(slot);

    // ---- editing ----

    /// <summary>Switching slots needs no confirm now: the left slot's edits keep saving on their own.</summary>
    void RequestTab(int slot)
    {
        if (slot == _tab) return;
        SwitchTo(slot);
    }

    void SwitchTo(int slot)
    {
        _tab = slot;
        GameSettings.SaveUi("loadout_tab", slot.ToString());
        _tabs[slot].ButtonPressed = true;
        _saved = _net?.Profile is { } p ? ServerCopy(p, slot) : Loadout.Empty(slot);
        if (_net?.Profile is { } q) Fill(q);
        Sync();
    }

    void OnPicked(int picker, int index)
    {
        if (Autosave == null || index < 0 || index >= _ids[picker].Count || _ids[picker][index] is not { } id) return;
        var d = Draft;
        var next = picker switch
        {
            0 => d with { PrimaryWeapon = id },
            1 => d with { SecondaryWeapon = id },
            2 => d with { Perk = id },
            3 => d with { Streak1 = id },
            4 => d with { Streak2 = id },
            _ => d with { Streak3 = id },
        };
        Autosave.Edit(next, (long)Time.GetTicksMsec());
        if (picker < 2) _statPicker = picker;
        Sync();
    }

    /// <summary>Steps the open slot back one edit (Ctrl+Z and the Undo button). The step saves like any edit.</summary>
    void Undo()
    {
        if (Autosave == null || !Autosave.Undo(_tab, (long)Time.GetTicksMsec())) return;
        Sync();
    }

    void MakeActive()
    {
        if (_net == null) return;
        _net.SelectSlot(_tab);
        Shell.Toast(Strings.F("loadout.toast.active", _tab + 1));
    }

    /// <summary>Esc: one level up. Unsent edits are flushed on the way out (they are never dropped).</summary>
    public override void Back()
    {
        _net?.FlushAutosave();
        base.Back();
    }

    /// <summary>1-5 pick a slot tab, as on the respawn screen. Ctrl+Z undoes the last edit of the open slot.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !_hasProfile) return false;
        if (k.Keycode == Key.Z && k.CtrlPressed && !k.ShiftPressed)
        {
            if (Autosave == null || !Autosave.CanUndo(_tab)) return false;
            Undo();
            return true;
        }
        var slot = k.Keycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4, _ => -1 };
        if (slot < 0) return false;
        RequestTab(slot);
        return true;
    }

    // ---- drawing ----

    /// <summary>Rebuilds the six pickers from the data and the player's level and unlocks.</summary>
    void Fill(ProfileMsg p)
    {
        var data = _data();
        var streak = data.Killstreaks.UnlockLevels;
        var lists = new[]
        {
            LoadoutEditor.Options(LoadoutPicker.Primary, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
            LoadoutEditor.Options(LoadoutPicker.Secondary, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
            LoadoutEditor.Options(LoadoutPicker.Perk, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
            LoadoutEditor.Options(LoadoutPicker.Streak1, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
            LoadoutEditor.Options(LoadoutPicker.Streak2, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
            LoadoutEditor.Options(LoadoutPicker.Streak3, data.Weapons, data.Perks, streak, p.Level, p.Unlocks),
        };
        for (var i = 0; i < 6; i++)
        {
            var ob = _picks[i];
            ob.Clear();
            _ids[i].Clear();
            string? lastGroup = null;
            foreach (var o in lists[i])
            {
                if (o.Group != "" && o.Group != lastGroup)
                {
                    ob.AddItem(Strings.T("loadout.group." + o.Group));
                    ob.SetItemDisabled(ob.ItemCount - 1, true);
                    _ids[i].Add(null);
                }
                lastGroup = o.Group;
                var name = i < 2 ? WeaponLabel(o.Id) : i == 2 ? (o.Id == "" ? Strings.T("loadout.none") : PerkLabel(data, o.Id)) : o.Id == "" ? Strings.T("loadout.none") : StreakLabel(o.Id);
                ob.AddItem(o.Unlocked ? name : Strings.F("loadout.locked", name, o.UnlockLevel));
                ob.SetItemDisabled(ob.ItemCount - 1, !o.Unlocked);
                _ids[i].Add(o.Id);
            }
        }
    }

    /// <summary>Selects each picker's current draft item, refreshes the stat bars, the buttons and the status line.</summary>
    void Sync()
    {
        if (!_hasProfile)
        {
            Refresh();
            return;
        }
        for (var i = 0; i < 6; i++)
        {
            var id = DraftId(i);
            _picks[i].Select(_ids[i].IndexOf(id));
        }
        // the status line words the autosave state (#145); the rules are checked by the server, which answers each save
        var state = Autosave?.StateOf(_tab) ?? AutosaveState.Saved;
        (_note.Text, _note.ThemeTypeVariation) = state switch
        {
            AutosaveState.Pending or AutosaveState.Saving => (Strings.T("loadout.saving"), "HudDim"),
            AutosaveState.Retrying => (Strings.T("loadout.retrying"), "HudWarn"),
            AutosaveState.Rejected => (Strings.F("loadout.invalid", Autosave!.RejectReason(_tab)), "MenuError"),
            _ => (Strings.T("loadout.saved"), "HudDim"),
        };
        _undo.Disabled = !(Autosave?.CanUndo(_tab) ?? false);
        _makeActive.Disabled = _net?.Profile is not { } p || p.ActiveSlot == _tab;
        UpdateStats();
        Refresh();
    }

    string DraftId(int picker) => picker switch
    {
        0 => Draft.PrimaryWeapon, 1 => Draft.SecondaryWeapon, 2 => Draft.Perk,
        3 => Draft.Streak1, 4 => Draft.Streak2, _ => Draft.Streak3,
    };

    void UpdateStats()
    {
        var data = _data().Weapons;
        var draftId = _statPicker == 0 ? Draft.PrimaryWeapon : Draft.SecondaryWeapon;
        var savedId = _statPicker == 0 ? _saved.PrimaryWeapon : _saved.SecondaryWeapon;
        _statTitle.Text = _statPicker == 0 ? Strings.T("loadout.primary") : Strings.T("loadout.secondary");
        var w = data.Find(draftId);
        var sw = data.Find(savedId);
        var stats = w == null ? null : LoadoutEditor.Stats(w, data);
        var saved = sw == null ? null : LoadoutEditor.Stats(sw, data);
        for (var i = 0; i < 4; i++)
        {
            var key = LoadoutEditor.StatKeys[i];
            _statName[i].Text = Strings.T("loadout.stat." + key);
            _bar[i].Value = stats?[i].Share ?? 0;
            _barSaved[i].Value = saved?[i].Share ?? 0;
            var text = stats == null ? Strings.T("loadout.empty") : StatText(i, stats[i].Value);
            if (stats != null && saved != null && Math.Abs(saved[i].Value - stats[i].Value) > 0.0001f)
                text += "   " + Strings.F("loadout.compare", StatText(i, saved[i].Value));
            _statVal[i].Text = text;
        }
        _statTitle.Text += stats == null ? "" : "   " + WeaponLabel(draftId);
    }

    static string StatText(int i, float v) => i switch
    {
        1 => Strings.F("loadout.rate", v),
        3 => Strings.F("fmt.times", v),
        _ => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    void Refresh()
    {
        _body.Visible = _hasProfile;
        _empty.Visible = !_hasProfile;
        if (!_hasProfile)
        {
            _empty.Text = _net == null ? Strings.T("loadout.offline") : Strings.T("loadout.loading");
            _emptyBack.Visible = true;
        }
    }

    static string WeaponLabel(string id)
    {
        if (id == "") return Strings.T("loadout.empty");
        var k = "weapon." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    static string StreakLabel(string id)
    {
        var k = "streak." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    static string PerkLabel(GameData data, string id) => data.Perks.Find(id)?.Name ?? id;
}

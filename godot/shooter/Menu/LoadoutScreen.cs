using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The loadout editor (ticket #118; docs/standards/ui-ux.md sections 1, 2, 3, 4 and 5). Five slot tabs (the last one used
/// reopens), six pickers grouped by category with locked rows shown as "unlocks at level N", stat bars that compare the
/// draft weapon with the saved one, Save / Discard / Make active, and a confirm before unsaved edits are dropped (Esc and
/// tab changes included). The server owns the data: Save and Make active send a request, and the screen redraws from the
/// profile the server answers with. Rename is not offered, because a loadout has no name on the wire.
/// </summary>
public partial class LoadoutScreen : MenuScreen
{
    readonly Func<NetClient?> _client;
    readonly Func<GameData> _data;
    NetClient? _net;
    int _tab;
    Loadout _saved = Loadout.Empty(0), _draft = Loadout.Empty(0);
    bool _hasProfile, _awaitingSave;
    int _statPicker; // 0 primary, 1 secondary: whose stats the bars show

    static readonly string[] PickCaptions = { "loadout.primary", "loadout.secondary", "loadout.perk", "loadout.streak", "loadout.streak", "loadout.streak" };

    readonly Button[] _tabs = new Button[Loadout.SlotCount];
    readonly OptionButton[] _picks = new OptionButton[6];
    readonly List<string?>[] _ids = { new(), new(), new(), new(), new(), new() }; // item index -> id (null for a group heading)
    readonly ProgressBar[] _bar = new ProgressBar[4], _barSaved = new ProgressBar[4];
    readonly Label[] _statName = new Label[4], _statVal = new Label[4];
    Label _level = null!, _statTitle = null!, _note = null!, _empty = null!, _hint = null!;
    Control _body = null!, _tabRow = null!;
    Button _save = null!, _discard = null!, _makeActive = null!, _emptyBack = null!;

    /// <param name="client">The live connection (null on the main menu: the screen then says to join a server).</param>
    /// <param name="data">The data files, for the weapon and perk names and stats.</param>
    public LoadoutScreen(Func<NetClient?> client, Func<GameData> data) { _client = client; _data = data; }

    bool Dirty => _draft != _saved;

    public override void _Ready()
    {
        base._Ready();
        _net = _client();
        _tab = LoadoutEditor.ClampTab(int.TryParse(GameSettings.LoadUi("loadout_tab", "0"), out var t) ? t : 0);
        if (_net != null) { _net.ProfileChanged += OnProfile; _net.ServerError += OnServerError; }
        if (_net?.Profile is { } p) OnProfile(p);
        else Refresh();
    }

    public override void _ExitTree()
    {
        if (_net != null) { _net.ProfileChanged -= OnProfile; _net.ServerError -= OnServerError; }
        base._ExitTree();
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
        _discard = Ui.Btn(Strings.T("loadout.discard"), DiscardDraft, null, 200);
        _makeActive = Ui.Btn(Strings.T("loadout.make_active"), MakeActive, null, 200);
        _save = Ui.Btn(Strings.T("loadout.save"), Save, "MenuPrimary", 200);
        bottom.AddChild(_discard);
        bottom.AddChild(_makeActive);
        bottom.AddChild(_save);
        body.AddChild(bottom);
        body.AddChild(Ui.Gap(ShooterTheme.Space));

        _hint = Ui.Lbl(Strings.T("loadout.hint"), "HudSmall");
        body.AddChild(_hint);
        body.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
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

    /// <summary>A new profile from the server: the open slot's saved copy moves, and the draft follows it unless the player is editing.</summary>
    void OnProfile(ProfileMsg p)
    {
        _hasProfile = true;
        var before = _saved;
        _saved = ServerCopy(p, _tab);
        if (_draft == before) _draft = _saved;
        if (_awaitingSave) { _awaitingSave = false; Shell.Toast(Strings.F("loadout.toast.saved", _tab + 1)); }
        _level.Text = Strings.F("loadout.level", p.Level);
        for (var i = 0; i < Loadout.SlotCount; i++)
            _tabs[i].Text = Strings.F("loadout.slot", i + 1) + (p.ActiveSlot == i ? "   " + Strings.T("loadout.active") : "");
        Fill(p);
        Sync();
    }

    void OnServerError(string reason)
    {
        if (!_awaitingSave) return;
        _awaitingSave = false;
        Shell.Toast(reason, ToastKind.Error);
        Sync();
    }

    static Loadout ServerCopy(ProfileMsg p, int slot) => p.Loadouts.FirstOrDefault(l => l.Slot == slot) ?? Loadout.Empty(slot);

    // ---- editing ----

    void RequestTab(int slot)
    {
        if (slot == _tab) return;
        if (!Dirty) { SwitchTo(slot); return; }
        _tabs[_tab].ButtonPressed = true; // the open tab stays pressed until the player confirms
        Shell.Push(Discard(() => SwitchTo(slot)));
    }

    void SwitchTo(int slot)
    {
        _tab = slot;
        GameSettings.SaveUi("loadout_tab", slot.ToString());
        _tabs[slot].ButtonPressed = true;
        _saved = _net?.Profile is { } p ? ServerCopy(p, slot) : Loadout.Empty(slot);
        _draft = _saved;
        _awaitingSave = false;
        if (_net?.Profile is { } q) Fill(q);
        Sync();
    }

    /// <summary>The confirm for unsaved edits: Cancel is focused (the destructive choice is not the default).</summary>
    DialogScreen Discard(Action run) => new(Strings.T("loadout.discard.title"), Strings.F("loadout.discard.body", _tab + 1),
        new[] { new DialogScreen.Choice(Strings.T("loadout.discard"), run, "MenuDanger") });

    void OnPicked(int picker, int index)
    {
        if (index < 0 || index >= _ids[picker].Count || _ids[picker][index] is not { } id) return;
        var d = _draft;
        _draft = picker switch
        {
            0 => d with { PrimaryWeapon = id },
            1 => d with { SecondaryWeapon = id },
            2 => d with { Perk = id },
            3 => d with { Streak1 = id },
            4 => d with { Streak2 = id },
            _ => d with { Streak3 = id },
        };
        if (picker < 2) _statPicker = picker;
        Sync();
    }

    void DiscardDraft()
    {
        _draft = _saved;
        Sync();
    }

    void Save()
    {
        if (_net == null || !Dirty || Reason() != null || _awaitingSave) return;
        _awaitingSave = true;
        _net.SaveLoadout(_draft);
        Sync();
    }

    void MakeActive()
    {
        if (_net == null) return;
        _net.SelectSlot(_tab);
        Shell.Toast(Strings.F("loadout.toast.active", _tab + 1));
    }

    /// <summary>Esc: one level up, but unsaved edits ask first (the same confirm as a tab change).</summary>
    public override void Back()
    {
        if (!_hasProfile || !Dirty) { base.Back(); return; }
        Shell.Push(Discard(() => Shell.Pop()));
    }

    /// <summary>1-5 pick a slot tab, as on the respawn screen.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !_hasProfile) return false;
        var slot = k.Keycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4, _ => -1 };
        if (slot < 0) return false;
        RequestTab(slot);
        return true;
    }

    // ---- drawing ----

    /// <summary>Why the draft cannot be saved (the same rules the server applies), or null when it can.</summary>
    string? Reason()
    {
        if (_net?.Profile is not { } p) return null;
        var data = _data();
        var perkLevels = data.Perks.Perks.ToDictionary(x => x.Id, x => x.UnlockLevel);
        return LoadoutRules.ValidateLoadout(_draft, data.Weapons, p.Level, p.Unlocks, perkLevels, KillstreakIds.Placeholder);
    }

    /// <summary>Rebuilds the six pickers from the data and the player's level and unlocks.</summary>
    void Fill(ProfileMsg p)
    {
        var data = _data();
        var streak = KillstreakIds.Placeholder;
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
        var reason = Reason();
        _note.Text = _awaitingSave ? Strings.T("loadout.saving")
            : reason != null ? Strings.F("loadout.invalid", reason)
            : Dirty ? Strings.T("loadout.unsaved") : Strings.T("loadout.saved");
        _note.ThemeTypeVariation = reason != null ? "MenuError" : Dirty ? "HudWarn" : "HudDim";
        _save.Disabled = !Dirty || reason != null || _awaitingSave;
        _discard.Disabled = !Dirty;
        _makeActive.Disabled = _awaitingSave || _net?.Profile is not { } p || p.ActiveSlot == _tab;
        UpdateStats();
        Refresh();
    }

    string DraftId(int picker) => picker switch
    {
        0 => _draft.PrimaryWeapon, 1 => _draft.SecondaryWeapon, 2 => _draft.Perk,
        3 => _draft.Streak1, 4 => _draft.Streak2, _ => _draft.Streak3,
    };

    void UpdateStats()
    {
        var data = _data().Weapons;
        var draftId = _statPicker == 0 ? _draft.PrimaryWeapon : _draft.SecondaryWeapon;
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

using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The loadout screen, laid out as a "Create a class" menu (ticket #147; docs/standards/ui-ux.md). A strip of five class tabs
/// (the active class is starred, and "Use on next spawn" is a separate button). A left column of seven rows (primary,
/// secondary, perk, three killstreaks, camouflage). The centre is the soldier on a turntable holding the primary, with the
/// camouflage applied (<see cref="CamoPreview"/>). The right side shows the weapons as large images with category badges,
/// then the perk and the killstreaks with the kills each needs. Choosing a row opens its picker in place of the centre and the
/// right side: an image grid (locked items show a padlock and their level; new unlocks carry a NEW tag) and a compare panel
/// with the highlighted item's bars against the equipped one, always with text values.
///
/// Edits autosave (ticket #145): each change becomes the slot's draft, which the connection saves after a short quiet period.
/// The indicator reads Saving, Saved or Error. Ctrl+Z undoes the open class's last edit. Reset to Default and Copy Class go
/// to the server directly (after any unsent edit is flushed), each behind a confirm that names what is lost. Esc goes one
/// level up: picker, then the screen. The server owns the data; the screen redraws from the profile it sends.
/// </summary>
public partial class LoadoutScreen : MenuScreen
{
    const int RowCount = 7, RowCamo = 6, PickColumns = 4, PickCardWidth = 188, PickCardHeight = 152;
    const int ColumnWidth = 340, CentreWidth = 440, RightWidth = 380, CompareWidth = 320;

    readonly Func<NetClient?> _client;
    readonly Func<GameData> _data;
    NetClient? _net;
    ProfileMsg? _profile;
    IconRegistry _icons = null!;
    int _tab;          // the open class
    int _row;          // the left-column row last opened (0 primary .. 6 camouflage)
    int _picker = -1;  // the row whose picker is open, or -1 for the class view
    bool _devMode, _renaming;
    string _pickerKey = "", _shownStatus = "";

    readonly ButtonGroup _tabGroup = new(), _rowGroup = new();
    readonly Button[] _tabs = new Button[Loadout.SlotCount];
    readonly Label[] _tabNames = new Label[Loadout.SlotCount];
    readonly Glyph[] _tabStars = new Glyph[Loadout.SlotCount];
    readonly Button[] _rowButtons = new Button[RowCount];
    readonly TextureRect[] _rowIcons = new TextureRect[RowCount];
    readonly Label[] _rowValues = new Label[RowCount];
    readonly Label[] _rowTitles = new Label[RowCount];
    readonly Label[] _cmpName = new Label[5], _cmpValue = new Label[5];
    readonly ProgressBar[] _cmpBar = new ProgressBar[5], _cmpBarEq = new ProgressBar[5];
    readonly Control[] _cmpRows = new Control[5];
    readonly TextureRect[] _cellIcons = new TextureRect[4];
    readonly Label[] _cellNames = new Label[4], _cellNotes = new Label[4];

    Button _useNext = null!;
    Glyph _useNextStar = null!;
    Label _useNextText = null!, _level = null!, _className = null!, _status = null!, _hint = null!, _pickHint = null!;
    Label _pickTitle = null!, _cmpTitle = null!, _cmpNote = null!, _empty = null!;
    LineEdit _nameEdit = null!;
    Button _undo = null!, _rename = null!, _reset = null!, _copy = null!, _emptyBack = null!;
    Control _body = null!, _classView = null!, _pickView = null!;
    GridContainer _grid = null!;
    CamoPreview _preview = null!;
    Label _devBadge = null!;
    WeaponBlock _primary = null!, _secondary = null!;

    /// <summary>Opens the camouflage customiser for a slot (#141). Set by the shooter; the row is inert without it.</summary>
    public Func<int, MenuScreen>? CustomizeFactory { get; set; }

    /// <param name="client">The live connection (null on the main menu: the screen then says to join a server).</param>
    /// <param name="data">The data files: weapon, perk and killstreak names and stats, the camouflage file and the icons.</param>
    public LoadoutScreen(Func<NetClient?> client, Func<GameData> data) { _client = client; _data = data; }

    /// <summary>The connection's autosave (null offline). It outlives this screen, so a save in flight is still answered.</summary>
    LoadoutAutosave? Autosave => _net?.Autosave;

    /// <summary>The loadout the screen shows for a slot: the draft if there is one, else the server copy.</summary>
    Loadout LoadoutAt(int slot) => Autosave?.Effective(slot) ?? ServerCopy(slot);

    Loadout Draft => LoadoutAt(_tab);

    Loadout ServerCopy(int slot) => _profile?.Loadouts.FirstOrDefault(l => l.Slot == slot) ?? Loadout.Empty(slot);

    static long Now() => (long)Time.GetTicksMsec();

    public override void _Ready()
    {
        base._Ready();
        _net = _client();
        _devBadge = DevBadge.Create(false);
        AddChild(_devBadge);
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
        if (Autosave == null || _profile == null) return;
        var key = $"{_tab}|{Autosave.StateOf(_tab)}|{Autosave.RejectReason(_tab)}|{Autosave.CanUndo(_tab)}";
        if (key == _shownStatus) return;
        _shownStatus = key;
        Sync();
    }

    protected override Control Build()
    {
        _icons = new IconRegistry(_data());
        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(1500, 0);

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
        body.AddChild(BuildTabStrip());
        body.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        body.AddChild(BuildColumns());
        body.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        body.AddChild(BuildBottomBar());
        body.AddChild(Ui.Gap(ShooterTheme.Space));
        _hint = Ui.Lbl(Strings.T("loadout.hint"), "HudSmall");
        _pickHint = Ui.Lbl(Strings.T("loadout.hint.pick"), "HudSmall");
        _pickHint.Visible = false;
        body.AddChild(_hint);
        body.AddChild(_pickHint);
        return v;
    }

    protected override Control? FirstFocusTarget() => _profile != null ? _tabs[_tab] : _emptyBack;

    // ---- layout ----

    /// <summary>Five class tabs (the name, the active one starred) and the "Use on next spawn" toggle.</summary>
    Control BuildTabStrip()
    {
        var row = Ui.HBox();
        for (var i = 0; i < Loadout.SlotCount; i++)
        {
            var slot = i;
            var b = new Button
            {
                ToggleMode = true, ButtonGroup = _tabGroup, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            var content = Content(out _tabNames[i], out _tabStars[i], "", Glyph.Kind.Star, ShooterTheme.Warn);
            b.AddChild(content);
            b.Pressed += () => SwitchTo(slot);
            _tabs[i] = b;
            row.AddChild(b);
        }
        row.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        _useNext = Ui.Btn("", UseNext, null, 260);
        _useNext.TooltipText = Strings.T("loadout.star.tip");
        var nextContent = Content(out _useNextText, out _useNextStar, Strings.T("loadout.use_next"), Glyph.Kind.Star, ShooterTheme.Warn);
        _useNextStar.Visible = false;
        _useNext.AddChild(nextContent);
        row.AddChild(_useNext);
        return row;
    }

    /// <summary>A full-size child for a button: an optional drawn mark, then a label. The button's own text stays empty.</summary>
    static HBoxContainer Content(out Label label, out Glyph mark, string text, Glyph.Kind kind, Color colour)
    {
        var box = Ui.HBox();
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.Alignment = BoxContainer.AlignmentMode.Center;
        mark = new Glyph(kind, colour, ShooterTheme.Space * 2);
        label = Ui.Lbl(text, null);
        box.AddChild(mark);
        box.AddChild(label);
        return box;
    }

    /// <summary>Left column, then either the class view (centre and right) or the picker (grid and compare panel).</summary>
    Control BuildColumns()
    {
        var row = Ui.HBox();
        row.AddChild(BuildRows());
        row.AddChild(Ui.Gap(ShooterTheme.Space * 3));

        _classView = Ui.HBox();
        var classBox = (HBoxContainer)_classView;
        classBox.AddChild(BuildCentre());
        classBox.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        classBox.AddChild(BuildRight());
        row.AddChild(_classView);

        _pickView = BuildPicker();
        _pickView.Visible = false;
        row.AddChild(_pickView);
        return row;
    }

    Control BuildRows()
    {
        var col = Ui.VBox();
        col.CustomMinimumSize = new Vector2(ColumnWidth, 0);
        for (var i = 0; i < RowCount; i++)
        {
            var row = i;
            var b = new Button
            {
                ToggleMode = true, ButtonGroup = _rowGroup, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 8 * 8),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            var line = Ui.HBox();
            line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            line.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.Alignment = BoxContainer.AlignmentMode.Begin;
            _rowIcons[i] = Art(new Vector2(48, 32));
            line.AddChild(_rowIcons[i]);
            line.AddChild(Ui.Gap(ShooterTheme.Space));
            var text = Ui.VBox();
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _rowTitles[i] = Ui.Lbl(RowTitle(i), "HudSmall");
            _rowValues[i] = Ui.Lbl("", null);
            text.AddChild(_rowTitles[i]);
            text.AddChild(_rowValues[i]);
            line.AddChild(text);
            b.AddChild(line);
            b.Pressed += () => OpenRow(row);
            _rowButtons[i] = b;
            col.AddChild(b);
            if (i < RowCount - 1) col.AddChild(Ui.Gap(ShooterTheme.Space));
        }
        return col;
    }

    Control BuildCentre()
    {
        var col = Ui.VBox();
        col.CustomMinimumSize = new Vector2(CentreWidth, 0);
        _className = Ui.Lbl("", "HudHead");
        col.AddChild(_className);
        _nameEdit = new LineEdit
        {
            MaxLength = LoadoutRules.MaxNameChars, PlaceholderText = Strings.T("loadout.name.placeholder"), Visible = false,
            CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2),
        };
        _nameEdit.TextSubmitted += _ => CommitRename(true);
        _nameEdit.FocusExited += () => CommitRename(false);
        col.AddChild(_nameEdit);
        _preview = new CamoPreview(new Vector2(CentreWidth, 400));
        col.AddChild(_preview.Root);
        col.AddChild(Ui.Lbl(Strings.T("camo.turn.hint"), "HudSmall", 0, true));
        return col;
    }

    /// <summary>The primary (large), the secondary, then the perk and the three killstreaks with the kills each needs.</summary>
    Control BuildRight()
    {
        var col = Ui.VBox();
        col.CustomMinimumSize = new Vector2(RightWidth, 0);
        _primary = new WeaponBlock(new Vector2(300, 150));
        col.AddChild(_primary.Root);
        col.AddChild(Ui.Gap(ShooterTheme.Space));
        _secondary = new WeaponBlock(new Vector2(200, 100));
        col.AddChild(_secondary.Root);
        col.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        var cells = Ui.HBox();
        for (var i = 0; i < 4; i++)
        {
            var cell = Ui.VBox();
            cell.CustomMinimumSize = new Vector2(88, 0);
            _cellIcons[i] = Art(new Vector2(64, 64));
            _cellNames[i] = Ui.Lbl("", "HudSmall", 88, true);
            _cellNotes[i] = Ui.Lbl("", "HudDim", 88, true);
            cell.AddChild(_cellIcons[i]);
            cell.AddChild(_cellNames[i]);
            cell.AddChild(_cellNotes[i]);
            cells.AddChild(cell);
            if (i < 3) cells.AddChild(Ui.Gap(ShooterTheme.Space / 2));
        }
        col.AddChild(cells);
        return col;
    }

    /// <summary>The picker: the grid of options on the left, the compare panel on the right.</summary>
    Control BuildPicker()
    {
        var row = Ui.HBox();
        var left = Ui.VBox();
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _pickTitle = Ui.Lbl("", "HudHead");
        left.AddChild(_pickTitle);
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 440), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true,
        };
        _grid = new GridContainer { Columns = PickColumns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_grid);
        left.AddChild(scroll);
        row.AddChild(left);
        row.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        row.AddChild(BuildCompare());
        return row;
    }

    Control BuildCompare()
    {
        var col = Ui.VBox();
        col.CustomMinimumSize = new Vector2(CompareWidth, 0);
        col.AddChild(Ui.Lbl(Strings.T("loadout.compare"), "HudDim"));
        _cmpTitle = Ui.Lbl("", "HudHead", 0, true);
        col.AddChild(_cmpTitle);
        for (var i = 0; i < 5; i++)
        {
            var block = Ui.VBox();
            var top = Ui.HBox();
            _cmpName[i] = Ui.Lbl("", "HudDim");
            _cmpName[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _cmpValue[i] = Ui.Lbl("", null);
            top.AddChild(_cmpName[i]);
            top.AddChild(_cmpValue[i]);
            block.AddChild(top);
            _cmpBar[i] = Bar("StatBar", 14);
            _cmpBarEq[i] = Bar("StatBarSaved", 6);
            block.AddChild(_cmpBar[i]);
            block.AddChild(_cmpBarEq[i]);
            _cmpRows[i] = block;
            col.AddChild(block);
            col.AddChild(Ui.Gap(ShooterTheme.Space / 2));
        }
        _cmpNote = Ui.Lbl("", "HudDim", 0, true);
        col.AddChild(_cmpNote);
        return col;
    }

    Control BuildBottomBar()
    {
        var bar = Ui.HBox();
        _status = Ui.Lbl("", "HudDim");
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bar.AddChild(_status);
        _undo = Ui.Btn(Strings.T("loadout.undo"), Undo, null, 200);
        _rename = Ui.Btn(Strings.T("loadout.rename"), StartRename, null, 160);
        _reset = Ui.Btn(Strings.T("loadout.reset"), ConfirmReset, "MenuDanger", 220);
        _copy = Ui.Btn(Strings.T("loadout.copy"), ConfirmCopy, null, 180);
        bar.AddChild(_undo);
        bar.AddChild(_rename);
        bar.AddChild(_reset);
        bar.AddChild(_copy);
        bar.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        return bar;
    }

    /// <summary>A large weapon: the image, the name and the category badge.</summary>
    sealed class WeaponBlock
    {
        public readonly VBoxContainer Root = Ui.VBox();
        public readonly TextureRect Image;
        public readonly Label Name = Ui.Lbl("", "HudHead");
        public readonly HBoxContainer Badge = Ui.HBox();
        public readonly TextureRect BadgeIcon;
        public readonly Label BadgeText = Ui.Lbl("", "HudDim");

        public WeaponBlock(Vector2 art)
        {
            Image = Art(art);
            BadgeIcon = Art(new Vector2(32, 32));
            Badge.AddChild(BadgeIcon);
            Badge.AddChild(BadgeText);
            Root.AddChild(Image);
            Root.AddChild(Name);
            Root.AddChild(Badge);
        }
    }

    static TextureRect Art(Vector2 size) => new()
    {
        CustomMinimumSize = size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    static ProgressBar Bar(string variation, int height) => new()
    {
        MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, ThemeTypeVariation = variation,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    // ---- server data ----

    /// <summary>A new profile from the server. The drafts are not touched: an unconfirmed edit stays on screen until its save is answered.</summary>
    void OnProfile(ProfileMsg p)
    {
        _profile = p;
        _devMode = DevUnlocks.IsMarked(p.Unlocks);
        _devBadge.Visible = _devMode;
        _level.Text = Strings.F("loadout.level", p.Level);
        Sync();
    }

    /// <summary>An answer to a save: a rejection is toasted (the status line keeps the reason). Accepted saves need no toast.</summary>
    void OnAck(LoadoutAckInfo ack)
    {
        if (!ack.Ok) Shell.Toast(Strings.F("loadout.invalid", ack.Reason), ToastKind.Error);
        Sync();
    }

    // ---- navigation and editing ----

    string RowTitle(int row) => row switch
    {
        0 => Strings.T("loadout.primary"),
        1 => Strings.T("loadout.secondary"),
        2 => Strings.T("loadout.perk"),
        6 => Strings.T("loadout.camo"),
        _ => Strings.F("loadout.streak", row - 2),
    };

    /// <summary>Tab names: the class name, or "Class N" when it has none.</summary>
    string NameOf(int slot)
    {
        var n = LoadoutAt(slot).Name;
        return n == "" ? LoadoutRules.DefaultName(slot) : n;
    }

    void SwitchTo(int slot)
    {
        if (slot == _tab) return;
        if (_picker >= 0) ClosePicker(false);
        _tab = slot;
        GameSettings.SaveUi("loadout_tab", slot.ToString());
        Sync();
    }

    /// <summary>Makes the open class the one the next spawn uses (the star toggle).</summary>
    void UseNext()
    {
        if (_net == null || _profile?.ActiveSlot == _tab) return;
        _net.SelectSlot(_tab);
        Shell.Toast(Strings.F("loadout.toast.active", _tab + 1));
    }

    void OpenRow(int row)
    {
        _row = row;
        if (row == RowCamo)
        {
            if (CustomizeFactory is { } f) Shell.Push(f(_tab));
            return;
        }
        _picker = row;
        _pickerKey = "";
        _classView.Visible = false;
        _pickView.Visible = true;
        _hint.Visible = false;
        _pickHint.Visible = true;
        _pickTitle.Text = RowTitle(row);
        RebuildPicker();
    }

    void ClosePicker(bool refocusRow = true)
    {
        var row = _picker;
        _picker = -1;
        _pickerKey = "";
        _classView.Visible = true;
        _pickView.Visible = false;
        _hint.Visible = true;
        _pickHint.Visible = false;
        if (refocusRow && row >= 0) _rowButtons[row].CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Chooses a picker's item for the open class. The edit autosaves, and the picker closes back to the class.</summary>
    void Choose(PickerOption o)
    {
        if (Autosave == null || _picker < 0) return;
        if (!o.Unlocked && !_devMode)
        {
            Shell.Toast(Strings.F("loadout.locked", o.UnlockLevel), ToastKind.Warn);
            return;
        }
        var d = Draft;
        var next = _picker switch
        {
            0 => d with { PrimaryWeapon = o.Id },
            1 => d with { SecondaryWeapon = o.Id },
            2 => d with { Perk = o.Id },
            3 => d with { Streak1 = o.Id },
            4 => d with { Streak2 = o.Id },
            _ => d with { Streak3 = o.Id },
        };
        Autosave?.Edit(next, Now());
        ClosePicker();
        Sync();
    }

    /// <summary>Steps the open class back one edit (Ctrl+Z and the Undo button). The step saves like any edit.</summary>
    void Undo()
    {
        if (Autosave?.Undo(_tab, Now()) == true) Sync();
    }

    void StartRename()
    {
        if (_profile == null || Autosave == null) return;
        _renaming = true;
        _nameEdit.Text = NameOf(_tab);
        _className.Visible = false;
        _nameEdit.Visible = true;
        _nameEdit.GrabFocus();
        _nameEdit.SelectAll();
    }

    /// <summary>Checks and saves the typed name. An invalid name is toasted and the old one stays.</summary>
    void CommitRename(bool refocus)
    {
        if (!_renaming) return;
        _renaming = false;
        var name = LoadoutRules.ValidateName(_nameEdit.Text, out var error);
        _nameEdit.Visible = false;
        _className.Visible = true;
        if (name == null) Shell.Toast(Strings.F("loadout.name.invalid", error ?? ""), ToastKind.Error);
        else if (name != Draft.Name) Autosave?.Edit(Draft with { Name = name }, Now());
        if (refocus) _rename.CallDeferred(Control.MethodName.GrabFocus);
        Sync();
    }

    void CancelRename()
    {
        if (!_renaming) return;
        _renaming = false;
        _nameEdit.Visible = false;
        _className.Visible = true;
        _rename.CallDeferred(Control.MethodName.GrabFocus);
        Sync();
    }

    /// <summary>Confirm first: names what is lost. Unsent edits go out before the reset, so the server applies them in order.</summary>
    void ConfirmReset()
    {
        if (_net == null) return;
        var slot = _tab + 1;
        var net = _net;
        var target = _tab;
        Shell.Push(new DialogScreen(Strings.F("loadout.reset.title", slot), Strings.F("loadout.reset.body", slot),
            new[] { new DialogScreen.Choice(Strings.T("loadout.reset.confirm"), () => { net.FlushAutosave(); net.ResetLoadout(target); }, "MenuDanger") }));
    }

    /// <summary>Step one: pick the class to overwrite. Step two confirms the overwrite (the copy has no undo here).</summary>
    void ConfirmCopy()
    {
        if (_net == null) return;
        var targets = Enumerable.Range(0, Loadout.SlotCount).Where(i => i != _tab)
            .Select(i => new DialogScreen.Choice(NameOf(i), () => ConfirmOverwrite(i))).ToArray();
        Shell.Push(new DialogScreen(Strings.F("loadout.copy.title", _tab + 1), Strings.T("loadout.copy.body"), targets));
    }

    void ConfirmOverwrite(int to)
    {
        if (_net == null) return;
        var net = _net;
        var from = _tab;
        Shell.Push(new DialogScreen(Strings.F("loadout.copy.overwrite.title", to + 1), Strings.F("loadout.copy.overwrite.body", from + 1, to + 1),
            new[] { new DialogScreen.Choice(Strings.T("loadout.copy.confirm"), () => { net.FlushAutosave(); net.CopyLoadout(from, to); }, "MenuDanger") }));
    }

    /// <summary>Esc: one level up. A rename is cancelled, a picker closes, and the screen flushes unsent edits on the way out.</summary>
    public override void Back()
    {
        if (_renaming) { CancelRename(); return; }
        if (_picker >= 0) { ClosePicker(); return; }
        _net?.FlushAutosave();
        base.Back();
    }

    /// <summary>
    /// Ctrl+Z undoes the open class's last edit; 1-5 pick a class. While a name is being typed, every key goes to the field
    /// except Esc, which cancels the rename.
    /// </summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _profile == null) return false;
        if (_renaming)
        {
            if (k.Keycode != Key.Escape) return false;
            CancelRename();
            return true;
        }
        if (k.Keycode == Key.Z && k.CtrlPressed && !k.ShiftPressed)
        {
            if (Autosave?.CanUndo(_tab) != true) return false;
            Undo();
            return true;
        }
        var slot = k.Keycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4, _ => -1 };
        if (slot < 0) return false;
        SwitchTo(slot);
        return true;
    }

    // ---- drawing ----

    /// <summary>Redraws everything from the draft, the server copy and the autosave state.</summary>
    void Sync()
    {
        if (_profile == null)
        {
            Refresh();
            return;
        }
        var p = _profile;
        var data = _data();
        for (var i = 0; i < Loadout.SlotCount; i++)
        {
            _tabNames[i].Text = NameOf(i);
            _tabStars[i].Visible = p.ActiveSlot == i;
            _tabs[i].ButtonPressed = i == _tab;
        }
        var onNext = p.ActiveSlot == _tab;
        _useNext.Disabled = onNext;
        _useNextText.Text = Strings.T(onNext ? "loadout.use_next.on" : "loadout.use_next");
        _useNextStar.Visible = onNext;
        _className.Text = NameOf(_tab);

        var d = Draft;
        RefreshRows(d, data);
        RefreshRight(d, data);
        var camo = p.Camos is { } list && _tab < list.Count ? list[_tab] : CamoSpec.Default;
        _preview.Show(camo, data.Camo);
        _preview.ShowWeapon(d.PrimaryWeapon);

        RefreshStatus();
        _undo.Disabled = Autosave?.CanUndo(_tab) != true;
        _rename.Disabled = Autosave == null;
        _reset.Disabled = _net == null;
        _copy.Disabled = _net == null;
        if (_picker >= 0) RebuildPicker();
        Refresh();
    }

    void RefreshStatus()
    {
        var state = Autosave?.StateOf(_tab) ?? AutosaveState.Saved;
        var reason = Autosave?.RejectReason(_tab) ?? "";
        (_status.Text, _status.ThemeTypeVariation) = state switch
        {
            AutosaveState.Pending or AutosaveState.Saving => (Strings.T("loadout.state.saving"), "HudDim"),
            AutosaveState.Retrying => (Strings.F("loadout.state.retry", reason), "HudWarn"),
            AutosaveState.Rejected => (Strings.F("loadout.state.error", reason), "MenuError"),
            _ => (Strings.T("loadout.state.saved"), "MenuOk"),
        };
    }

    void RefreshRows(Loadout d, GameData data)
    {
        for (var i = 0; i < RowCount; i++)
        {
            _rowButtons[i].ButtonPressed = i == _row;
            switch (i)
            {
                case 0:
                case 1:
                    _rowIcons[i].Texture = Tex(IconKind.Weapon, i == 0 ? d.PrimaryWeapon : d.SecondaryWeapon);
                    _rowValues[i].Text = WeaponName(i == 0 ? d.PrimaryWeapon : d.SecondaryWeapon);
                    break;
                case 2:
                    _rowIcons[i].Texture = Tex(IconKind.Perk, d.Perk);
                    _rowValues[i].Text = d.Perk == "" ? Strings.T("loadout.none") : PerkLabel(data, d.Perk);
                    break;
                case RowCamo:
                    _rowIcons[i].Texture = null;
                    _rowValues[i].Text = Strings.T("loadout.customize");
                    break;
                default:
                    var streak = StreakAt(d, i - 3);
                    _rowIcons[i].Texture = Tex(IconKind.Streak, streak);
                    _rowValues[i].Text = streak == "" ? Strings.T("loadout.none") : StreakLabel(streak);
                    break;
            }
        }
    }

    void RefreshRight(Loadout d, GameData data)
    {
        FillWeapon(_primary, d.PrimaryWeapon, data);
        FillWeapon(_secondary, d.SecondaryWeapon, data);
        _cellIcons[0].Texture = Tex(IconKind.Perk, d.Perk);
        _cellNames[0].Text = d.Perk == "" ? Strings.T("loadout.none") : PerkLabel(data, d.Perk);
        _cellNotes[0].Text = Strings.T("loadout.perk");
        for (var s = 0; s < 3; s++)
        {
            var id = StreakAt(d, s);
            var cell = s + 1;
            _cellIcons[cell].Texture = Tex(IconKind.Streak, id);
            _cellNames[cell].Text = id == "" ? Strings.T("loadout.none") : StreakLabel(id);
            var needed = data.Killstreaks.Streaks.FirstOrDefault(x => x.Id == id)?.KillsRequired;
            _cellNotes[cell].Text = needed is { } n && id != "" ? Strings.F("loadout.kills", n) : "";
        }
    }

    void FillWeapon(WeaponBlock block, string id, GameData data)
    {
        var w = id == "" ? null : data.Weapons.Find(id);
        block.Image.Texture = Tex(IconKind.Weapon, w == null ? null : id);
        block.Name.Text = w == null ? Strings.T("loadout.empty") : WeaponLabel(id);
        block.Badge.Visible = w != null;
        if (w == null) return;
        var category = IconRegistry.CategoryId(w.Category);
        block.BadgeIcon.Texture = Tex(IconKind.Category, category);
        block.BadgeIcon.Visible = block.BadgeIcon.Texture != null;
        block.BadgeText.Text = CategoryLabel(w.Category);
    }

    /// <summary>Rebuilds the picker's grid when its rows change (so a redraw does not steal the player's focus).</summary>
    void RebuildPicker()
    {
        if (_profile == null || _picker < 0) return;
        var options = OptionsFor(_picker);
        var current = CurrentId(_picker);
        var key = $"{_picker}|{current}|{_devMode}|" + string.Join(",", options.Select(o => $"{o.Id}:{o.Unlocked}:{IsNew(o)}"));
        if (key == _pickerKey) return;
        _pickerKey = key;
        foreach (var child in _grid.GetChildren())
        {
            _grid.RemoveChild(child);
            child.QueueFree();
        }
        // focus goes to the equipped item, or the first one; the compare panel shows that item
        var focusAt = Math.Max(0, options.ToList().FindIndex(o => o.Id == current));
        for (var i = 0; i < options.Count; i++) _grid.AddChild(MakeCard(options[i], current));
        if (options.Count == 0) { Compare(null); return; }
        _grid.GetChild<Button>(focusAt).CallDeferred(Control.MethodName.GrabFocus);
        Compare(options[focusAt]);
    }

    IReadOnlyList<PickerOption> OptionsFor(int row)
    {
        var p = _profile!;
        var data = _data();
        return LoadoutEditor.Options((LoadoutPicker)row, data.Weapons, data.Perks, data.Killstreaks.UnlockLevels, p.Level, p.Unlocks);
    }

    string CurrentId(int row)
    {
        var d = Draft;
        return row switch { 0 => d.PrimaryWeapon, 1 => d.SecondaryWeapon, 2 => d.Perk, 3 => d.Streak1, 4 => d.Streak2, _ => d.Streak3 };
    }

    /// <summary>NEW: unlocked at the level the player has just reached (the fresh unlocks of the last level-up).</summary>
    bool IsNew(PickerOption o) => !_devMode && o.Unlocked && o.UnlockLevel > 0 && o.UnlockLevel == _profile?.Level;

    /// <summary>One grid cell: the image, the name, and the state (padlock and level, Equipped, NEW).</summary>
    Button MakeCard(PickerOption o, string current)
    {
        var data = _data();
        var card = new Button
        {
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(PickCardWidth, PickCardHeight),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var locked = !o.Unlocked && !_devMode;
        var col = Ui.VBox();
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        var art = Art(new Vector2(PickCardWidth - 24, 60));
        art.Texture = _picker < 2 ? Tex(IconKind.Weapon, o.Id) : Tex(_picker == 2 ? IconKind.Perk : IconKind.Streak, o.Id);
        art.Modulate = locked ? ShooterTheme.DisabledText : Colors.White; // locked items are greyed, and say why in text
        col.AddChild(art);
        col.AddChild(Ui.Lbl(NameFor(o), locked ? "HudDim" : null));
        var status = Ui.HBox();
        if (locked)
        {
            status.AddChild(new Glyph(Glyph.Kind.Padlock, ShooterTheme.Warn, ShooterTheme.Space * 2));
            status.AddChild(Ui.Lbl(Strings.F("loadout.locked", o.UnlockLevel), "HudSmall"));
        }
        else if (o.Id == current)
        {
            status.AddChild(Ui.Lbl(Strings.T("loadout.current"), "HudSmall"));
        }
        if (IsNew(o)) status.AddChild(Ui.Lbl(Strings.T("loadout.new"), "HudAccent"));
        col.AddChild(status);
        card.AddChild(col);
        card.Pressed += () => Choose(o);
        card.FocusEntered += () => Compare(o);
        card.MouseEntered += () => Compare(o);
        return card;
    }

    string NameFor(PickerOption o) => _picker switch
    {
        < 2 => o.Id == "" ? Strings.T("loadout.empty") : WeaponLabel(o.Id),
        2 => o.Id == "" ? Strings.T("loadout.none") : PerkLabel(_data(), o.Id),
        _ => o.Id == "" ? Strings.T("loadout.none") : StreakLabel(o.Id),
    };

    /// <summary>
    /// The compare panel for the highlighted option: five bars (the option, and the equipped item under it) with text values.
    /// Perks and killstreaks have no bars; their panel says what they need.
    /// </summary>
    void Compare(PickerOption? o)
    {
        if (o == null || _picker < 0)
        {
            _cmpTitle.Text = "";
            _cmpNote.Text = "";
            foreach (var r in _cmpRows) r.Visible = false;
            return;
        }
        var data = _data();
        _cmpTitle.Text = NameFor(o);
        if (_picker < 2)
        {
            var current = CurrentId(_picker);
            var w = o.Id == "" ? null : data.Weapons.Find(o.Id);
            var eq = current == "" ? null : data.Weapons.Find(current);
            var stats = w == null ? null : LoadoutEditor.CompareStats(w, data.Weapons);
            var equipped = eq == null ? null : LoadoutEditor.CompareStats(eq, data.Weapons);
            for (var i = 0; i < 5; i++)
            {
                var key = LoadoutEditor.CompareKeys[i];
                _cmpRows[i].Visible = true;
                _cmpName[i].Text = Strings.T("loadout.stat." + key);
                _cmpBar[i].Value = stats?[i].Share ?? 0;
                _cmpBarEq[i].Value = equipped?[i].Share ?? 0;
                var value = stats == null ? Strings.T("loadout.empty") : StatText(key, stats[i].Value);
                var was = equipped == null ? Strings.T("loadout.empty") : StatText(key, equipped[i].Value);
                _cmpValue[i].Text = $"{value}   {Strings.F("loadout.equipped", was)}";
            }
            _cmpNote.Text = w == null ? "" : Strings.T("loadout.compare.note");
            return;
        }
        foreach (var r in _cmpRows) r.Visible = false;
        var lines = new List<string>();
        if (o.Id != "")
        {
            if (_picker >= 3) lines.Add(Strings.F("loadout.kills", data.Killstreaks.Streaks.FirstOrDefault(x => x.Id == o.Id)?.KillsRequired ?? 0));
            lines.Add(!o.Unlocked && !_devMode ? Strings.F("loadout.locked", o.UnlockLevel) : Strings.T("loadout.available"));
        }
        _cmpNote.Text = string.Join("\n", lines);
    }

    static string StatText(string key, float v) => key switch
    {
        LoadoutEditor.StatRate => Strings.F("loadout.rate", v),
        LoadoutEditor.StatRecoil => Strings.F("loadout.recoil", v),
        LoadoutEditor.StatMobility => Strings.F("fmt.times", v),
        _ => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    void Refresh()
    {
        var has = _profile != null;
        _body.Visible = has;
        _empty.Visible = !has;
        _emptyBack.Visible = !has;
        if (!has) _empty.Text = _net == null ? Strings.T("loadout.offline") : Strings.T("loadout.loading");
    }

    // ---- names and icons ----

    static string StreakAt(Loadout d, int i) => i switch { 0 => d.Streak1, 1 => d.Streak2, _ => d.Streak3 };

    /// <summary>The icon of an id, or null for "nothing" (an empty slot, or a category without a badge).</summary>
    Texture2D? Tex(IconKind kind, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return ResourceLoader.Load<Texture2D>(_icons.Resolve(kind, id, p => ResourceLoader.Exists(p)));
    }

    static string WeaponName(string id) => id == "" ? Strings.T("loadout.empty") : WeaponLabel(id);

    static string WeaponLabel(string id)
    {
        var k = "weapon." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    static string CategoryLabel(WeaponCategory c)
    {
        var k = "loadout.group." + c;
        var t = Strings.T(k);
        return t == k ? c.ToString() : t;
    }

    static string StreakLabel(string id)
    {
        var k = "streak." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    static string PerkLabel(GameData data, string id) => data.Perks.Find(id)?.Name ?? id;
}

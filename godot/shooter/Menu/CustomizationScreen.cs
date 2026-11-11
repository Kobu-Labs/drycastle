using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The camouflage customiser (ticket #141; docs/standards/ui-ux.md sections 1 to 5). One look per loadout slot: five slot
/// tabs, a live turntable preview (drag or arrows, prone toggle), four groups of controls built from the camo.json
/// parameters (suit colours, pattern, foliage, face paint) with a preset picker for the suit and the face, randomize,
/// reset, undo (Ctrl+Z), a share code (copy and paste, base64 of the CamoSpec), Revert and Apply. Apply sends the look for
/// the open slot; the server answers with the profile. Esc goes one level up; unsaved edits ask first, the same as the
/// loadout editor. The draft lives on this screen until Apply, so nothing reaches the server by accident.
/// </summary>
public partial class CustomizationScreen : MenuScreen
{
    /// <summary>How many undo steps are kept. Each slider tick is one step.</summary>
    const int UndoLimit = 200;

    readonly Func<NetClient?> _client;
    readonly Func<GameData> _data;
    readonly Random _rng = new(); // slot -1 (given to the constructor) means the active slot, decided when the profile arrives
    NetClient? _net;
    CamoFile _camo = null!;
    IReadOnlyList<CamoParam> _params = Array.Empty<CamoParam>();
    int _slot = -1, _group;
    CamoSpec _saved = CamoSpec.Default, _draft = CamoSpec.Default;
    readonly List<CamoSpec> _undo = new();
    bool _hasProfile, _awaitingSave, _syncing;
    CamoPreview _preview = null!;

    readonly record struct Row(CamoParam P, Control Input, Label Value);
    readonly List<Row> _rows = new();

    static readonly string[] GroupKeys = { "camo.group.suit", "camo.group.pattern", "camo.group.foliage", "camo.group.face" };
    readonly Button[] _slotTabs = new Button[Loadout.SlotCount];
    readonly Button[] _groupTabs = new Button[4];
    readonly Control[] _groupPanes = new Control[4];
    Label _level = null!, _note = null!, _empty = null!;
    Control _body = null!;
    LineEdit _code = null!;
    Button _emptyBack = null!, _undoBtn = null!, _revert = null!, _apply = null!;
    CheckButton _prone = null!;

    /// <param name="client">The live connection (null on the main menu: the screen then says to join a server).</param>
    /// <param name="data">The data files: camo.json and the weapon data for the level.</param>
    /// <param name="slot">The loadout slot to edit, or -1 for the active one.</param>
    public CustomizationScreen(Func<NetClient?> client, Func<GameData> data, int slot = -1)
    {
        _client = client; _data = data;
        _slot = slot < 0 ? -1 : LoadoutEditor.ClampTab(slot);
    }

    bool Dirty => _draft != _saved;

    public override void _Ready()
    {
        base._Ready();
        _net = _client();
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
        var data = _data();
        _camo = data.Camo;
        _params = CamoEditor.Params(_camo);
        _group = Math.Clamp(int.TryParse(GameSettings.LoadUi("camo_group", "0"), out var g) ? g : 0, 0, GroupKeys.Length - 1);

        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(1000, 0);

        var head = Ui.HBox();
        head.AddChild(Ui.Lbl(Strings.T("camo.title"), "HudDisplay"));
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

        var columns = Ui.HBox();
        columns.AddChild(BuildPreview());
        columns.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        columns.AddChild(BuildEditor());
        body.AddChild(columns);

        body.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        var bottom = Ui.HBox();
        _note = Ui.Lbl("", "HudDim", 0, true);
        _note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bottom.AddChild(_note);
        _undoBtn = Ui.Btn(Strings.T("camo.undo"), Undo, null, 160);
        _revert = Ui.Btn(Strings.T("camo.revert"), RequestRevert, "MenuDanger", 160);
        _apply = Ui.Btn(Strings.T("camo.apply"), Apply, "MenuPrimary", 200);
        bottom.AddChild(_undoBtn);
        bottom.AddChild(_revert);
        bottom.AddChild(_apply);
        body.AddChild(bottom);
        body.AddChild(Ui.Gap(ShooterTheme.Space));

        body.AddChild(Ui.Lbl(Strings.T("camo.hint"), "HudSmall"));
        body.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        return v;
    }

    Control BuildPreview()
    {
        var col = Ui.VBox();
        _preview = new CamoPreview(new Vector2(360, 440));
        col.AddChild(_preview.Root);
        _prone = new CheckButton { Text = Strings.T("camo.prone"), FocusMode = Control.FocusModeEnum.All };
        _prone.Toggled += on => _preview.SetProne(on);
        col.AddChild(_prone);
        col.AddChild(Ui.Lbl(Strings.T("camo.turn.hint"), "HudSmall", 0, true));
        return col;
    }

    Control BuildEditor()
    {
        var col = Ui.VBox();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var group = new ButtonGroup();
        var slotRow = Ui.HBox();
        for (var i = 0; i < Loadout.SlotCount; i++)
        {
            var slot = i;
            var b = TabButton(group);
            b.Pressed += () => RequestSlot(slot);
            _slotTabs[i] = b;
            slotRow.AddChild(b);
        }
        col.AddChild(slotRow);
        col.AddChild(Ui.Gap(ShooterTheme.Space));

        var groupRow = Ui.HBox();
        var groups = new ButtonGroup();
        for (var i = 0; i < GroupKeys.Length; i++)
        {
            var g = i;
            var b = TabButton(groups);
            b.Text = Strings.T(GroupKeys[i]);
            b.ButtonPressed = i == _group;
            b.Pressed += () => ShowGroup(g);
            _groupTabs[i] = b;
            groupRow.AddChild(b);
        }
        col.AddChild(groupRow);
        col.AddChild(Ui.Gap(ShooterTheme.Space));

        // the panes share one scroll area, so the list stays usable at 1280x720
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, ShooterTheme.Space * 46), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var stack = Ui.VBox();
        stack.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var g = 0; g < GroupKeys.Length; g++)
        {
            var pane = Ui.VBox();
            pane.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            if (g == 0) pane.AddChild(PresetRow(true));
            if (g == 3) pane.AddChild(PresetRow(false));
            foreach (var p in _params.Where(p => p.Group == (CamoGroup)g)) pane.AddChild(ParamRow(p));
            _groupPanes[g] = pane;
            stack.AddChild(pane);
        }
        scroll.AddChild(stack);
        col.AddChild(scroll);
        ShowGroup(_group);

        col.AddChild(Ui.Gap(ShooterTheme.Space));
        var actions = Ui.HBox();
        actions.AddChild(Ui.Btn(Strings.T("camo.randomize"), Randomize, null, 160));
        actions.AddChild(Ui.Btn(Strings.T("camo.reset"), ResetLook, null, 160));
        actions.AddChild(Ui.Btn(Strings.T("camo.copy"), CopyCode, null, 160));
        actions.AddChild(Ui.Btn(Strings.T("camo.paste"), PasteCode, null, 160));
        col.AddChild(actions);
        col.AddChild(Ui.Gap(ShooterTheme.Space / 2));
        var codeRow = Ui.HBox();
        codeRow.AddChild(Ui.Lbl(Strings.T("camo.code"), "HudDim", 160));
        _code = new LineEdit { Editable = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, Alignment = HorizontalAlignment.Left };
        codeRow.AddChild(_code);
        col.AddChild(codeRow);
        return col;
    }

    static Button TabButton(ButtonGroup group) => new()
    {
        ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2),
    };

    /// <summary>A preset picker for the suit (applies colours, pattern and foliage) or the face (applies the paint).</summary>
    Control PresetRow(bool suit)
    {
        var row = Ui.HBox();
        row.AddChild(Ui.Lbl(Strings.T("camo.preset"), "HudDim", 240));
        var ob = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2) };
        var presets = suit ? _camo.SuitPresets : _camo.FacePresets;
        foreach (var p in presets) ob.AddItem(p.Name);
        if (presets.Count > 0) ob.Select(0);
        row.AddChild(ob);
        row.AddChild(Ui.Btn(Strings.T("camo.preset.apply"), () =>
        {
            var idx = ob.Selected;
            if (idx < 0 || idx >= presets.Count) return;
            SetDraft(suit ? CamoEditor.WithSuitPreset(_draft, presets[idx].Spec) : CamoEditor.WithFacePreset(_draft, presets[idx].Spec), true);
            Shell.Toast(Strings.F("camo.toast.preset", presets[idx].Name));
        }, null, 160));
        return row;
    }

    /// <summary>One camo.json parameter: its label, the control that edits it, and the value written beside it.</summary>
    Control ParamRow(CamoParam p)
    {
        var row = Ui.HBox();
        row.AddChild(Ui.Lbl(Strings.T("camo.param." + p.Key), "HudDim", 240));
        var value = Ui.Lbl("", null, ShooterTheme.Space * 12);
        Control input = p.Kind switch
        {
            CamoParamKind.Choice => ChoiceInput(p),
            CamoParamKind.Colour => ColourInput(p, value),
            _ => SliderInput(p, value),
        };
        input.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(input);
        row.AddChild(value);
        _rows.Add(new Row(p, input, value));
        var wrap = Ui.VBox();
        wrap.AddChild(row);
        wrap.AddChild(Ui.Gap(ShooterTheme.Space / 2));
        return wrap;
    }

    Control SliderInput(CamoParam p, Label value)
    {
        var s = new HSlider
        {
            MinValue = p.Min, MaxValue = p.Max, Step = 1, FocusMode = Control.FocusModeEnum.All,
            CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2), MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        s.ValueChanged += v =>
        {
            if (_syncing) return;
            value.Text = ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Edit(p, (int)v);
        };
        return s;
    }

    Control ChoiceInput(CamoParam p)
    {
        var names = ChoiceNames(p.Key);
        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2) };
        foreach (var n in names) ob.AddItem(n);
        ob.ItemSelected += idx =>
        {
            if (_syncing) return;
            Edit(p, (int)idx);
        };
        return ob;
    }

    Control ColourInput(CamoParam p, Label value)
    {
        var cp = new ColorPickerButton { FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 3 * ShooterTheme.Space * 2), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        cp.ColorChanged += c =>
        {
            if (_syncing) return;
            var packed = ((int)Math.Round(c.R * 255) << 16) | ((int)Math.Round(c.G * 255) << 8) | (int)Math.Round(c.B * 255);
            value.Text = Hex(packed);
            Edit(p, packed);
        };
        return cp;
    }

    IReadOnlyList<string> ChoiceNames(string key) => key switch
    {
        "suit_pattern" => _camo.SuitPatterns,
        "face_pattern" => _camo.FacePatterns,
        _ => _camo.Vegetation,
    };

    static string Hex(int packed) => "#" + packed.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

    // ---- server data ----

    void OnProfile(ProfileMsg p)
    {
        var first = !_hasProfile;
        _hasProfile = true;
        if (_slot < 0) _slot = LoadoutEditor.ClampTab(p.ActiveSlot);
        var before = _saved;
        _saved = ServerCopy(p, _slot);
        if (first || _draft == before) { _draft = _saved; _undo.Clear(); }
        if (_awaitingSave) { _awaitingSave = false; Shell.Toast(Strings.F("camo.toast.saved", _slot + 1)); }
        _level.Text = Strings.F("loadout.level", p.Level);
        for (var i = 0; i < Loadout.SlotCount; i++)
            _slotTabs[i].Text = Strings.F("loadout.slot", i + 1) + (p.ActiveSlot == i ? "   " + Strings.T("loadout.active") : "");
        _slotTabs[_slot].ButtonPressed = true;
        PushToControls();
        Preview();
        Sync();
    }

    void OnServerError(string reason)
    {
        if (!_awaitingSave) return;
        _awaitingSave = false;
        Shell.Toast(reason, ToastKind.Error);
        Sync();
    }

    static CamoSpec ServerCopy(ProfileMsg p, int slot) => p.Camos is { } list && slot < list.Count ? list[slot] : CamoSpec.Default;

    // ---- editing ----

    void RequestSlot(int slot)
    {
        if (slot == _slot) return;
        if (!Dirty) { SwitchTo(slot); return; }
        _slotTabs[_slot].ButtonPressed = true; // the open tab stays pressed until the player confirms
        Shell.Push(Discard(() => SwitchTo(slot)));
    }

    void SwitchTo(int slot)
    {
        _slot = slot;
        _slotTabs[slot].ButtonPressed = true;
        _saved = _net?.Profile is { } p ? ServerCopy(p, slot) : CamoSpec.Default;
        _draft = _saved;
        _undo.Clear();
        _awaitingSave = false;
        PushToControls();
        Preview();
        Sync();
    }

    void RequestRevert()
    {
        if (!Dirty) return;
        Shell.Push(Discard(() => SetDraft(_saved, true)));
    }

    /// <summary>The confirm for unsaved edits: Cancel is focused (the destructive choice is not the default).</summary>
    DialogScreen Discard(Action run) => new(Strings.T("camo.discard.title"), Strings.F("camo.discard.body", _slot + 1),
        new[] { new DialogScreen.Choice(Strings.T("camo.discard.confirm"), run, "MenuDanger") });

    void ShowGroup(int g)
    {
        _group = g;
        for (var i = 0; i < _groupPanes.Length; i++)
        {
            if (_groupPanes[i] != null) _groupPanes[i].Visible = i == g;
            _groupTabs[i].ButtonPressed = i == g;
        }
        GameSettings.SaveUi("camo_group", g.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>One control moved: the draft takes the value and the undo stack remembers the step.</summary>
    void Edit(CamoParam p, int value)
    {
        var next = p.Set(_draft, value);
        SetDraft(next, true);
    }

    void SetDraft(CamoSpec next, bool pushUndo)
    {
        if (next == _draft) return;
        if (pushUndo)
        {
            _undo.Add(_draft);
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        }
        _draft = next;
        PushToControls();
        Preview();
        Sync();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _draft = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        PushToControls();
        Preview();
        Sync();
    }

    void Randomize() => SetDraft(CamoEditor.Randomize(_camo, _rng), true);

    void ResetLook() => SetDraft(CamoSpec.Default, true);

    void CopyCode()
    {
        DisplayServer.ClipboardSet(CamoEditor.ShareCode(_draft));
        Shell.Toast(Strings.T("camo.toast.copied"));
    }

    void PasteCode()
    {
        var spec = CamoEditor.ParseShareCode(DisplayServer.ClipboardGet(), _camo, out var reason);
        if (spec is null) { Shell.Toast(Strings.F("camo.paste.failed", reason ?? ""), ToastKind.Error); return; }
        SetDraft(spec, true);
        Shell.Toast(Strings.T("camo.toast.pasted"));
    }

    void Apply()
    {
        if (_net == null || !Dirty || _awaitingSave) return;
        _awaitingSave = true;
        _net.SaveCamo(_slot, _draft);
        Sync();
    }

    /// <summary>Esc: one level up, but unsaved edits ask first (the same confirm as a slot change).</summary>
    public override void Back()
    {
        if (!_hasProfile || !Dirty) { base.Back(); return; }
        Shell.Push(Discard(() => Shell.Pop()));
    }

    /// <summary>1-5 pick a slot, Ctrl+Z undoes; the arrow keys turn the model when the preview has focus.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !_hasProfile) return false;
        if (k.CtrlPressed && k.Keycode == Key.Z) { Undo(); return true; }
        var slot = k.Keycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4, _ => -1 };
        if (slot < 0) return false;
        RequestSlot(slot);
        return true;
    }

    // ---- drawing ----

    /// <summary>Writes the draft into every control without firing their signals.</summary>
    void PushToControls()
    {
        _syncing = true;
        try
        {
            foreach (var r in _rows)
            {
                var v = r.P.Get(_draft);
                switch (r.Input)
                {
                    case HSlider s: s.SetValueNoSignal(v); r.Value.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case OptionButton ob: ob.Select(v); r.Value.Text = ChoiceNames(r.P.Key).ElementAtOrDefault(v) ?? ""; break;
                    case ColorPickerButton cp:
                        cp.Color = new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
                        r.Value.Text = Hex(v);
                        break;
                }
            }
        }
        finally { _syncing = false; }
    }

    void Preview()
    {
        if (_preview == null || _camo == null) return;
        _preview.Show(_draft, _camo);
    }

    /// <summary>Status line, buttons and the share code follow the draft and the server state.</summary>
    void Sync()
    {
        if (!_hasProfile)
        {
            Refresh();
            return;
        }
        _note.Text = _awaitingSave ? Strings.T("camo.saving") : Dirty ? Strings.T("camo.unsaved") : Strings.T("camo.saved");
        _note.ThemeTypeVariation = Dirty && !_awaitingSave ? "HudWarn" : "HudDim";
        _apply.Disabled = !Dirty || _awaitingSave || _net == null;
        _revert.Disabled = !Dirty || _awaitingSave;
        _undoBtn.Disabled = _undo.Count == 0;
        _code.Text = CamoEditor.ShareCode(_draft);
        Refresh();
    }

    void Refresh()
    {
        _body.Visible = _hasProfile;
        _empty.Visible = !_hasProfile;
        if (_hasProfile) return;
        _empty.Text = _net == null ? Strings.T("camo.offline") : Strings.T("camo.loading");
        _emptyBack.Visible = true;
    }

    protected override Control? FirstFocusTarget() => _hasProfile ? _slotTabs[_slot] : _emptyBack;
}

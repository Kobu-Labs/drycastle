using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The camouflage customiser (ticket #141; docs/standards/ui-ux.md sections 1 to 5). One look per loadout slot.
/// Top: the five slot tabs (what is being edited). Left: the live preview (drag or arrows to turn, wheel or +/- to zoom, Home to
/// reset, prone toggle); it frames the head while the face paint is edited. Right: four groups of controls built from the camo.json
/// parameters (character, suit, foliage, face paint), one-click presets for the suit and the face, and the look-wide actions
/// (randomize, reset, copy and paste a share code, base64 of the CamoSpec). Bottom: Back, the save state, Undo, Redo, Revert, Apply.
/// Every value is shown in a unit the player can read (percent, centimetres, a name), and a control that cannot change the look
/// (foliage controls while foliage is off, headgear on a body without a helmet) is disabled with the reason written above it
/// (<see cref="CamoEditor.Inert"/>). Apply sends the look for the open slot; the server answers with the profile. Esc goes one
/// level up; unsaved edits ask first, the same as the loadout editor. The draft lives on this screen until Apply.
/// </summary>
public partial class CustomizationScreen : MenuScreen
{
    /// <summary>How many undo steps are kept.</summary>
    const int UndoLimit = 200;
    /// <summary>Edits of one control closer together than this (ms) are one undo step: a slider drag or a held arrow key undoes in one go.</summary>
    const ulong UndoMergeMs = 700;
    /// <summary>Slider step for the 0..255 parameters: 51 stops, so the arrow keys cross the range in a few seconds.</summary>
    const int ByteStep = 5;
    const int CardWidth = 1400, PreviewWidth = 440, PreviewHeight = 520, LabelWidth = 220, ValueWidth = 120, ActionWidth = 160;
    const int RowHeight = 5 * ShooterTheme.Space, ControlHeight = 6 * ShooterTheme.Space;

    readonly Func<NetClient?> _client;
    readonly Func<GameData> _data;
    readonly Random _rng = new();
    NetClient? _net;
    CamoFile _camo = null!;
    IReadOnlyList<CamoParam> _params = Array.Empty<CamoParam>();
    int _slot = -1, _group; // slot -1 (given to the constructor) means the active slot, decided when the profile arrives
    CamoSpec _saved = CamoSpec.Default, _draft = CamoSpec.Default;
    readonly List<CamoSpec> _undo = new(), _redo = new();
    string _lastEditKey = "";
    ulong _lastEditMs;
    bool _hasProfile, _awaitingSave, _syncing, _previewStale, _pickerOpen;
    CamoPreview _preview = null!;

    readonly record struct Row(CamoParam P, Control Input, Label? Value, Control Line, Label Name);
    readonly List<Row> _rows = new();
    readonly record struct PresetButton(Button Button, CamoPreset Preset, bool Suit);
    readonly List<PresetButton> _presets = new();

    static readonly string[] GroupKeys = { "camo.group.character", "camo.group.suit", "camo.group.foliage", "camo.group.face" };
    readonly Button[] _slotTabs = new Button[Loadout.SlotCount];
    readonly Button[] _groupTabs = new Button[GroupKeys.Length];
    readonly Control[] _groupPanes = new Control[GroupKeys.Length];
    readonly Label[] _groupNotes = new Label[GroupKeys.Length];
    Label _level = null!, _note = null!, _empty = null!;
    Control _body = null!, _emptyBox = null!, _content = null!;
    ScrollContainer _fit = null!, _paneScroll = null!;
    Spinner _spinner = null!;
    Button _emptyBack = null!, _undoBtn = null!, _redoBtn = null!, _revert = null!, _apply = null!;
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

    /// <summary>The preview is rebuilt at most once a frame, however many slider steps arrived in it.</summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        FitToViewport();
        if (!_previewStale) return;
        _previewStale = false;
        _preview.Show(_draft, _camo);
    }

    /// <summary>The card is wider than a small viewport at a high UI scale: cap it to the screen and scroll the rest (as the loadout editor does).</summary>
    void FitToViewport()
    {
        if (_fit.GetParent() is not Control host) return;
        var avail = host.Size - new Vector2(ShooterTheme.Margin * 2, ShooterTheme.Margin * 2);
        var want = _content.GetCombinedMinimumSize();
        var size = new Vector2(Mathf.Min(want.X, avail.X), Mathf.Min(want.Y, avail.Y));
        if (_fit.CustomMinimumSize != size) _fit.CustomMinimumSize = size;
    }

    protected override Control Build()
    {
        var data = _data();
        _camo = data.Camo;
        _params = CamoEditor.Params(_camo);
        _group = Math.Clamp(int.TryParse(GameSettings.LoadUi("camo_group", "0"), out var g) ? g : 0, 0, GroupKeys.Length - 1);

        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(CardWidth, 0);
        _content = v;
        _fit = new ScrollContainer { FollowFocus = true, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        _fit.AddChild(v);

        var head = Ui.HBox();
        head.AddChild(Ui.Lbl(Strings.T("camo.title"), "HudDisplay"));
        head.AddChild(Ui.Fill());
        _level = Ui.Lbl("", "HudDim");
        _level.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        head.AddChild(_level);
        v.AddChild(head);

        // loading and offline state: no connection yet, or the server has not answered
        _empty = Ui.Lbl("", "HudDim", 0, true);
        _empty.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _spinner = new Spinner(ShooterTheme.Space * 5);
        _emptyBack = Ui.Btn(Strings.T("dialog.back"), Back, null, ActionWidth);
        _emptyBack.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        var emptyBox = Ui.VBox();
        emptyBox.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        var emptyRow = Ui.HBox();
        emptyRow.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        emptyRow.AddChild(_spinner);
        emptyRow.AddChild(_empty);
        emptyBox.AddChild(emptyRow);
        emptyBox.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        emptyBox.AddChild(_emptyBack);
        _emptyBox = emptyBox;
        v.AddChild(emptyBox);

        var body = Ui.VBox();
        _body = body;
        v.AddChild(body);

        // what is being edited: one look per loadout slot
        var slots = new ButtonGroup();
        var slotRow = Ui.HBox();
        for (var i = 0; i < Loadout.SlotCount; i++)
        {
            var slot = i;
            var b = TabButton(slots);
            b.Pressed += () => RequestSlot(slot);
            _slotTabs[i] = b;
            slotRow.AddChild(b);
        }
        body.AddChild(slotRow);

        var columns = Ui.HBox();
        columns.AddThemeConstantOverride("separation", ShooterTheme.Space * 3);
        columns.AddChild(BuildPreview());
        columns.AddChild(BuildEditor());
        body.AddChild(columns);

        var bottom = Ui.HBox();
        bottom.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, ActionWidth));
        _note = Ui.Lbl("", "HudDim");
        _note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _note.HorizontalAlignment = HorizontalAlignment.Right;
        _note.ClipText = true;
        bottom.AddChild(_note);
        _undoBtn = Ui.Btn(Strings.T("camo.undo"), Undo, null, ActionWidth - 40);
        _redoBtn = Ui.Btn(Strings.T("camo.redo"), Redo, null, ActionWidth - 40);
        _revert = Ui.Btn(Strings.T("camo.revert"), RequestRevert, "MenuDanger", ActionWidth - 20);
        _apply = Ui.Btn(Strings.T("camo.apply"), Apply, "MenuPrimary", ActionWidth + 40);
        _undoBtn.TooltipText = "Ctrl+Z";
        _redoBtn.TooltipText = "Ctrl+Y";
        bottom.AddChild(_undoBtn);
        bottom.AddChild(_redoBtn);
        bottom.AddChild(_revert);
        bottom.AddChild(_apply);
        body.AddChild(bottom);
        body.AddChild(Ui.Lbl(Strings.T("camo.hint"), "HudSmall"));
        return _fit;
    }

    Control BuildPreview()
    {
        var col = Ui.VBox();
        _preview = new CamoPreview(new Vector2(PreviewWidth, PreviewHeight));
        _preview.FocusTarget.TooltipText = Strings.T("camo.preview.tip");
        col.AddChild(_preview.Root);
        var row = Ui.HBox();
        _prone = new CheckButton { Text = Strings.T("camo.prone"), FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        _prone.Toggled += on => _preview.SetProne(on);
        row.AddChild(_prone);
        var hint = Ui.Lbl(Strings.T("camo.view.hint"), "HudSmall", 0, true);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(hint);
        row.CustomMinimumSize = new Vector2(PreviewWidth, 0);
        col.AddChild(row);
        return col;
    }

    Control BuildEditor()
    {
        var col = Ui.VBox();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

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

        // the panes share one scroll area that follows the keyboard focus, so a long group stays usable at 1280x720
        _paneScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var panel = new PanelContainer { ThemeTypeVariation = "HudPanel", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var stack = Ui.VBox();
        stack.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (var g = 0; g < GroupKeys.Length; g++)
        {
            var pane = Ui.VBox();
            pane.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
            pane.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            if ((CamoGroup)g == CamoGroup.Suit) pane.AddChild(PresetRow(true));
            if ((CamoGroup)g == CamoGroup.FacePaint) pane.AddChild(PresetRow(false));
            // why some of this group's controls are disabled, when they are
            var note = Ui.Lbl("", "HudWarn", 0, true);
            note.Visible = false;
            _groupNotes[g] = note;
            pane.AddChild(note);
            foreach (var p in _params.Where(p => p.Group == (CamoGroup)g)) pane.AddChild(ParamRow(p));
            _groupPanes[g] = pane;
            stack.AddChild(pane);
        }
        _paneScroll.AddChild(stack);
        panel.AddChild(_paneScroll);
        col.AddChild(panel);
        ShowGroup(_group);

        // actions on the whole look
        var actions = Ui.HBox();
        foreach (var (key, run) in new (string, Action)[] { ("camo.randomize", Randomize), ("camo.reset", ResetLook), ("camo.copy", CopyCode), ("camo.paste", PasteCode) })
        {
            var b = Ui.Btn(Strings.T(key), run);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            actions.AddChild(b);
        }
        col.AddChild(actions);
        return col;
    }

    static Button TabButton(ButtonGroup group) => new()
    {
        ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        CustomMinimumSize = new Vector2(0, ControlHeight),
    };

    /// <summary>
    /// One button per preset: a press applies it at once. The suit presets set colours, pattern and foliage; the face presets the paint.
    /// The button of the preset the draft matches stays pressed, so the player sees which preset the look still is.
    /// </summary>
    Control PresetRow(bool suit)
    {
        var box = Ui.VBox();
        var row = Ui.HBox();
        var label = Ui.Lbl(Strings.T(suit ? "camo.preset.suit" : "camo.preset.face"), "HudDim", LabelWidth);
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(label);
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", ShooterTheme.Space);
        flow.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
        foreach (var preset in suit ? _camo.SuitPresets : _camo.FacePresets)
        {
            var b = new Button
            {
                Text = preset.Name, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, ThemeTypeVariation = "MenuTab",
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, CustomMinimumSize = new Vector2(ActionWidth, ControlHeight),
            };
            b.Pressed += () =>
            {
                var next = suit ? CamoEditor.WithSuitPreset(_draft, preset.Spec) : CamoEditor.WithFacePreset(_draft, preset.Spec);
                if (next != _draft) { SetDraft(next, true); Shell.Toast(Strings.F("camo.toast.preset", preset.Name)); }
                SyncPresets(); // the press toggled the button: put it back to what the draft says
            };
            _presets.Add(new PresetButton(b, preset, suit));
            flow.AddChild(b);
        }
        row.AddChild(flow);
        box.AddChild(row);
        if (suit) box.AddChild(Ui.Lbl(Strings.T("camo.preset.suit.note"), "HudSmall"));
        box.AddChild(Ui.Gap(ShooterTheme.Space));
        return box;
    }

    /// <summary>One camo.json parameter: its label, the control that edits it, and (for sliders and colours) the value in a readable unit.</summary>
    Control ParamRow(CamoParam p)
    {
        var row = Ui.HBox();
        row.CustomMinimumSize = new Vector2(0, RowHeight);
        var name = Ui.Lbl(Strings.T("camo.param." + p.Key), "HudDim", LabelWidth);
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(name);
        Label? value = null;
        if (p.Kind != CamoParamKind.Choice)
        {
            value = Ui.Lbl("", null, ValueWidth);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }
        Control input = p.Kind switch
        {
            CamoParamKind.Choice => ChoiceInput(p),
            CamoParamKind.Colour => ColourInput(p),
            _ => SliderInput(p),
        };
        input.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        input.TooltipText = Tip(p.Key);
        row.AddChild(input);
        if (value != null) row.AddChild(value);
        _rows.Add(new Row(p, input, value, row, name));
        return row;
    }

    /// <summary>What a control does, for the controls whose name does not say it. "" when there is nothing to add.</summary>
    static string Tip(string key)
    {
        var k = key is "tuft_head" or "tuft_chest" or "tuft_arms" or "tuft_legs" ? "camo.tip.tuft_density" : "camo.tip." + key;
        var text = Strings.T(k);
        return text == k ? "" : text;
    }

    Control SliderInput(CamoParam p)
    {
        var whole = p.Max < 255; // a count (the foliage thickness): one stop per value, with ticks
        var s = new HSlider
        {
            MinValue = p.Min, MaxValue = p.Max, Step = whole ? 1 : ByteStep, FocusMode = Control.FocusModeEnum.All,
            CustomMinimumSize = new Vector2(0, RowHeight), MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        if (whole) { s.TickCount = p.Max - p.Min + 1; s.TicksOnBorders = true; }
        s.ValueChanged += v =>
        {
            if (_syncing) return;
            Edit(p, (int)v);
        };
        return s;
    }

    Control ChoiceInput(CamoParam p)
    {
        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, ControlHeight), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        foreach (var n in ChoiceNames(p.Key)) ob.AddItem(n);
        ob.ItemSelected += idx =>
        {
            if (_syncing) return;
            Edit(p, (int)idx);
        };
        return ob;
    }

    Control ColourInput(CamoParam p)
    {
        // no alpha: a suit or paint colour is opaque, and the look does not store one
        var cp = new ColorPickerButton { EditAlpha = false, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, RowHeight), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        cp.Pressed += () => _pickerOpen = true;
        cp.PopupClosed += () => _pickerOpen = false;
        cp.ColorChanged += c =>
        {
            if (_syncing) return;
            Edit(p, ((int)Math.Round(c.R * 255) << 16) | ((int)Math.Round(c.G * 255) << 8) | (int)Math.Round(c.B * 255));
        };
        return cp;
    }

    IReadOnlyList<string> ChoiceNames(string key) => key switch
    {
        "suit_pattern" => _camo.SuitPatterns.Select(n => Named("camo.suit_pattern.", n)).ToList(),
        "face_pattern" => _camo.FacePatterns.Select(n => Named("camo.face_pattern.", n)).ToList(),
        "body" => _camo.Bodies.Select(b => b.Name).ToList(),
        "skin_tone" => _camo.SkinTones.Select(s => s.Name).ToList(),
        "headgear" => _camo.Headgear.Select(n => Named("camo.headgear.", n)).ToList(),
        _ => _camo.Vegetation.Select(n => Named("camo.vegetation.", n)).ToList(),
    };

    /// <summary>The display name of a camo.json list entry: its translation, or (an entry added to the data with no string yet) the id with a capital.</summary>
    static string Named(string prefix, string id)
    {
        var text = Strings.T(prefix + id);
        return text != prefix + id ? text : id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');
    }

    /// <summary>A slider or colour value the way a player reads it: a share in percent, a length in centimetres, a name; never the stored 0..255.</summary>
    string ValueText(CamoParam p, int v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        static int Pct(int value, int of) => (int)Math.Round(value * 100.0 / of);
        return p.Key switch
        {
            _ when p.Kind == CamoParamKind.Colour => "#" + v.ToString("x6", inv),
            "tuft_layers" => v == 0 ? Strings.T("camo.value.off") : v <= 3 ? Strings.T("camo.thickness." + v.ToString(inv)) : Strings.F("camo.thickness.n", v),
            "tuft_size" => Strings.F("camo.value.cm", Math.Round(_camo.TuftSizeOf((byte)v) * 100).ToString(inv)),
            // the stored value is pattern cells per metre (more detail to the right), shown as the size of one patch
            "pattern_scale" => Strings.F("camo.value.cm", Math.Round(100.0 / Math.Max(0.01, _camo.PatternScaleOf((byte)v))).ToString(inv)),
            "contrast" => Strings.F("camo.value.percent", Pct(v, 255) * 2), // 100% draws the colours as picked (the shader's midpoint)
            _ => Strings.F("camo.value.percent", Pct(v, Math.Max(1, p.Max))),
        };
    }

    // ---- server data ----

    void OnProfile(ProfileMsg p)
    {
        var first = !_hasProfile;
        _hasProfile = true;
        if (_slot < 0) _slot = LoadoutEditor.ClampTab(p.ActiveSlot);
        var before = _saved;
        _saved = ServerCopy(p, _slot);
        if (first || _draft == before) { _draft = _saved; ClearHistory(); }
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
        ClearHistory();
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
        if (_paneScroll != null) _paneScroll.ScrollVertical = 0;
        // the face paint cannot be judged on the whole figure: the camera goes to the head for that group
        _preview?.Frame((CamoGroup)g == CamoGroup.FacePaint ? CamoPreview.Framing.Head : CamoPreview.Framing.Body);
        GameSettings.SaveUi("camo_group", g.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>One control moved: the draft takes the value. Quick successive moves of the same control are one undo step.</summary>
    void Edit(CamoParam p, int value)
    {
        var now = Time.GetTicksMsec();
        var merge = p.Key == _lastEditKey && now - _lastEditMs < UndoMergeMs;
        SetDraft(p.Set(_draft, value), !merge);
        _lastEditKey = p.Key;
        _lastEditMs = now;
    }

    void SetDraft(CamoSpec next, bool pushUndo)
    {
        _lastEditKey = "";
        if (next == _draft) return;
        if (pushUndo)
        {
            _undo.Add(_draft);
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        }
        _redo.Clear();
        Show(next);
    }

    void Undo() => Step(_undo, _redo);

    void Redo() => Step(_redo, _undo);

    /// <summary>Moves the draft one step along the history: the newest entry of <paramref name="from"/> becomes the draft, the draft goes on <paramref name="to"/>.</summary>
    void Step(List<CamoSpec> from, List<CamoSpec> to)
    {
        if (from.Count == 0 || _awaitingSave) return;
        to.Add(_draft);
        var next = from[^1];
        from.RemoveAt(from.Count - 1);
        _lastEditKey = "";
        Show(next);
    }

    void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastEditKey = "";
    }

    /// <summary>Makes <paramref name="next"/> the draft and brings the controls, the preview and the status line up to date.</summary>
    void Show(CamoSpec next)
    {
        _draft = next;
        PushToControls();
        Preview();
        Sync();
    }

    void Randomize()
    {
        SetDraft(CamoEditor.Randomize(_camo, _rng), true);
        Shell.Toast(Strings.T("camo.toast.random"));
    }

    void ResetLook()
    {
        if (_draft == CamoSpec.Default) return;
        SetDraft(CamoSpec.Default, true);
        Shell.Toast(Strings.T("camo.toast.reset"));
    }

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
        if (!_net.SaveCamo(_slot, _draft)) { Shell.Toast(Strings.T("net.not_sent"), ToastKind.Error); return; }
        _awaitingSave = true;
        Sync();
    }

    /// <summary>Esc: one level up, but unsaved edits ask first (the same confirm as a slot change).</summary>
    public override void Back()
    {
        if (!_hasProfile || !Dirty) { base.Back(); return; }
        Shell.Push(Discard(() => Shell.Pop()));
    }

    /// <summary>1-5 pick a slot, Ctrl+Z undoes, Ctrl+Y or Ctrl+Shift+Z redoes. Nothing is taken while a colour picker is open (its hex field needs the digits).</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !_hasProfile || _pickerOpen) return false;
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return false;
        if (k.CtrlPressed && (k.Keycode == Key.Y || (k.Keycode == Key.Z && k.ShiftPressed))) { Redo(); return true; }
        if (k.CtrlPressed && k.Keycode == Key.Z) { Undo(); return true; }
        if (k.CtrlPressed || k.AltPressed) return false;
        var slot = k.Keycode switch { Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4, _ => -1 };
        if (slot < 0) return false;
        RequestSlot(slot);
        return true;
    }

    // ---- drawing ----

    /// <summary>Writes the draft into every control without firing their signals, and disables the controls that cannot change this look.</summary>
    void PushToControls()
    {
        _syncing = true;
        try
        {
            var notes = new string?[GroupKeys.Length];
            foreach (var r in _rows)
            {
                var v = r.P.Get(_draft);
                if (r.Value != null) r.Value.Text = ValueText(r.P, v);
                var inert = CamoEditor.Inert(r.P.Key, _draft, _camo);
                if (inert != null) notes[(int)r.P.Group] ??= Strings.T("camo.inert." + inert);
                // an inert control is dimmed, cannot be changed and is skipped by the keyboard; the group's note says why
                r.Line.Modulate = new Color(1, 1, 1, inert != null ? ShooterTheme.DisabledAlpha : 1f);
                r.Input.FocusMode = inert != null ? FocusModeEnum.None : FocusModeEnum.All;
                switch (r.Input)
                {
                    case HSlider s: s.SetValueNoSignal(v); s.Editable = inert == null; break;
                    case OptionButton ob: ob.Select(v); ob.Disabled = inert != null; break;
                    case ColorPickerButton cp:
                        cp.Color = new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
                        cp.Disabled = inert != null;
                        break;
                }
            }
            for (var g = 0; g < notes.Length; g++)
            {
                _groupNotes[g].Text = notes[g] ?? "";
                _groupNotes[g].Visible = notes[g] != null;
            }
            SyncPresets();
        }
        finally { _syncing = false; }
    }

    /// <summary>The preset buttons: pressed while the draft's suit (or face paint) is exactly that preset's.</summary>
    void SyncPresets()
    {
        foreach (var p in _presets)
        {
            var applied = p.Suit ? CamoEditor.WithSuitPreset(_draft, p.Preset.Spec) : CamoEditor.WithFacePreset(_draft, p.Preset.Spec);
            p.Button.SetPressedNoSignal(applied == _draft);
        }
    }

    /// <summary>Asks for the preview to be redrawn; <see cref="_Process"/> does it once for the frame.</summary>
    void Preview() => _previewStale = true;

    /// <summary>Status line and buttons follow the draft and the server state.</summary>
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
        _undoBtn.Disabled = _undo.Count == 0 || _awaitingSave;
        _redoBtn.Disabled = _redo.Count == 0 || _awaitingSave;
        Refresh();
    }

    void Refresh()
    {
        _body.Visible = _hasProfile;
        _emptyBox.Visible = !_hasProfile;
        if (_hasProfile) return;
        _empty.Text = _net == null ? Strings.T("camo.offline") : Strings.T("camo.loading");
        _spinner.Visible = _net != null;
    }

    protected override Control? FirstFocusTarget() => _hasProfile ? _slotTabs[_slot] : _emptyBack;
}

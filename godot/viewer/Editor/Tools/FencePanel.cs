using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>The Fence page of the sidebar, with the header (target, unsaved mark, scope) and the footer (Apply, Revert) the Fence tool shows around it.</summary>
public sealed class FencePanel
{
    static readonly Color Grey = EditorTheme.Dim;

    readonly IEditorHost _host;
    readonly FenceTool _fence;
    bool _syncing;
    string _gapRowsKey = "", _sideRowsKey = "";

    OptionButton _modeOpt = null!, _typeOpt = null!, _scopeOpt = null!;
    Label _hdrLabel = null!, _dirtyLabel = null!, _footLabel = null!, _fenceInfo = null!, _gateHint = null!, _mixedLabel = null!;
    Button _heightReset = null!, _thickReset = null!, _colourReset = null!;
    SpinBox _heightSpin = null!, _thickSpin = null!;
    ColorPickerButton _colourBtn = null!;
    CheckBox _houseColour = null!, _sideFenced = null!;
    VBoxContainer _sidesBox = null!;
    Button _btnPickSide = null!;
    Label _sideScopeLabel = null!, _sideHint = null!;
    HBoxContainer _sideScopeRow = null!;
    VBoxContainer _gapsBox = null!;
    Button _btnAddGate = null!, _btnApply = null!, _btnRevert = null!, _btnRevertAll = null!;
    HBoxContainer _scopeRow = null!;

    public FencePanel(IEditorHost host, FenceTool fence)
    {
        _host = host;
        _fence = fence;
    }

    FenceGates Gates => _fence.Gates;

    // texts for the smoke
    public string HeaderText => _hdrLabel.Text;
    public bool DirtyShown => _dirtyLabel.Visible;
    public string FooterText => _footLabel.Text;
    public string FenceInfo => _fenceInfo.Text;
    public bool ScopeRowShown => _scopeRow.Visible;
    public bool GateButtonEnabled => !_btnAddGate.Disabled;
    public string[] FenceTypeItems => Enumerable.Range(1, _typeOpt.ItemCount - 1).Select(i => _typeOpt.GetItemText(i)).ToArray();
    public string[] FenceModeItems => Enumerable.Range(0, _modeOpt.ItemCount).Select(i => _modeOpt.GetItemText(i)).ToArray();
    public bool FieldsEnabled => !_typeOpt.Disabled;
    public bool HouseColourShown => _houseColour.Visible;
    public bool HouseColourOn => _houseColour.ButtonPressed;
    public int SideRowCount => _sidesBox.GetChildCount();
    public string SideScopeText => _sideScopeRow.Visible ? _sideScopeLabel.Text : "";
    public int GapRowCount => _gapsBox.GetChildCount();

    // ---- build ----

    public void BuildHeader(VBoxContainer outer)
    {
        _hdrLabel = new Label { Text = "Nothing selected", MouseFilter = Control.MouseFilterEnum.Ignore };
        _hdrLabel.ThemeTypeVariation = "Title";
        outer.AddChild(_hdrLabel);
        _dirtyLabel = new Label { Text = "* unsaved changes", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _dirtyLabel.ThemeTypeVariation = "Warn";
        outer.AddChild(_dirtyLabel);
        _scopeRow = new HBoxContainer { Visible = false };
        _scopeOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        _scopeOpt.AddItem("whole group");
        _scopeOpt.AddItem("this parcel");
        _scopeOpt.ItemSelected += i => { if (!_syncing) _fence.SetGroupScope(i == 0); };
        _scopeRow.AddChild(new Label { Text = "Edit: " });
        _scopeRow.AddChild(_scopeOpt);
        outer.AddChild(_scopeRow);
        _mixedLabel = Ui.Note();
        outer.AddChild(_mixedLabel);
    }

    public void BuildFooter(VBoxContainer outer)
    {
        _footLabel = Ui.Note("no unsaved changes");
        outer.AddChild(_footLabel);
        var r = new HBoxContainer();
        _btnApply = Ui.Btn("Apply (Enter)", () => _fence.Apply());
        _btnRevert = Ui.Btn("Revert", _fence.Revert);
        _btnRevertAll = Ui.Btn("Revert all", _fence.RevertAll);
        foreach (var b in new[] { _btnApply, _btnRevert, _btnRevertAll }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; r.AddChild(b); }
        outer.AddChild(r);
    }

    public void BuildPage(VBoxContainer body)
    {
        _modeOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var m in EditSchema.FenceModes) _modeOpt.AddItem(m);
        _modeOpt.ItemSelected += i => { if (!_syncing) _fence.SetMode(EditSchema.FenceModes[(int)i]); };
        var modeRow = Ui.Row("mode", _modeOpt);
        modeRow.AddChild(Ui.Btn("Reset", () => _fence.Reset()));
        body.AddChild(modeRow);
        _fenceInfo = Ui.Note();
        body.AddChild(_fenceInfo);

        // while a side is selected, the fields below edit that side
        _sideScopeRow = new HBoxContainer { Visible = false };
        _sideScopeLabel = new Label { ThemeTypeVariation = "Warn", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _sideScopeRow.AddChild(_sideScopeLabel);
        var whole = Ui.FocusBtn("Whole fence", () => _fence.Sides.Select(-1));
        whole.TooltipText = "Edit the whole fence again (Esc)";
        _sideScopeRow.AddChild(whole);
        body.AddChild(_sideScopeRow);
        _sideFenced = new CheckBox { Text = "Fence on this side", FocusMode = Control.FocusModeEnum.All, Visible = false, TooltipText = "Untick to leave this side without a fence" };
        _sideFenced.Toggled += on => { if (!_syncing) _fence.SetSideFenced(on); _sideFenced.ReleaseFocus(); };
        body.AddChild(_sideFenced);

        _typeOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        _typeOpt.AddItem("(default)");
        foreach (var t in EditSchema.FenceTypes)
        {
            if (FenceIcons.For(t.Name) is { } icon) _typeOpt.AddIconItem(icon, t.Name);
            else _typeOpt.AddItem(t.Name);
        }
        _typeOpt.ItemSelected += i => { if (!_syncing) _fence.SetType(i == 0 ? null : EditSchema.FenceTypes[(int)i - 1].Name); };
        body.AddChild(Ui.Row("type", _typeOpt));

        _heightSpin = Ui.Spin(0.1, 10, 0.05, " m");
        _heightReset = Ui.ResetBtn(() => _fence.SetHeight(null));
        _heightSpin.ValueChanged += v => { if (!_syncing) _fence.SetHeight(v); };
        var hr = Ui.Row("height", _heightSpin); hr.AddChild(_heightReset); body.AddChild(hr);

        _thickSpin = Ui.Spin(0.01, 2, 0.01, " m");
        _thickReset = Ui.ResetBtn(() => _fence.SetThickness(null));
        _thickSpin.ValueChanged += v => { if (!_syncing) _fence.SetThickness(v); };
        var tr = Ui.Row("thickness", _thickSpin); tr.AddChild(_thickReset); body.AddChild(tr);

        _colourBtn = new ColorPickerButton { CustomMinimumSize = new Vector2(80, 28), FocusMode = Control.FocusModeEnum.None, EditAlpha = false };
        _colourReset = Ui.ResetBtn(() => _fence.SetColour(null));
        _colourBtn.ColorChanged += c => { if (!_syncing) _fence.SetColour("#" + c.ToHtml(false)); };
        var cr = Ui.Row("colour", _colourBtn); cr.AddChild(_colourReset); body.AddChild(cr);

        _houseColour = new CheckBox
        {
            Text = "Use the house colour", FocusMode = Control.FocusModeEnum.All, Visible = false,
            TooltipText = "Paint the fence the wall colour of this parcel's house (its largest building; a group: the group's). No house: the colour above.",
        };
        _houseColour.Toggled += on => { if (!_syncing) _fence.SetHouseColour(on); _houseColour.ReleaseFocus(); };
        body.AddChild(_houseColour);

        var sh = new Label { Text = "Sides" };
        sh.ThemeTypeVariation = "Header";
        body.AddChild(sh);
        body.AddChild(Ui.Note("A side runs from corner to corner. Pick one to give it its own type, colour or height, or no fence."));
        _sidesBox = new VBoxContainer();
        body.AddChild(_sidesBox);
        _btnPickSide = Ui.Btn("Pick side", () => { if (_fence.Sides.Armed) _fence.Sides.Cancel(); else _fence.Sides.Arm(); });
        _btnPickSide.TooltipText = "Then click near a side of the fence in the view";
        body.AddChild(_btnPickSide);
        _sideHint = Ui.Note();
        body.AddChild(_sideHint);

        var gh = new Label { Text = "Gates" };
        gh.ThemeTypeVariation = "Header";
        body.AddChild(gh);
        _gapsBox = new VBoxContainer();
        body.AddChild(_gapsBox);
        _btnAddGate = Ui.Btn("Add gate", () => { if (Gates.Armed) Gates.Cancel(); else { if (_fence.Current.EffectiveMode != "custom") _fence.SetMode("custom"); Gates.Arm(); } });
        body.AddChild(_btnAddGate);
        _gateHint = Ui.Note();
        body.AddChild(_gateHint);
    }

    // ---- refresh ----

    /// <summary>Bring every control in line with the target and its draft.</summary>
    public void Refresh()
    {
        _syncing = true;
        try { SyncControls(); }
        finally { _syncing = false; }
    }

    void SyncControls()
    {
        var t = _fence.Target;
        var has = t != null;
        _hdrLabel.Text = t?.Header ?? "Nothing selected";
        _dirtyLabel.Visible = _fence.TargetDirty;
        var single = _host.Selection.Count == 1 && has && _host.Map.Parcels.TryGetValue(_host.Selection.First(), out var sp) && sp.Group != null;
        _scopeRow.Visible = single;
        _scopeOpt.Select(_fence.GroupScope ? 0 : 1);
        _mixedLabel.Text = has && EditModel.Mixed(_host.Edits.Disk(), t!) ? $"(the selected parcels differ on disk; showing {t!.Parcels[0]})" : "";
        var pending = _fence.PendingCount;
        _footLabel.Text = pending == 0 ? "no unsaved changes" : $"{pending} unsaved target{(pending == 1 ? "" : "s")}: {_fence.PendingNames}";
        _btnApply.Disabled = pending == 0;
        _btnRevert.Disabled = !_fence.TargetDirty;
        _btnRevertAll.Visible = pending > 1;

        var d = _fence.Current;
        var mode = d.EffectiveMode;
        var custom = mode == "custom";
        var eff = d.EffectiveCustom(_fence.InheritedGroupFence());
        _modeOpt.Disabled = !has;
        _modeOpt.Select(Array.IndexOf(EditSchema.FenceModes, mode));
        var defs = _host.Map.EditDefaults;
        _fenceInfo.Text = !has ? "" : mode switch
        {
            "none" => "none: no fence" + (t!.IsGroup ? " around the group." : "."),
            "auto" => defs == null ? "auto: the rules decide." : $"auto: the rules decide (kinds {string.Join("/", defs.FenceKinds)}, {defs.FenceProbability:P0} of them, height {defs.FenceHeight:0.##} m).",
            _ => t!.IsGroup ? "custom: always fenced around the group's outer edge; settings apply to the whole group." : "custom: always fenced on the boundary.",
        };
        var group = _fence.InheritedGroupFence();
        var side = _fence.Sides.Current;
        var look = side == null ? eff : d.EffectiveSide(side, group);
        var (ownType, ownHeight, ownThick, ownColour) = side == null ? (d.Type, d.Height, d.Thickness, d.Colour) : (side.Type, side.Height, side.Thickness, side.Colour);
        var editable = has && side?.Unfenced != true;
        _sideScopeRow.Visible = side != null;
        if (side != null) _sideScopeLabel.Text = $"Editing {SideName(_fence.Current.Sides.IndexOf(side), side)}";
        _sideFenced.Visible = side != null;
        _sideFenced.SetPressedNoSignal(side?.Unfenced != true);
        _typeOpt.SetItemText(0, side == null ? $"(default: {eff.type})" : $"(the fence's: {eff.type})");
        _typeOpt.SetItemIcon(0, FenceIcons.For(eff.type));
        _typeOpt.Select(ownType == null ? 0 : 1 + Array.IndexOf(EditSchema.FenceTypes.Select(x => x.Name).ToArray(), ownType));
        _typeOpt.Disabled = !editable;
        SyncOptional(_heightReset, _heightSpin, ownHeight, look.height, editable);
        SyncOptional(_thickReset, _thickSpin, ownThick, look.thickness, editable);
        var house = side == null ? d.EffectiveHouseColour(group) : d.EffectiveSideHouseColour(side, group);
        _houseColour.Visible = editable && EditSchema.HouseColourTypes.Contains(look.type);
        _houseColour.SetPressedNoSignal(house);
        _colourReset.Visible = ownColour != null && !house && editable;
        _colourBtn.Color = Color.FromHtml(ownColour ?? look.colour);
        _colourBtn.Disabled = !editable || house;
        _colourBtn.Modulate = ownColour == null || house ? Grey : Colors.White;
        _colourBtn.TooltipText = house ? "From the house's walls (untick \"Use the house colour\" to pick one)" : "";
        SyncSideRows(d, custom);
        _btnPickSide.Disabled = !has || !Gates.Allowed;
        _btnPickSide.Text = _fence.Sides.Armed ? "Click a side... (Esc cancels)" : "Pick side";
        _sideHint.Text = !has ? "" : !Gates.Allowed ? "sides: select one parcel or one property group" : _host.Map.OriginEpsg5514 == null ? "sides need a map exported with origin_epsg5514 (0.4.0+)" : "";
        SyncGapRows(d, custom);
        _btnAddGate.Disabled = !has || !Gates.Allowed;
        _btnAddGate.Text = Gates.Armed ? "Click the fence... (Esc cancels)" : "Add gate";
        _gateHint.Text = !has ? "" : !Gates.Allowed ? "gates: select one parcel or one property group" : _host.Map.OriginEpsg5514 == null ? "gates need a map exported with origin_epsg5514 (0.4.0+)" : "";
    }

    static void SyncOptional(Button reset, SpinBox spin, double? value, double effective, bool enabled)
    {
        reset.Visible = value != null;
        spin.SetValueNoSignal(value ?? effective);
        spin.Editable = enabled;
        spin.Modulate = value == null ? Grey : Colors.White;
    }

    string SideName(int i, SideDraft s)
    {
        var at = _fence.Sides.Describe(s);
        var name = at is { } a ? $"side {a.number} ({a.length:0.#} m)" : $"side entry {i + 1} (not on the boundary)";
        return s.Unfenced ? $"{name}: no fence" : $"{name}: {s.Type ?? "fence's type"}";
    }

    void SyncSideRows(FenceDraft d, bool custom)
    {
        var key = $"{_fence.Target?.Key}|{d.Sides.Count}";
        if (key != _sideRowsKey)
        {
            _sideRowsKey = key;
            foreach (var c in _sidesBox.GetChildren()) { _sidesBox.RemoveChild(c); c.QueueFree(); }
            for (var i = 0; i < d.Sides.Count; i++)
            {
                var idx = i;
                var row = new HBoxContainer { Name = $"side{i}" };
                var pick = new Button { FocusMode = Control.FocusModeEnum.None, ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Left, TooltipText = "Edit this side (the fields above)" };
                pick.Pressed += () => _fence.Sides.Select(_fence.Sides.Selected == idx ? -1 : idx);
                var del = Ui.Btn("delete", () => _fence.Sides.Remove(idx));
                del.TooltipText = "The side looks like the rest of the fence again";
                row.AddChild(pick); row.AddChild(del);
                _sidesBox.AddChild(row);
            }
        }
        for (var i = 0; i < d.Sides.Count && i < _sidesBox.GetChildCount(); i++)
        {
            var row = (HBoxContainer)_sidesBox.GetChild(i);
            var pick = (Button)row.GetChild(0);
            pick.Text = SideName(i, d.Sides[i]);
            pick.Icon = d.Sides[i].Unfenced ? null : FenceIcons.For(d.EffectiveSide(d.Sides[i], _fence.InheritedGroupFence()).type);
            pick.SetPressedNoSignal(i == _fence.Sides.Selected);
            pick.Disabled = !custom;
            ((Button)row.GetChild(1)).Disabled = !custom;
        }
    }

    void SyncGapRows(FenceDraft d, bool custom)
    {
        var key = $"{_fence.Target?.Key}|{d.Gaps.Count}";
        if (key != _gapRowsKey)
        {
            _gapRowsKey = key;
            foreach (var c in _gapsBox.GetChildren()) { _gapsBox.RemoveChild(c); c.QueueFree(); }
            for (var i = 0; i < d.Gaps.Count; i++)
            {
                var idx = i;
                var row = new HBoxContainer { Name = $"gap{i}" };
                var pick = new Button { Text = $"gate {i + 1}", FocusMode = Control.FocusModeEnum.None, ToggleMode = true };
                pick.Pressed += () => Gates.Select(idx);
                var w = Ui.Spin(EditSchema.GateMin, 30, EditSchema.GateSnap, " m");
                w.ValueChanged += v => { if (!_syncing) Gates.SetWidth(idx, v); };
                var del = Ui.Btn("delete", () => Gates.Remove(idx));
                row.AddChild(pick); row.AddChild(w); row.AddChild(del);
                _gapsBox.AddChild(row);
            }
        }
        for (var i = 0; i < d.Gaps.Count && i < _gapsBox.GetChildCount(); i++)
        {
            var row = (HBoxContainer)_gapsBox.GetChild(i);
            var pick = (Button)row.GetChild(0);
            pick.SetPressedNoSignal(i == Gates.Selected);
            pick.Disabled = !custom;
            ((SpinBox)row.GetChild(1)).SetValueNoSignal(d.Gaps[i].Width);
            ((SpinBox)row.GetChild(1)).Editable = custom;
            ((Button)row.GetChild(2)).Disabled = !custom;
        }
    }
}

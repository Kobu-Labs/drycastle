using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Suchohrad.MapLoader;

/// <summary>
/// The Fence and Trees sections of the editor panel (Editor 6): the target (a parcel, several parcels or a
/// property group), the pending state per target, the gate tool and Apply. Every control calls one of the
/// public methods below, so the headless smoke drives the same code without mouse events.
/// </summary>
public partial class EditorMode
{
    // ---- target and pending state ----

    EditTarget? _target;
    bool _scopeGroup = true;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    bool _syncing;
    int _selectedGate = -1;

    public EditTarget? Target => _target;
    public int PendingCount => _pending.Count;
    public bool TargetDirty => _target != null && _pending.ContainsKey(_target.Key);
    public bool GroupScope => _scopeGroup;
    public int SelectedGate => _selectedGate;
    /// <summary>Number of times edits.json was written by this editor (group operations and Apply).</summary>
    public int SaveCount { get; private set; }

    SortedDictionary<string, object?> Disk()
    {
        if (_disk != null) return _disk;
        try { _disk = EditsFile.Load(EditsPath); } catch (Exception) { _disk = EditsFile.Load("/nonexistent"); }
        return _disk;
    }

    /// <summary>The target for the current selection (see the panel header), or null when nothing is selected.</summary>
    EditTarget? ComputeTarget()
    {
        var sel = Selection.Where(Map.Parcels.ContainsKey).ToList();
        if (sel.Count == 0) return null;
        var groups = sel.Select(Group).Distinct().ToList();
        if (groups.Count == 1 && groups[0] != null)
        {
            var gid = groups[0]!;
            var members = Map.Parcels.Values.Where(p => p.Group == gid).Select(p => p.Id).ToList();
            if (members.Count == sel.Count || (sel.Count == 1 && _scopeGroup)) return EditTarget.ForGroup(gid, members);
        }
        return EditTarget.ForParcels(sel);
    }

    void UpdateTarget()
    {
        var t = ComputeTarget();
        if (t?.Key != _target?.Key)
        {
            if (GateArmed) CancelGate();
            _selectedGate = -1;
        }
        _target = t;
    }

    Pending PendingFor()
    {
        if (!_pending.TryGetValue(_target!.Key, out var p)) _pending[_target.Key] = p = new Pending { Target = _target };
        return p;
    }

    /// <summary>The fence as the panel shows it: the pending draft, else what is on disk.</summary>
    public FenceDraft CurrentFence
        => _target == null ? new FenceDraft()
            : _pending.GetValueOrDefault(_target.Key)?.Fence ?? EditModel.FenceFromDisk(Disk(), _target);

    public TreesDraft CurrentTrees
        => _target == null ? new TreesDraft()
            : _pending.GetValueOrDefault(_target.Key)?.Trees ?? EditModel.TreesFromDisk(Disk(), _target);

    FenceDraft? InheritedGroupFence()
        => _target == null || _target.IsGroup ? null : EditModel.GroupFenceOf(Disk(), _target.Parcels[0]);

    void AfterEdit() { _panelDirty = true; _outlinesDirty = true; }

    bool EditFence(Action<FenceDraft> change, bool needCustom = false)
    {
        if (_target == null) { Say("select a parcel first", true); return false; }
        if (needCustom && CurrentFence.EffectiveMode != "custom") { Say("this needs fence mode custom", true); return false; }
        var p = PendingFor();
        var d = p.Fence ??= EditModel.FenceFromDisk(Disk(), _target);
        change(d);
        AfterEdit();
        return true;
    }

    bool EditTrees(Action<TreesDraft> change)
    {
        if (_target == null) { Say("select a parcel first", true); return false; }
        var p = PendingFor();
        var d = p.Trees ?? EditModel.TreesFromDisk(Disk(), _target);
        p.Trees = d;
        change(d);
        AfterEdit();
        return true;
    }

    // ---- fence edits ----

    /// <summary>Remove the fence entry: the rules decide again (a group member inherits the group's fence).</summary>
    public bool ResetFence()
    {
        _selectedGate = -1;
        return EditFence(d => d.Reset());
    }

    public bool SetFenceMode(string mode)
    {
        if (!EditSchema.FenceModes.Contains(mode)) { Say($"unknown fence mode {mode}", true); return false; }
        _selectedGate = -1;
        return EditFence(d => d.SetMode(mode));
    }

    /// <summary>Set the type (null = default); only in mode custom.</summary>
    public bool SetFenceType(string? type)
    {
        if (type != null && !EditSchema.FenceTypeNames.Contains(type)) { Say($"unknown fence type {type}", true); return false; }
        return EditFence(d => d.Type = type, true);
    }

    public bool SetFenceHeight(double? metres) => EditFence(d => d.Height = metres is { } v ? Math.Max(0.05, v) : null, true);
    public bool SetFenceThickness(double? metres) => EditFence(d => d.Thickness = metres is { } v ? Math.Max(0.01, v) : null, true);

    public bool SetFenceColour(string? colour)
    {
        if (colour != null && !EditSchema.ColourRx.IsMatch(colour)) { Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        return EditFence(d => d.Colour = colour?.ToLowerInvariant(), true);
    }

    public bool RemoveGap(int index)
    {
        if (index < 0 || index >= CurrentFence.Gaps.Count) return false;
        var ok = EditFence(d => d.Gaps.RemoveAt(index), true);
        if (ok) _selectedGate = -1;
        return ok;
    }

    public bool SetGapWidth(int index, double width)
    {
        if (index < 0 || index >= CurrentFence.Gaps.Count) return false;
        return EditFence(d => d.Gaps[index] = d.Gaps[index] with { Width = Math.Max(EditSchema.GateMin, width) }, true);
    }

    public void SelectGate(int index)
    {
        _selectedGate = index >= 0 && index < CurrentFence.Gaps.Count ? index : -1;
        _panelDirty = true;
    }

    // ---- trees edits ----

    string? FirstKind => _target == null ? null : Map.Parcels.GetValueOrDefault(_target.Parcels[0])?.Kind;

    /// <summary>Trees per 1000 m2 the rules give the first targeted parcel.</summary>
    public double RulesDensity
        => FirstKind is { } k && Map.EditDefaults is { } ed ? ed.TreeDensity.GetValueOrDefault(k, 0.0) : 0.0;

    double FirstArea => _target == null ? 0 : Map.Parcels.GetValueOrDefault(_target.Parcels[0])?.AreaM2 ?? 0;

    /// <summary>Choose rules, density or count; rules also forgets the seed offset (the entry goes away).</summary>
    public bool SetTreesKind(string kind)
    {
        if (kind is not (TreesDraft.Rules or TreesDraft.Density or TreesDraft.Count)) { Say($"unknown trees mode {kind}", true); return false; }
        if (_target != null && CurrentTrees.Kind == kind) return true;
        return EditTrees(t =>
        {
            if (kind == t.Kind) return;
            var density = t.Kind == TreesDraft.Rules ? RulesDensity : t.Kind == TreesDraft.Density ? t.DensityValue : t.CountValue * 1000.0 / Math.Max(1.0, FirstArea);
            if (kind == TreesDraft.Rules) { t.Seed = 0; t.Kind = kind; return; }
            if (kind == TreesDraft.Density) t.DensityValue = Math.Round(density, 2);
            else t.CountValue = (int)Math.Round(density * FirstArea / 1000.0);
            t.Kind = kind;
        });
    }

    public bool SetTreesValue(double v)
        => EditTrees(t =>
        {
            if (t.Kind == TreesDraft.Density) t.DensityValue = Math.Max(0, v);
            else if (t.Kind == TreesDraft.Count) t.CountValue = (int)Math.Max(0, Math.Round(v));
        });

    public bool SetSeedOffset(int seed) => EditTrees(t => { if (t.Kind != TreesDraft.Rules) t.Seed = seed; });

    /// <summary>Increment the seed offset. From the rules it first switches to the rules' density (a seed alone is not expressible).</summary>
    public bool Reroll()
        => EditTrees(t =>
        {
            if (t.Kind == TreesDraft.Rules) { t.Kind = TreesDraft.Density; t.DensityValue = RulesDensity; }
            t.Seed++;
        });

    /// <summary>Remove the trees entry: the rules decide again.</summary>
    public bool ResetTrees() => SetTreesKind(TreesDraft.Rules);

    public void SetGroupScope(bool group)
    {
        _scopeGroup = group;
        _panelDirty = true;
    }

    // ---- apply and revert ----

    /// <summary>Write all pending targets in one atomic save (the file is reloaded first); true when saved.</summary>
    public bool Apply()
    {
        if (_pending.Count == 0) { Say("nothing to apply"); return false; }
        var items = _pending.Values.ToList();
        var ok = Mutate(root =>
        {
            var errors = EditModel.Apply(root, items);
            return errors.Count > 0 ? (false, "not saved: " + errors[0]) : (true, items.Count == 1 ? items[0].Target.Header : $"{items.Count} targets");
        }, "apply " + (items.Count == 1 ? items[0].Target.Header : $"{items.Count} targets"));
        if (!ok) return false;
        _pending.Clear();
        AfterEdit();
        return true;
    }

    /// <summary>Discard the pending changes of the current target.</summary>
    public void Revert()
    {
        if (_target != null && _pending.Remove(_target.Key)) { Say($"reverted {_target.Header}"); AfterEdit(); }
    }

    public void RevertAll()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        Say("reverted all unsaved changes");
        AfterEdit();
    }

    // ---- gate tool ----

    public bool GateArmed { get; private set; }
    public bool GateDragging => _gateStart != null;
    public double GateWidth { get; private set; } = EditSchema.GateDefault;
    (Vector2 point, Vector2 tangent)? _gateStart;
    Node3D _gateRoot = null!, _previewRoot = null!;
    public Node3D MarkerRoot { get; private set; } = null!;

    /// <summary>Refresh the panel and the outlines now (normally done on the next frame).</summary>
    public void RefreshNow() { RefreshPanel(); RebuildOutlines(); }

    /// <summary>The fence boundary of the target in ground axes: a parcel's own edges, a group's outer perimeter; null for several loose parcels.</summary>
    List<(Vector2 a, Vector2 b)>? TargetBoundary()
    {
        if (_target == null) return null;
        if (_target.IsGroup)
            return GroupPerimeter(_target.Parcels.Where(Map.Parcels.ContainsKey).Select(id => Map.Parcels[id]).ToList());
        if (_target.Parcels.Count == 1 && Map.Parcels.TryGetValue(_target.Parcels[0], out var p)) return AllEdges(p).ToList();
        return null;
    }

    public bool CanGate => _target != null && (_target.IsGroup || _target.Parcels.Count == 1);

    /// <summary>The point of the segments nearest to p (within maxDist) and the segment's direction.</summary>
    public static (Vector2 point, Vector2 tangent, float dist)? NearestOnBoundary(IReadOnlyList<(Vector2 a, Vector2 b)> segs, Vector2 p, float maxDist)
    {
        (Vector2, Vector2, float)? best = null;
        foreach (var (a, b) in segs)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();
            var t = len2 < 1e-12f ? 0f : Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
            var q = a + ab * t;
            var d = q.DistanceTo(p);
            if (d <= maxDist && (best == null || d < best.Value.Item3)) best = (q, ab.Normalized(), d);
        }
        return best;
    }

    (double x, double y) ToAbsolute(Vector2 ground)
    {
        var o = Map.OriginEpsg5514!.Value;
        return (Math.Round(o.X + ground.X, 3), Math.Round(o.Y - ground.Y, 3));
    }

    Vector2 FromAbsolute(double x, double y)
    {
        var o = Map.OriginEpsg5514!.Value;
        return new Vector2((float)(x - o.X), (float)(-(y - o.Y)));
    }

    /// <summary>[Add gate]: the next click picks the fence point.</summary>
    public bool ArmGate()
    {
        if (PlacementAsset != null) DisarmPlacement(false);
        if (!CanGate) { Say("gates: select one parcel or one property group", true); return false; }
        if (CurrentFence.EffectiveMode != "custom") { Say("gates need fence mode custom", true); return false; }
        if (Map.OriginEpsg5514 == null) { Say("this map has no origin_epsg5514 (export it again, map 0.4.0+)", true); return false; }
        GateArmed = true;
        _gateStart = null;
        Say("click the fence where the gate goes, drag for the width, release to add; Esc cancels");
        _panelDirty = true;
        return true;
    }

    public void CancelGate()
    {
        var was = GateArmed;
        GateArmed = false;
        _gateStart = null;
        ClearPreview();
        if (was) Say("gate cancelled");
        _panelDirty = true;
    }

    /// <summary>Press: pick the nearest fence point within 6 m of the ground point. False (tool stays armed) when none.</summary>
    public bool GateBegin(Vector3 ground)
    {
        if (!GateArmed) return false;
        var b = TargetBoundary();
        var near = b == null ? null : NearestOnBoundary(b, new Vector2(ground.X, ground.Z), (float)EditSchema.GatePickRadius);
        if (near == null) { Say($"no fence boundary of {_target?.Header} within {EditSchema.GatePickRadius:F0} m of that point", true); return false; }
        _gateStart = (near.Value.point, near.Value.tangent);
        GateWidth = EditSchema.GateDefault;
        UpdatePreview();
        return true;
    }

    /// <summary>Drag: the width is twice the distance from the picked point (the gate is centred there), snapped to 0.5 m, at least 1 m.</summary>
    public void GateDrag(Vector3 ground)
    {
        if (_gateStart is not { } s) return;
        var d = new Vector2(ground.X, ground.Z).DistanceTo(s.point);
        GateWidth = d < 0.25f ? EditSchema.GateDefault : Math.Max(EditSchema.GateMin, Math.Round(2.0 * d / EditSchema.GateSnap) * EditSchema.GateSnap);
        UpdatePreview();
    }

    /// <summary>Release: add the gate to the pending fence of the target.</summary>
    public bool GateEnd()
    {
        if (_gateStart is not { } s) return false;
        var (x, y) = ToAbsolute(s.point);
        var width = GateWidth;
        _gateStart = null;
        GateArmed = false;
        ClearPreview();
        var ok = EditFence(d => d.Gaps.Add(new Gap(x, y, width)), true);
        if (ok)
        {
            _selectedGate = CurrentFence.Gaps.Count - 1;
            Say($"gate added: {width:F1} m wide (not saved yet, Apply writes it)");
        }
        return ok;
    }

    void UpdateGateDrag()
    {
        if (_gateStart == null) return;
        var mouse = GetViewport().GetMousePosition();
        if (RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var g)) GateDrag(g);
    }

    // ---- gate markers ----

    static readonly Color GateColour = new(1f, 0.55f, 0.1f), GateSelectedColour = new(1f, 0.2f, 0.9f), GatePreviewColour = new(0.1f, 0.95f, 1f);

    void ClearPreview()
    {
        foreach (var c in _previewRoot.GetChildren()) { _previewRoot.RemoveChild(c); c.QueueFree(); }
    }

    void UpdatePreview()
    {
        ClearPreview();
        if (_gateStart is { } s) AddGateMarker(_previewRoot, s.point, s.tangent, (float)GateWidth, GatePreviewColour);
    }

    void AddGateMarker(Node3D parent, Vector2 point, Vector2 tangent, float width, Color colour)
    {
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var half = tangent * width / 2f;
        var ribbon = new MeshInstance3D { MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ribbon.Mesh = BuildRibbon(new List<(Vector2, Vector2)> { (point - half, point + half) }, 0.45f, 0.6f);
        parent.AddChild(ribbon);
        foreach (var end in new[] { point - half, point + half })
        {
            var y = Map.GroundHeightOrMean(end.X, end.Y);
            var post = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.1f, Height = 2.4f, RadialSegments = 8 },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(end.X, y + 1.2f, end.Y),
            };
            parent.AddChild(post);
        }
    }

    /// <summary>Number of gate markers currently drawn (existing gates of the target; the smoke checks it).</summary>
    public int GateMarkerCount => _gateRoot.GetChildCount() / 3;

    void RebuildGateMarkers()
    {
        foreach (var c in _gateRoot.GetChildren()) { _gateRoot.RemoveChild(c); c.QueueFree(); }
        if (_target == null || !CanGate || Map.OriginEpsg5514 == null) return;
        var boundary = TargetBoundary();
        if (boundary == null) return;
        var gaps = CurrentFence.Gaps;
        for (var i = 0; i < gaps.Count; i++)
        {
            var at = FromAbsolute(gaps[i].X, gaps[i].Y);
            var near = NearestOnBoundary(boundary, at, 1000f);
            if (near == null) continue;
            AddGateMarker(_gateRoot, near.Value.point, near.Value.tangent, (float)gaps[i].Width, i == _selectedGate ? GateSelectedColour : GateColour);
        }
    }

    // ---- UI ----

    OptionButton _modeOpt = null!, _typeOpt = null!, _scopeOpt = null!;
    Label _hdrLabel = null!, _dirtyLabel = null!, _footLabel = null!, _fenceInfo = null!, _gateHint = null!, _treesNote = null!, _rulesLabel = null!, _mixedLabel = null!;
    CheckBox _heightChk = null!, _thickChk = null!, _colourChk = null!;
    SpinBox _heightSpin = null!, _thickSpin = null!, _densitySpin = null!, _countSpin = null!, _seedSpin = null!;
    ColorPickerButton _colourBtn = null!;
    VBoxContainer _gapsBox = null!;
    Button _btnAddGate = null!, _btnApply = null!, _btnRevert = null!, _btnRevertAll = null!, _btnReroll = null!, _btnResetTrees = null!;
    CheckBox _radioRules = null!, _radioDensity = null!, _radioCount = null!;
    HBoxContainer _scopeRow = null!;
    string _gapRowsKey = "";

    static readonly Color Grey = EditorTheme.Dim;

    static Label Note(string text = "")
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.ThemeTypeVariation = "Dim";
        return l;
    }

    static HBoxContainer Row(string label, Control field, int labelWidth = 70)
    {
        var r = new HBoxContainer();
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(labelWidth, 0) };
        r.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        r.AddChild(field);
        return r;
    }

    static SpinBox Spin(double min, double max, double step, string suffix = "")
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Suffix = suffix, AllowGreater = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        // after Enter in the field, release the focus so the next Enter applies the changes
        s.GetLineEdit().TextSubmitted += _ => s.GetLineEdit().ReleaseFocus();
        return s;
    }

    CheckBox SetBox(string text = "set")
    {
        var c = new CheckBox { Text = text, FocusMode = Control.FocusModeEnum.None };
        return c;
    }

    void BuildPanelHeader(VBoxContainer outer)
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
        _scopeOpt.ItemSelected += i => { if (!_syncing) SetGroupScope(i == 0); };
        _scopeRow.AddChild(new Label { Text = "Edit: " });
        _scopeRow.AddChild(_scopeOpt);
        outer.AddChild(_scopeRow);
        _mixedLabel = Note();
        outer.AddChild(_mixedLabel);
    }

    void BuildPanelFooter(VBoxContainer outer)
    {
        _footLabel = Note("no unsaved changes");
        outer.AddChild(_footLabel);
        var r = new HBoxContainer();
        _btnApply = Btn("Apply (Enter)", () => Apply());
        _btnRevert = Btn("Revert", Revert);
        _btnRevertAll = Btn("Revert all", RevertAll);
        foreach (var b in new[] { _btnApply, _btnRevert, _btnRevertAll }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; r.AddChild(b); }
        outer.AddChild(r);
        MarkerRoot = new Node3D { Name = "EditorMarkers", Visible = false };
        _gateRoot = new Node3D { Name = "EditorGates" };
        _previewRoot = new Node3D { Name = "EditorGatePreview" };
        MarkerRoot.AddChild(_gateRoot);
        MarkerRoot.AddChild(_previewRoot);
        AddChild(MarkerRoot);
    }

    void BuildFenceSection(VBoxContainer body)
    {
        _modeOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var m in EditSchema.FenceModes) _modeOpt.AddItem(m);
        _modeOpt.ItemSelected += i => { if (!_syncing) SetFenceMode(EditSchema.FenceModes[(int)i]); };
        var modeRow = Row("mode", _modeOpt);
        modeRow.AddChild(Btn("Reset", () => ResetFence()));
        body.AddChild(modeRow);
        _fenceInfo = Note();
        body.AddChild(_fenceInfo);

        _typeOpt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        _typeOpt.AddItem("(default)");
        foreach (var t in EditSchema.FenceTypeNames) _typeOpt.AddItem(t);
        _typeOpt.ItemSelected += i => { if (!_syncing) SetFenceType(i == 0 ? null : EditSchema.FenceTypes[(int)i - 1].Name); };
        body.AddChild(Row("type", _typeOpt));

        _heightSpin = Spin(0.1, 10, 0.05, " m");
        _heightChk = SetBox();
        _heightChk.Toggled += on => { if (!_syncing) SetFenceHeight(on ? _heightSpin.Value : null); };
        _heightSpin.ValueChanged += v => { if (!_syncing) SetFenceHeight(v); };
        var hr = Row("height", _heightSpin); hr.AddChild(_heightChk); body.AddChild(hr);

        _thickSpin = Spin(0.01, 2, 0.01, " m");
        _thickChk = SetBox();
        _thickChk.Toggled += on => { if (!_syncing) SetFenceThickness(on ? _thickSpin.Value : null); };
        _thickSpin.ValueChanged += v => { if (!_syncing) SetFenceThickness(v); };
        var tr = Row("thickness", _thickSpin); tr.AddChild(_thickChk); body.AddChild(tr);

        _colourBtn = new ColorPickerButton { CustomMinimumSize = new Vector2(80, 28), FocusMode = Control.FocusModeEnum.None, EditAlpha = false };
        _colourChk = SetBox();
        _colourChk.Toggled += on => { if (!_syncing) SetFenceColour(on ? "#" + _colourBtn.Color.ToHtml(false) : null); };
        _colourBtn.ColorChanged += c => { if (!_syncing) SetFenceColour("#" + c.ToHtml(false)); };
        var cr = Row("colour", _colourBtn); cr.AddChild(_colourChk); body.AddChild(cr);

        var gh = new Label { Text = "Gates" };
        gh.ThemeTypeVariation = "Header";
        body.AddChild(gh);
        _gapsBox = new VBoxContainer();
        body.AddChild(_gapsBox);
        _btnAddGate = Btn("Add gate", () => { if (GateArmed) CancelGate(); else ArmGate(); });
        body.AddChild(_btnAddGate);
        _gateHint = Note();
        body.AddChild(_gateHint);
    }

    void BuildTreesSection(VBoxContainer body)
    {
        _treesNote = Note();
        body.AddChild(_treesNote);
        _rulesLabel = Note();
        body.AddChild(_rulesLabel);
        var group = new ButtonGroup();
        CheckBox Radio(string text, string kind)
        {
            var c = new CheckBox { Text = text, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None };
            c.Toggled += on => { if (on && !_syncing) SetTreesKind(kind); };
            return c;
        }
        _radioRules = Radio("rules (default)", TreesDraft.Rules);
        _radioDensity = Radio("density", TreesDraft.Density);
        _densitySpin = Spin(0, 500, 0.1, " / 1000 m2");
        _densitySpin.ValueChanged += v => { if (!_syncing) SetTreesValue(v); };
        _radioCount = Radio("count", TreesDraft.Count);
        _countSpin = Spin(0, 5000, 1, " trees");
        _countSpin.ValueChanged += v => { if (!_syncing) SetTreesValue(v); };
        body.AddChild(_radioRules);
        var d = new HBoxContainer(); d.AddChild(_radioDensity); d.AddChild(_densitySpin); body.AddChild(d);
        var c2 = new HBoxContainer(); c2.AddChild(_radioCount); c2.AddChild(_countSpin); body.AddChild(c2);
        _seedSpin = Spin(-100000, 100000, 1);
        _seedSpin.ValueChanged += v => { if (!_syncing) SetSeedOffset((int)v); };
        _btnReroll = Btn("Reroll", () => Reroll());
        var sr = Row("seed offset", _seedSpin, 100); sr.AddChild(_btnReroll); body.AddChild(sr);
        _btnResetTrees = Btn("Reset to rules", () => ResetTrees());
        body.AddChild(_btnResetTrees);
    }

    // texts for the smoke
    public string PanelHeaderText => _hdrLabel.Text;
    public bool PanelDirtyShown => _dirtyLabel.Visible;
    public string PanelFooterText => _footLabel.Text;
    public string PanelTreesNote => _treesNote.Text;
    public string PanelFenceInfo => _fenceInfo.Text;
    public bool ScopeRowShown => _scopeRow.Visible;
    public bool GateButtonEnabled => !_btnAddGate.Disabled;
    public string[] FenceTypeItems => Enumerable.Range(1, _typeOpt.ItemCount - 1).Select(i => _typeOpt.GetItemText(i)).ToArray();
    public string[] FenceModeItems => Enumerable.Range(0, _modeOpt.ItemCount).Select(i => _modeOpt.GetItemText(i)).ToArray();
    public bool FenceFieldsEnabled => !_typeOpt.Disabled;
    public int GapRowCount => _gapsBox.GetChildCount();

    /// <summary>Bring every control in line with the target and its draft (called when the panel is dirty).</summary>
    void RefreshEditPanel()
    {
        UpdateTarget();
        _syncing = true;
        try { SyncControls(); }
        finally { _syncing = false; }
        RebuildGateMarkers();
        _outlinesDirty = true;
    }

    void SyncControls()
    {
        var t = _target;
        var has = t != null;
        _hdrLabel.Text = t?.Header ?? "Nothing selected";
        _dirtyLabel.Visible = TargetDirty;
        var single = Selection.Count == 1 && has && Map.Parcels.TryGetValue(Selection.First(), out var sp) && sp.Group != null;
        _scopeRow.Visible = single;
        _scopeOpt.Select(_scopeGroup ? 0 : 1);
        _mixedLabel.Text = has && EditModel.Mixed(Disk(), t!) ? $"(the selected parcels differ on disk; showing {t!.Parcels[0]})" : "";
        _footLabel.Text = _pending.Count == 0 ? "no unsaved changes" : $"{_pending.Count} unsaved target{(_pending.Count == 1 ? "" : "s")}: {string.Join(", ", _pending.Keys.Take(4))}";
        _btnApply.Disabled = _pending.Count == 0;
        _btnRevert.Disabled = !TargetDirty;
        _btnRevertAll.Visible = _pending.Count > 1;

        // ---- fence ----
        var d = CurrentFence;
        var mode = d.EffectiveMode;
        var custom = mode == "custom";
        var eff = d.EffectiveCustom(InheritedGroupFence());
        _modeOpt.Disabled = !has;
        _modeOpt.Select(Array.IndexOf(EditSchema.FenceModes, mode));
        var defs = Map.EditDefaults;
        _fenceInfo.Text = !has ? "" : mode switch
        {
            "none" => "none: no fence" + (t!.IsGroup ? " around the group." : "."),
            "auto" => defs == null ? "auto: the rules decide." : $"auto: the rules decide (kinds {string.Join("/", defs.FenceKinds)}, {defs.FenceProbability:P0} of them, height {defs.FenceHeight:0.##} m).",
            _ => t!.IsGroup ? "custom: always fenced around the group's outer edge; settings apply to the whole group." : "custom: always fenced on the boundary.",
        };
        _typeOpt.SetItemText(0, $"(default: {eff.type})");
        _typeOpt.Select(d.Type == null ? 0 : 1 + Array.IndexOf(EditSchema.FenceTypes.Select(x => x.Name).ToArray(), d.Type));
        _typeOpt.Disabled = !custom;
        SyncOptional(_heightChk, _heightSpin, d.Height, eff.height, custom);
        SyncOptional(_thickChk, _thickSpin, d.Thickness, eff.thickness, custom);
        _colourChk.SetPressedNoSignal(d.Colour != null);
        _colourChk.Disabled = !custom;
        _colourBtn.Color = Color.FromHtml(d.Colour ?? eff.colour);
        _colourBtn.Disabled = !custom || d.Colour == null;
        _colourBtn.Modulate = d.Colour == null ? Grey : Colors.White;
        SyncGapRows(d, custom);
        _btnAddGate.Disabled = !has || !custom || !CanGate;
        _btnAddGate.Text = GateArmed ? "Click the fence... (Esc cancels)" : "Add gate";
        _gateHint.Text = !has ? "" : !CanGate ? "gates: select one parcel or one property group" : !custom ? "gates need mode custom" : Map.OriginEpsg5514 == null ? "gates need a map exported with origin_epsg5514 (0.4.0+)" : "";

        // ---- trees ----
        var tr = CurrentTrees;
        _treesNote.Text = !has ? "" : t!.IsGroup ? $"applies to {t.Parcels.Count} parcels (per parcel, the whole group)" : t.Parcels.Count > 1 ? $"applies to {t.Parcels.Count} parcels" : "";
        _rulesLabel.Text = !has ? "" : $"rules: {FirstKind} {RulesDensity:0.##} per 1000 m2 (about {RulesDensity * FirstArea / 1000.0:0.#} trees, max {defs?.MaxTreesPerParcel})";
        _radioRules.SetPressedNoSignal(tr.Kind == TreesDraft.Rules);
        _radioDensity.SetPressedNoSignal(tr.Kind == TreesDraft.Density);
        _radioCount.SetPressedNoSignal(tr.Kind == TreesDraft.Count);
        foreach (var r in new[] { _radioRules, _radioDensity, _radioCount }) r.Disabled = !has;
        _densitySpin.SetValueNoSignal(tr.Kind == TreesDraft.Density ? tr.DensityValue : RulesDensity);
        _densitySpin.Editable = tr.Kind == TreesDraft.Density;
        _densitySpin.Modulate = tr.Kind == TreesDraft.Density ? Colors.White : Grey;
        _countSpin.SetValueNoSignal(tr.Kind == TreesDraft.Count ? tr.CountValue : Math.Round(RulesDensity * FirstArea / 1000.0));
        _countSpin.Editable = tr.Kind == TreesDraft.Count;
        _countSpin.Modulate = tr.Kind == TreesDraft.Count ? Colors.White : Grey;
        _seedSpin.SetValueNoSignal(tr.Seed);
        _seedSpin.Editable = tr.Kind != TreesDraft.Rules;
        _seedSpin.Modulate = tr.Kind == TreesDraft.Rules ? Grey : Colors.White;
        _btnReroll.Disabled = !has;
        _btnResetTrees.Disabled = !has || tr.Kind == TreesDraft.Rules;
    }

    static void SyncOptional(CheckBox chk, SpinBox spin, double? value, double effective, bool custom)
    {
        chk.SetPressedNoSignal(value != null);
        chk.Disabled = !custom;
        spin.SetValueNoSignal(value ?? effective);
        spin.Editable = custom && value != null;
        spin.Modulate = value == null ? Grey : Colors.White;
    }

    void SyncGapRows(FenceDraft d, bool custom)
    {
        var key = $"{_target?.Key}|{d.Gaps.Count}";
        if (key != _gapRowsKey)
        {
            _gapRowsKey = key;
            foreach (var c in _gapsBox.GetChildren()) { _gapsBox.RemoveChild(c); c.QueueFree(); }
            for (var i = 0; i < d.Gaps.Count; i++)
            {
                var idx = i;
                var row = new HBoxContainer { Name = $"gap{i}" };
                var pick = new Button { Text = $"gate {i + 1}", FocusMode = Control.FocusModeEnum.None, ToggleMode = true };
                pick.Pressed += () => SelectGate(idx);
                var w = Spin(EditSchema.GateMin, 30, EditSchema.GateSnap, " m");
                w.ValueChanged += v => { if (!_syncing) SetGapWidth(idx, v); };
                var del = Btn("delete", () => RemoveGap(idx));
                row.AddChild(pick); row.AddChild(w); row.AddChild(del);
                _gapsBox.AddChild(row);
            }
        }
        for (var i = 0; i < d.Gaps.Count && i < _gapsBox.GetChildCount(); i++)
        {
            var row = (HBoxContainer)_gapsBox.GetChild(i);
            var pick = (Button)row.GetChild(0);
            pick.SetPressedNoSignal(i == _selectedGate);
            pick.Disabled = !custom;
            ((SpinBox)row.GetChild(1)).SetValueNoSignal(d.Gaps[i].Width);
            ((SpinBox)row.GetChild(1)).Editable = custom;
            ((Button)row.GetChild(2)).Disabled = !custom;
        }
    }
}

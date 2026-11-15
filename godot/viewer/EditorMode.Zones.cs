using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Drycastle.MapLoader;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// The Zones tool (GLOSSARY "Zone", "Zone kind", ADR 0022): what an area means in play, drawn over the ground without changing it.
/// A zone is a hand-drawn polygon of exactly one zone kind (edits.json `zones`, {Z-NNNN: {kind, polygon}}); zones overlap freely.
/// The kinds are a fixed catalog in code, which the map lists in parcels.json `zone_kinds` (label, tint, and `where_ground` when a kind
/// also applies by rule wherever some ground types lie, drawing nothing). The polygon gestures are the shared machinery of
/// EditorMode.Shapes.cs. The page lists the kinds (swatch = tint, label, how many zones are drawn, the rule), the zone form
/// (change kind, delete) and all zones.
///
/// The overlay (M toggles it, remembered in editor_ui.cfg) draws every zone translucent in its kind's tint with a thin edge; the
/// selected kind is brightened and the others dimmed; rule zones (the baked ground areas of a kind's where_ground types) are drawn
/// lighter than drawn ones, and the hover text says "rule". Hovering names the zones and rule kinds at the cursor, clicking a zone
/// selects it, clicking a rule zone selects its kind. Zones have no paint and no hot reload; the build turns them into candidate points.
/// Nothing is saved by the overlay.
/// </summary>
public partial class EditorMode
{
    const float ZoneFill = 0.42f, ZoneFillSelected = 0.62f, ZoneFillDim = 0.14f;
    const float ZoneRuleFill = 0.20f, ZoneRuleFillSelected = 0.34f, ZoneRuleFillDim = 0.07f;

    public string? SelectedZoneKind { get; private set; }
    string? _restoreZoneKind;

    VBoxContainer _zKinds = null!;
    Label _zError = null!, _zKindsHead = null!, _zOverlayNote = null!;
    OptionButton _zKindOpt = null!;
    HBoxContainer _zKindRow = null!;
    CheckBox _zOverlayCheck = null!;
    bool _zSync;
    string _zKindsSig = "\0", _zKindOptSig = "\0", _zScrolledFor = "";
    readonly Dictionary<string, Button> _zKindRows = new();

    // overlay
    MeshInstance3D? _zoMesh;
    bool _zoOn = true, _zoIndexed;
    object? _zoDisk, _zoKindsFor;
    string _zoSig = "";
    int _zoStep;
    readonly TriangleBudget _zoBudget = new();
    ArrayMesh? _zoArrayMesh;
    List<string> _zoOrder = new();
    readonly Dictionary<string, GroundMapType> _zoTypes = new();   // key: the kind (drawn zones) or kind|rule (its rule zones)
    const string RuleSuffix = "|rule";

    public bool ZoneOverlayShown => _zoOn;
    public bool ZoneOverlayBuilt => _zoArrayMesh != null && _zoStep >= _zoOrder.Count && _zoOrder.Count > 0;

    // ---- the catalog ----

    ZoneKindInfo? ZoneKindOf(string name) => Map.ZoneKinds.FirstOrDefault(k => k.Name == name);
    public string ZoneLabel(string name) => ZoneKindOf(name)?.Label ?? name;
    Color ZoneTint(string name) => ZoneKindOf(name) is { } k && ParseColour(k.Tint) is { } c ? Color.FromHtml(c) : new Color(0.5f, 0.5f, 0.5f);
    static string RuleText(ZoneKindInfo k) => k.WhereGround is { Count: > 0 } w ? "rule: " + string.Join(", ", w) : "";

    /// <summary>Tell the validator which kinds exist (it checks a zone's kind against the map's catalog; no catalog: no check).</summary>
    void SyncCatalogs()
    {
        GroundRows(); // sets what the ground type validation needs
        ObjectEdits.ZoneKinds = Map.ZoneKinds.Select(k => k.Name).ToList();
    }

    public bool SelectZoneKind(string? name)
    {
        if (name != null && ZoneKindOf(name) == null) { Say($"zone kind {name} is not in this map's catalog", true); return false; }
        if (name == SelectedZoneKind) return true;
        SelectedZoneKind = name;
        _panelDirty = true;
        SaveUiConfig();
        return true;
    }

    string? CannotDrawZone()
    {
        if (Map.ZoneKinds.Count == 0) return "this map lists no zone kinds: build it again (make map), then reopen the editor";
        if (SelectedZoneKind is not { } k) return "pick a zone kind in the list first: a zone is drawn of the selected kind";
        if (ZoneKindOf(k) == null) return $"{k} is not in this map's catalog any more";
        return null;
    }

    /// <summary>Give a zone another kind of the catalog (one save).</summary>
    public bool SetZoneKind(string id, string kind)
    {
        if (Zone(id) is not { } z) { Say($"zone {id} does not exist", true); return false; }
        if (ZoneKindOf(kind) == null) { Say($"zone kind {kind} is not in this map's catalog", true); return false; }
        if (z.Kind == kind) return true;
        var ok = Mutate(root =>
        {
            if (ShapesOf(root, ShapeKind.Zone)?.GetValueOrDefault(id) is not Obj e) return (false, $"zone {id} no longer exists");
            e["kind"] = kind;
            return (true, $"{id} is now {ZoneLabel(kind)}");
        });
        if (ok && SelectedZoneId == id) SelectZoneKind(kind);
        return ok;
    }

    // ---- keys ----

    /// <summary>Zones tool keys besides the shape keys (not reached while a text field has the focus). True when used.</summary>
    bool HandleZoneKey(InputEventKey k)
    {
        if (Tool != EditorTool.Zones || _cheat.Visible || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.M: ToggleZoneOverlay(); return true;
            case Key.Tab when GetViewport().GuiGetFocusOwner() == null: FocusZoneList(); return true;
        }
        return false;
    }

    void FocusZoneList()
    {
        (SelectedZoneKind != null && _zKindRows.TryGetValue(SelectedZoneKind, out var b) ? b : _zKindRows.Values.FirstOrDefault())?.GrabFocus();
    }

    /// <summary>Esc: one level up (the kind; the zone and the drawing went first, the tool is left by the main handler). True when it deselected.</summary>
    bool ZoneEscape()
    {
        if (SelectedZoneKind == null) return false;
        SelectZoneKind(null);
        return true;
    }

    public string ZonesHintText() => ShapeHintText()
        ?? (SelectedZoneKind == null
            ? "zones: Tab or click a kind in the list, click a zone to select it, M shows or hides the overlay, N draws a zone of the selected kind, Ctrl+P commands, Esc leaves the tool"
            : $"{ZoneLabel(SelectedZoneKind)}: N draws a zone of it, click a zone to select it, M shows or hides the overlay, Ctrl+Z undoes, Esc deselects");

    // ---- the page ----

    void BuildZonesPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Zones", ThemeTypeVariation = "Header" });
        page.AddChild(Note("A zone marks what an area means in play (Bigfoot, deer, player spawn, ...). It never changes how the ground looks, and zones overlap freely. Pick a kind, draw a zone of it; some kinds also apply by rule wherever certain ground lies, with nothing to draw."));
        _zError = Note("", "Danger");
        page.AddChild(_zError);
        _zOverlayCheck = new CheckBox
        {
            Text = "Show zones on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = _zoOn,
            TooltipText = "Draw every zone on the terrain in its kind's tint (M). The selected kind is brightened, the others dimmed; rule zones are lighter. Hover names the zones, click selects one.",
        };
        _zOverlayCheck.Toggled += on => { if (!_zSync) SetZoneOverlay(on); };
        page.AddChild(_zOverlayCheck);
        _zOverlayNote = Note("");
        page.AddChild(_zOverlayNote);

        _zKindsHead = new Label { Text = "Zone kinds", ThemeTypeVariation = "Header" };
        page.AddChild(_zKindsHead);
        _zKinds = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_zKinds);

        page.AddChild(new HSeparator());
        _zKindOpt = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _zKindOpt.ItemSelected += i =>
        {
            if (!_zSync && SelectedZoneId != null && i >= 0 && i < Map.ZoneKinds.Count) SetZoneKind(SelectedZoneId, Map.ZoneKinds[(int)i].Name);
            _zKindOpt.ReleaseFocus();
        };
        _zKindRow = FieldRow("Kind", _zKindOpt, "The zone kind of the selected zone");
        BuildShapeActions(page, ShapeKind.Zone, _zKindRow);
        BuildShapeDelete(page, ShapeKind.Zone);
        BuildShapeList(page, ShapeKind.Zone, "All zones", "No zones drawn yet. Pick a kind, press N, click corners on the ground, Enter creates the zone.");
        page.AddChild(Note("Zones overlap freely and add up. Esc steps back: cancel drawing, deselect the zone, deselect the kind, leave the tool."));
    }

    void RefreshZonesPanel()
    {
        if (Tool != EditorTool.Zones) return;
        var zones = ZoneList();
        if (_restoreZoneKind != null) { if (ZoneKindOf(_restoreZoneKind) != null) SelectedZoneKind = _restoreZoneKind; _restoreZoneKind = null; }
        if (_restoreZone != null) { if (zones.Any(z => z.Id == _restoreZone)) SelectedZoneId = _restoreZone; _restoreZone = null; }
        if (SelectedZoneId != null && zones.All(z => z.Id != SelectedZoneId)) { SelectedZoneId = null; _dragCorner = -1; }
        if (SelectedZoneKind != null && ZoneKindOf(SelectedZoneKind) == null) SelectedZoneKind = null;
        var sel = SelectedZoneId == null ? null : Zone(SelectedZoneId);
        var kinds = Map.ZoneKinds;

        _zError.Visible = kinds.Count == 0;
        _zError.Text = "This map's parcels.json lists no zone kinds (it was built before zones existed). Build it again (make map), then reopen the editor. Zones cannot be drawn until then.";
        _zSync = true; _zOverlayCheck.SetPressedNoSignal(_zoOn); _zSync = false;
        _zKindsHead.Text = $"Zone kinds ({kinds.Count})";

        // the kinds
        var counts = zones.GroupBy(z => z.Kind).ToDictionary(g => g.Key, g => g.Count());
        var kindsSig = string.Join("|", kinds.Select(k => k.Name + k.Tint + RuleText(k) + counts.GetValueOrDefault(k.Name))) + "#" + SelectedZoneKind;
        if (kindsSig != _zKindsSig)
        {
            _zKindsSig = kindsSig;
            foreach (var b in _zKindRows.Values) { _zKinds.RemoveChild(b); b.QueueFree(); }
            _zKindRows.Clear();
            foreach (var c in _zKinds.GetChildren().OfType<Label>().ToList()) { _zKinds.RemoveChild(c); c.QueueFree(); }
            if (kinds.Count == 0) _zKinds.AddChild(Note("No zone kinds in this map. Build it again (make map)."));
            foreach (var k in kinds)
            {
                var name = k.Name;
                var n = counts.GetValueOrDefault(name);
                var rule = RuleText(k);
                var b = new Button
                {
                    Text = $"{k.Label}   {n} drawn" + (rule != "" ? $"   {rule}" : ""), Icon = Swatch(k.Tint), Alignment = HorizontalAlignment.Left, ClipText = true,
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.All,
                    TooltipText = $"{k.Label} ({k.Group}): {n} zone{(n == 1 ? "" : "s")} drawn" + (rule != "" ? $"; {rule} (applies there without drawing)" : "; drawn only") + ". Select it to draw a zone of it (N) and to highlight it on the map.",
                };
                b.SetPressedNoSignal(name == SelectedZoneKind);
                b.Pressed += () => { SelectZoneKind(name == SelectedZoneKind ? null : name); b.ReleaseFocus(); };
                _zKinds.AddChild(b);
                _zKindRows[name] = b;
            }
        }

        var drawText = SelectedZoneKind != null ? $"Draw zone of {ZoneLabel(SelectedZoneKind)}  (N)" : "Draw zone of the selected kind  (N)";
        RefreshShapeActions(ShapeKind.Zone, drawText, s => ZoneLabel(((ZoneInfo)s).Kind));
        var mode = ShapeMode;
        var showForm = mode == ShapeState.Selected && sel != null;
        _zKindRow.Visible = showForm;
        if (showForm)
        {
            var sig = string.Join("|", kinds.Select(k => k.Name + k.Label + k.Tint));
            if (!_zKindOpt.GetPopup().Visible && sig != _zKindOptSig)
            {
                _zKindOptSig = sig;
                _zSync = true;
                _zKindOpt.Clear();
                foreach (var k in kinds) _zKindOpt.AddIconItem(Swatch(k.Tint), k.Label);
                _zSync = false;
            }
            var at = kinds.ToList().FindIndex(k => k.Name == sel!.Kind);
            if (!_zKindOpt.GetPopup().Visible && _zKindOpt.Selected != at) { _zSync = true; _zKindOpt.Selected = at; _zSync = false; }
            _zKindOpt.Disabled = kinds.Count == 0;
        }

        RefreshShapeList(ShapeKind.Zone, "zone", s =>
        {
            var z = (ZoneInfo)s;
            var tint = ZoneKindOf(z.Kind)?.Tint ?? "#808080";
            return ($"{z.Id}   {ZoneLabel(z.Kind)}   {z.Points.Count} corners", tint, $"Select {z.Id} ({ZoneLabel(z.Kind)}, {z.Points.Count} corners, {z.AreaM2:F0} m2)");
        }, string.Concat(kinds.Select(k => k.Name + k.Label + k.Tint)));

        if (sel != null && _zScrolledFor != sel.Id)
        {
            _zScrolledFor = sel.Id;
            Callable.From(() => _scroll.EnsureControlVisible(_su[ShapeKind.Zone].State)).CallDeferred();
        }
        else if (sel == null) _zScrolledFor = "";
    }

    // ---- the overlay ----

    public void ToggleZoneOverlay() => SetZoneOverlay(!_zoOn);

    public void SetZoneOverlay(bool on, bool save = true)
    {
        _zoOn = on;
        if (_zOverlayCheck != null) _zOverlayCheck.SetPressedNoSignal(on);
        if (_zoMesh != null) _zoMesh.Visible = on && Tool == EditorTool.Zones;
        _panelDirty = true;
        Say(on ? "zone overlay on: every zone in its kind's tint (M hides it)" : "zone overlay hidden (M shows it)");
        if (save) SaveUiConfig();
    }

    /// <summary>Index the zones (drawn) and the rule zones (the baked ground areas of each kind's where_ground types) per kind.</summary>
    void EnsureZoneIndex()
    {
        var disk = Disk();
        var kinds = Map.ZoneKinds;
        if (_zoIndexed && ReferenceEquals(disk, _zoDisk) && ReferenceEquals(kinds, _zoKindsFor)) return;
        _zoIndexed = true; _zoDisk = disk; _zoKindsFor = kinds;

        var entries = new List<(string Key, List<Vector2[]> Rings, bool Drawn)>();
        if (Map.OriginEpsg5514 != null)
            foreach (var z in ZoneList())
                entries.Add((z.Kind, new List<Vector2[]> { z.Points.Select(c => FromAbsolute(c.x, c.y)).ToArray() }, true));
        foreach (var k in kinds.Where(k => k.WhereGround != null))
            foreach (var a in Map.GroundAreas.Where(a => k.WhereGround!.Contains(a.Kind)))
                entries.Add((k.Name + RuleSuffix, a.Rings.ToList(), false));
        // geometry only changes with a drawn zone or a new map; tints and selection only touch the materials
        var sig = Map.GroundAreas.Count + "#" + string.Join(",", kinds.Select(k => k.Name + string.Join("+", k.WhereGround ?? new List<string>()))) + "#"
            + string.Join(";", entries.Where(e => e.Drawn).Select(e => e.Key + ":" + string.Join(",", e.Rings[0].Select(v => $"{v.X:F1},{v.Y:F1}"))));
        var geometryChanged = sig != _zoSig;
        _zoSig = sig;

        var prev = _zoTypes.ToDictionary(p => p.Key, p => (p.Value.Fill, p.Value.Edge));
        _zoTypes.Clear();
        foreach (var (key, rings, drawn) in entries)
        {
            if (!_zoTypes.TryGetValue(key, out var t)) _zoTypes[key] = t = new GroundMapType { Name = key };
            if (drawn) t.Drawn++; else t.Areas++;
            SplitRings(rings, t, out var area);
            t.AreaM2 += area;
        }
        if (geometryChanged) { _zoStep = 0; _zoBudget.Count = 0; _zoBudget.Truncated = false; _zoArrayMesh = null; if (_zoMesh != null) _zoMesh.Mesh = null; }
        else foreach (var (name, t) in _zoTypes) if (prev.TryGetValue(name, out var m)) { t.Fill = m.Fill; t.Edge = m.Edge; }
        var order = kinds.Select(k => k.Name).ToList();
        _zoOrder = _zoTypes.Keys.OrderBy(k => k.EndsWith(RuleSuffix) ? 0 : 1).ThenBy(k => order.IndexOf(k.Replace(RuleSuffix, "")), Comparer<int>.Default).ToList(); // rule zones under the drawn ones
    }

    /// <summary>Per frame while the tool is open: index, build one kind of the mesh, and keep tints and selection in the materials.</summary>
    void UpdateZoneOverlay()
    {
        var show = Tool == EditorTool.Zones && _zoOn && Map.OriginEpsg5514 != null;
        if (_zoMesh != null) _zoMesh.Visible = show;
        if (Tool != EditorTool.Zones) return;
        EnsureZoneIndex();
        if (_zOverlayNote != null) _zOverlayNote.Text = ZoneOverlayNote();
        if (!show || _zoOrder.Count == 0) return;
        if (_zoMesh == null)
        {
            _zoMesh = new MeshInstance3D { Name = "ZoneOverlay", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            OutlineRoot.AddChild(_zoMesh); // follows the editor's visibility: gone in walk mode
        }
        if (_zoStep < _zoOrder.Count)
        {
            _zoArrayMesh ??= new ArrayMesh();
            if (_zoStep == 0 && _zoArrayMesh.GetSurfaceCount() > 0) _zoArrayMesh = new ArrayMesh();
            var key = _zoOrder[_zoStep];
            var t = _zoTypes[key];
            var kind = key.Replace(RuleSuffix, "");
            var rule = key.EndsWith(RuleSuffix);
            BuildAreaSurfaces(_zoArrayMesh, t, ZoneTint(kind), rule ? ZoneRuleFill : ZoneFill, rule ? 4 : 6, _zoBudget);
            _zoStep++;
            _zoMesh.Mesh = _zoArrayMesh;
        }
        ApplyZoneMaterials();
    }

    string ZoneOverlayNote()
    {
        if (Map.ZoneKinds.Count == 0) return "No zone kinds in this map's parcels.json. Build it again (make map), then reopen the editor.";
        if (!_zoOn) return "The zone overlay is off.";
        if (_zoStep < _zoOrder.Count) return $"Drawing zones {_zoStep + 1}/{_zoOrder.Count}...";
        var drawn = _zoTypes.Values.Sum(t => t.Drawn);
        var rule = _zoTypes.Where(p => p.Key.EndsWith(RuleSuffix)).Sum(p => p.Value.Areas);
        return $"{drawn} drawn zone{(drawn == 1 ? "" : "s")}, {rule} ground area{(rule == 1 ? "" : "s")} by rule (lighter)" + (_zoBudget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    void ApplyZoneMaterials()
    {
        var sel = SelectedZoneKind;
        foreach (var (key, t) in _zoTypes)
        {
            var kind = key.Replace(RuleSuffix, "");
            var rule = key.EndsWith(RuleSuffix);
            var colour = ZoneTint(kind);
            var isSel = sel != null && kind == sel;
            var dim = sel != null && !isSel;
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, rule ? (isSel ? ZoneRuleFillSelected : dim ? ZoneRuleFillDim : ZoneRuleFill) : (isSel ? ZoneFillSelected : dim ? ZoneFillDim : ZoneFill));
            if (t.Edge != null) t.Edge.AlbedoColor = isSel ? new Color(colour.Lightened(0.55f), 0.95f) : new Color(colour, rule ? (dim ? 0.2f : 0.45f) : (dim ? GroundMapEdgeDim : 0.95f));
        }
    }

    // ---- pointing ----

    /// <summary>The kinds that apply at a ground point by rule (their where_ground ground areas), in catalog order.</summary>
    List<ZoneKindInfo> RuleKindsAt(Vector2 p)
    {
        EnsureZoneIndex();
        return Map.ZoneKinds.Where(k => k.WhereGround != null && _zoTypes.TryGetValue(k.Name + RuleSuffix, out var t) && HitType(t, p)).ToList();
    }

    /// <summary>Hover in the Zones tool: the zones and rule kinds at the cursor, as a label near it.</summary>
    string? ZoneHoverText(Vector3 point)
    {
        var (x, y) = RawAbs(point);
        var lines = new List<string>();
        foreach (var z in ZoneList().Where(z => PointInRing(z.Points, x, y)).OrderByDescending(z => ShapeNumber(z.Id)).Take(4))
            lines.Add($"{z.Id}  {ZoneLabel(z.Kind)} zone");
        foreach (var k in RuleKindsAt(new Vector2(point.X, point.Z)))
            lines.Add($"{k.Label}  (rule zone: {string.Join(", ", k.WhereGround!)})");
        if (lines.Count == 0) return "no zone here";
        return string.Join("\n", lines) + "\nclick to select";
    }

    /// <summary>Click on the ground in the Zones tool where no drawn zone is: a rule zone selects its kind.</summary>
    void ZonePick(Vector3 hit, double rx, double ry)
    {
        var rule = RuleKindsAt(new Vector2(hit.X, hit.Z));
        if (rule.Count == 0) { Say("no zone there"); return; }
        var k = rule[0];
        SelectZoneKind(k.Name);
        Say($"{k.Label} applies here by rule ({string.Join(", ", k.WhereGround!)}); nothing is drawn, so there is nothing to select or move. Draw a zone for more");
    }

    /// <summary>The zone to select at a point: where zones overlap the selected kind's win, then the higher ID.</summary>
    string? ZoneAtPoint(double x, double y)
    {
        var hits = ZoneList().Where(z => PointInRing(z.Points, x, y)).ToList();
        if (hits.Count == 0) return null;
        return hits.OrderBy(z => z.Kind == SelectedZoneKind ? 1 : 0).ThenBy(z => ShapeNumber(z.Id)).ThenBy(z => z.Id, StringComparer.Ordinal).Last().Id;
    }

    // test hooks
    public int ZoneKindRowCount => _zKindRows.Count;
    public string ZoneKindRowText(string kind) => _zKindRows.TryGetValue(kind, out var b) ? b.Text : "";
    public bool ZoneErrorShown => _zError.Visible;
    public string ZoneOverlayText => _zOverlayNote.Text;
    public void PressZoneKindRow(string kind) { if (_zKindRows.TryGetValue(kind, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
    public string ZoneKindShown => _zKindOpt.Selected >= 0 && _zKindOpt.Selected < Map.ZoneKinds.Count ? Map.ZoneKinds[_zKindOpt.Selected].Name : "";
    public int RuleZoneAreas(string kind) => _zoTypes.TryGetValue(kind + RuleSuffix, out var t) ? t.Areas : 0;
    /// <summary>Run the zone overlay now, until every kind is built (smoke tests; normally one kind per frame).</summary>
    public void ZoneOverlayNow()
    {
        for (var i = 0; i < 128 && (_zoStep < Math.Max(_zoOrder.Count, 1) || _zoArrayMesh == null); i++) { UpdateZoneOverlay(); if (_zoOrder.Count == 0) break; }
    }
}

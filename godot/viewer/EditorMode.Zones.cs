using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Drycastle.MapLoader;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// The Zones tool (GLOSSARY "Zone", "Zone kind", ADR 0022): what an area means in play, drawn over the ground without changing it.
/// A zone is a polygon of exactly one zone kind; zones overlap freely. To the user every zone is the same: counted, listed, selected,
/// reshaped, retyped and deleted alike. Where they come from is internal: hand-drawn ones are edits.json `zones` ({Z-NNNN: {kind,
/// polygon}}), and a kind with `where_ground` (parcels.json `zone_kinds`) also has one zone per ground area of those types
/// (parcels.json `ground_areas`). Editing a rule-derived zone detaches it lazily (<see cref="EditRuleZone"/>): a drawn zone with its
/// polygon (nothing for a delete) plus a cut (edits.json `zone_cuts`, {ZC-NNNN: {kind, polygon}}) that the export subtracts from the
/// kind's rule zone, all in one save = one undo step. The polygon gestures are the shared machinery of EditorMode.Shapes.cs. The page
/// lists the kinds (swatch = tint, label, how many zones), the zone form (change kind, delete) and the zones.
///
/// The overlay (M toggles it, remembered in editor_ui.cfg) draws every zone translucent in its kind's tint with a thin edge; the
/// selected kind is brightened and the others dimmed. Hovering names the zones at the cursor, clicking selects one. Zones have no
/// paint and no hot reload; the build turns them into candidate points. Nothing is saved by the overlay.
/// </summary>
public partial class EditorMode
{
    const float ZoneFill = 0.26f, ZoneFillSelected = 0.62f, ZoneFillDim = 0.09f;

    /// <summary>Rows of the zone list at most (a map has thousands of zones; picking a kind narrows the list).</summary>
    const int ZoneListCap = 300;

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
    readonly Dictionary<string, GroundMapType> _zoTypes = new();   // key: the kind

    public bool ZoneOverlayShown => _zoOn;
    public bool ZoneOverlayBuilt => _zoArrayMesh != null && _zoStep >= _zoOrder.Count && _zoOrder.Count > 0;

    // ---- the catalog ----

    ZoneKindInfo? ZoneKindOf(string name) => Map.ZoneKinds.FirstOrDefault(k => k.Name == name);
    public string ZoneLabel(string name) => ZoneKindOf(name)?.Label ?? name;
    Color ZoneTint(string name) => ZoneKindOf(name) is { } k && ParseColour(k.Tint) is { } c ? Color.FromHtml(c) : new Color(0.5f, 0.5f, 0.5f);

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
        if (z.FromRule) return EditRuleZone(z, kind, z.Points.ToList(), $"{z.Label} is now {ZoneLabel(kind)}");
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
        page.AddChild(Note("A zone marks what an area means in play (Bigfoot, deer, player spawn, ...). It never changes how the ground looks, and zones overlap freely. Pick a kind to see its zones; click any zone to select it, then drag its corners, change its kind or delete it. N draws a new one."));
        _zError = Note("", "Danger");
        page.AddChild(_zError);
        _zOverlayCheck = new CheckBox
        {
            Text = "Show zones on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = _zoOn,
            TooltipText = "Draw every zone on the terrain in its kind's tint (M). The selected kind is brightened, the others dimmed. Hover names the zones, click selects one.",
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
        BuildShapeList(page, ShapeKind.Zone, "All zones", "No zones to list. Pick a kind, press N, click corners on the ground, Enter creates the zone.");
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
        var kindsSig = string.Join("|", kinds.Select(k => k.Name + k.Tint + counts.GetValueOrDefault(k.Name))) + "#" + SelectedZoneKind;
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
                var b = new Button
                {
                    Text = $"{k.Label}   {n}", Icon = Swatch(k.Tint), Alignment = HorizontalAlignment.Left, ClipText = true,
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.All,
                    TooltipText = $"{k.Label} ({k.Group}): {n} zone{(n == 1 ? "" : "s")}. Select it to list only its zones, highlight them on the map and draw a new one of it (N).",
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
            return ($"{z.Label}   {ZoneLabel(z.Kind)}   {z.Points.Count} corners", tint, $"Select {z.Label} ({ZoneLabel(z.Kind)}, {z.Points.Count} corners, {z.AreaM2:F0} m2)");
        }, string.Concat(kinds.Select(k => k.Name + k.Label + k.Tint)),
            filter: SelectedZoneKind == null ? null : z => ((ZoneInfo)z).Kind == SelectedZoneKind,
            head: SelectedZoneKind == null ? null : $"{ZoneLabel(SelectedZoneKind)} zones", cap: ZoneListCap);

        if (sel != null && _zScrolledFor != sel.Id)
        {
            _zScrolledFor = sel.Id;
            Callable.From(() => _scroll.EnsureControlVisible(_su[ShapeKind.Zone].State)).CallDeferred();
        }
        else if (sel == null) _zScrolledFor = "";
    }

    // ---- the zones: drawn ones and the ones a rule gives ----

    /// <summary>The cuts of the file (edits.json `zone_cuts`): areas taken out of the rule zone of a kind. Internal: never listed as zones.</summary>
    sealed record ZoneCut(string Id, string Kind, List<(double x, double y)> Ring);

    /// <summary>One outline of a ground area, absolute (zones) and local (overlay): the part of a rule zone before any kind or cut applies.</summary>
    sealed record RulePart(string Area, string Part, string GroundKind, List<(double x, double y)> Abs, List<List<(double x, double y)>> HoleAbs);

    const char RuleSep = '~';
    /// <summary>A cut is grown by zones.CUT_BUFFER_M (0.5 m) in the export; an area whose outline lies inside a cut, within that, is the cut one (also when a rebuild moved it a little).</summary>
    const double CutTolM = 0.55;

    Obj? _zonesFor;
    object? _zoneKindsFor, _rulePartsFor;
    List<ZoneInfo>? _zoneCache;
    List<RulePart>? _ruleParts;
    readonly Dictionary<string, ZoneInfo> _zoneById = new();
    string _zoneSig = "";

    /// <summary>
    /// Every zone of the map: the drawn ones (edits.json `zones`) and those the rules give (a ground area of a kind's where_ground
    /// types, one outline each), without the cut ones. One concept to the user. Cached per file read and map.
    /// </summary>
    List<ZoneInfo> ZoneList()
    {
        var disk = Disk();
        var kinds = Map.ZoneKinds;
        if (ReferenceEquals(disk, _zonesFor) && ReferenceEquals(kinds, _zoneKindsFor) && _zoneCache != null) return _zoneCache;
        var list = new List<ZoneInfo>();
        var sig = new StringBuilder();
        _zonesFor = disk; _zoneKindsFor = kinds; _zoneCache = list;
        _zoneById.Clear();
        if (ShapesOf(disk, ShapeKind.Zone) is { } zo)
            foreach (var (id, e) in zo)
                if (e is Obj r && r.GetValueOrDefault("kind") is string kind && ParsePolygon(r.GetValueOrDefault("polygon")) is { } ring)
                {
                    list.Add(new ZoneInfo(id, kind, ring));
                    sig.Append(id).Append(':').Append(kind).Append(':').AppendJoin(',', ring.Select(c => $"{c.x:F1},{c.y:F1}")).Append(';');
                }
        var cuts = CutList(disk);
        foreach (var c in cuts) sig.Append("cut:").Append(c.Kind).Append(':').AppendJoin(',', c.Ring.Select(v => $"{v.x:F1},{v.y:F1}")).Append(';');
        if (Map.OriginEpsg5514 != null)
            foreach (var k in kinds.Where(k => k.WhereGround != null))
            {
                var mine = cuts.Where(c => c.Kind == k.Name).ToList();
                var n = 0;
                foreach (var part in RuleParts().Where(p => k.WhereGround!.Contains(p.GroundKind)))
                {
                    if (mine.Any(c => CutCovers(c, part))) continue;
                    list.Add(new ZoneInfo(part.Part + RuleSep + k.Name, k.Name, part.Abs, part.Part, part.HoleAbs.Count > 0 ? part.HoleAbs : null));
                    n++;
                }
                sig.Append(k.Name).Append('=').Append(n).Append(';');
            }
        _zoneSig = sig.ToString();
        foreach (var z in list) _zoneById[z.Id] = z;
        return list;
    }

    /// <summary>The outlines of the last build's ground areas (parcels.json `ground_areas`), absolute; a multi-part area is `A-0012.1`, `A-0012.2`, ... (as the MCP server names them).</summary>
    List<RulePart> RuleParts()
    {
        var kinds = Map.ZoneKinds;
        if (_ruleParts != null && ReferenceEquals(_rulePartsFor, kinds)) return _ruleParts;
        _rulePartsFor = kinds;
        var parts = new List<RulePart>();
        static Vector2[] Open(Vector2[] r) => r.Length > 1 && r[0] == r[^1] ? r[..^1] : r;
        List<(double x, double y)>? Clean(Vector2[] ring)
        {
            var abs = new List<(double x, double y)>();
            foreach (var v in ring) { var p = ToAbsolute(v); if (abs.Count == 0 || abs[^1] != p) abs.Add(p); }
            if (abs.Count > 1 && abs[0] == abs[^1]) abs.RemoveAt(abs.Count - 1);
            return abs.Count >= 3 ? abs : null;
        }
        foreach (var a in Map.GroundAreas)
        {
            var rings = a.Rings.Select(Open).Where(r => r.Length >= 3).ToList();
            var outers = new List<Vector2[]>(); var holes = new List<Vector2[]>();
            foreach (var ring in rings)
                (rings.Count(o => !ReferenceEquals(o, ring) && PointInRing(o, ring[0])) % 2 == 0 ? outers : holes).Add(ring);
            var of = holes.GroupBy(h => outers.Where(o => PointInRing(o, h[0])).OrderBy(o => Mathf.Abs(ParcelInfo.RingArea(o))).FirstOrDefault())
                .Where(g => g.Key != null).ToDictionary(g => g.Key!, g => g.ToList());
            var i = 0;
            foreach (var o in outers)
            {
                i++;
                if (Clean(o) is not { } abs) continue;
                var h = of.GetValueOrDefault(o)?.Select(Clean).Where(r => r != null).Select(r => r!).ToList() ?? new List<List<(double x, double y)>>();
                parts.Add(new RulePart(a.Id, outers.Count == 1 ? a.Id : $"{a.Id}.{i}", a.Kind, abs, h));
            }
        }
        return _ruleParts = parts;
    }

    static List<ZoneCut> CutList(Obj disk)
    {
        var list = new List<ZoneCut>();
        if (disk.GetValueOrDefault("zone_cuts") is Obj co)
            foreach (var (id, e) in co)
                if (e is Obj r && r.GetValueOrDefault("kind") is string kind && ParsePolygon(r.GetValueOrDefault("polygon")) is { Count: >= 3 } ring)
                    list.Add(new ZoneCut(id, kind, ring));
        return list;
    }

    static bool CutCovers(ZoneCut c, RulePart p)
    {
        foreach (var v in p.Abs)
        {
            if (PointInRing(c.Ring, v.x, v.y)) continue;
            var near = false;
            for (var i = 0; i < c.Ring.Count && !near; i++) near = SegmentDistance(c.Ring[i], c.Ring[(i + 1) % c.Ring.Count], v) <= CutTolM;
            if (!near) return false;
        }
        return true;
    }

    static double SegmentDistance((double x, double y) a, (double x, double y) b, (double x, double y) p)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.x - (a.x + t * dx), 2) + Math.Pow(p.y - (a.y + t * dy), 2));
    }

    /// <summary>
    /// Edit a zone that comes from a rule (move a corner, change its kind, delete it) as one save and one undo step: a cut of its
    /// outline is recorded in its kind (edits.json `zone_cuts`) and, unless it is a delete (<paramref name="ring"/> null), a drawn zone
    /// of <paramref name="kind"/> (default: its own) with <paramref name="ring"/> is written. The new zone is selected.
    /// </summary>
    bool EditRuleZone(ZoneInfo z, string? kind, List<(double x, double y)>? ring, string message)
    {
        if (ObjectEdits.RingProblemCode(z.Points) is var code && code != ObjectEdits.RingOk) { Say($"{z.Label} cannot be edited: its outline {RingMessage(code)}", true); return false; }
        string? newId = null;
        var ok = Mutate(root =>
        {
            if (root.GetValueOrDefault("zone_cuts") is not Obj cuts) root["zone_cuts"] = cuts = EditsFile.NewObject();
            var cut = EditsFile.NewObject();
            cut["kind"] = z.Kind;
            cut["polygon"] = PolygonValue(z.Points);
            cuts[NextId(root, "zone_cuts", "ZC-")] = cut;
            if (ring != null)
            {
                if (ShapesOf(root, ShapeKind.Zone) is not { } zones) root[KeyOf(ShapeKind.Zone)] = zones = EditsFile.NewObject();
                newId = NextShapeId(root, ShapeKind.Zone);
                var e = EditsFile.NewObject();
                e["kind"] = kind ?? z.Kind;
                e["polygon"] = PolygonValue(ring);
                zones[newId] = e;
            }
            return (true, message);
        });
        if (ok && newId != null) { var corner = _activeCorner; SelectShape(ShapeKind.Zone, newId); _activeCorner = corner; }
        return ok;
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

    /// <summary>Index the zones per kind for the overlay. Geometry changes only with a zone, a cut or a new map; tints and selection only touch the materials.</summary>
    void EnsureZoneIndex()
    {
        var disk = Disk();
        var kinds = Map.ZoneKinds;
        if (_zoIndexed && ReferenceEquals(disk, _zoDisk) && ReferenceEquals(kinds, _zoKindsFor)) return;
        _zoIndexed = true; _zoDisk = disk; _zoKindsFor = kinds;
        var zones = ZoneList();
        var sig = Map.OriginEpsg5514 == null ? "" : _zoneSig;
        if (sig == _zoSig && _zoArrayMesh != null) return;
        _zoSig = sig;

        var prev = _zoTypes.ToDictionary(p => p.Key, p => (p.Value.Fill, p.Value.Edge));
        _zoTypes.Clear();
        if (Map.OriginEpsg5514 != null)
            foreach (var z in zones)
            {
                var rings = new List<Vector2[]> { z.Points.Select(c => FromAbsolute(c.x, c.y)).ToArray() };
                if (z.Holes != null) rings.AddRange(z.Holes.Select(h => h.Select(c => FromAbsolute(c.x, c.y)).ToArray()));
                if (!_zoTypes.TryGetValue(z.Kind, out var t)) _zoTypes[z.Kind] = t = new GroundMapType { Name = z.Kind };
                t.Areas++;
                SplitRings(rings, t, out var area);
                t.AreaM2 += area;
            }
        _zoStep = 0; _zoBudget.Count = 0; _zoBudget.Truncated = false; _zoArrayMesh = null; if (_zoMesh != null) _zoMesh.Mesh = null;
        foreach (var (name, t) in _zoTypes) if (prev.TryGetValue(name, out var m)) { t.Fill = m.Fill; t.Edge = m.Edge; }
        var order = kinds.Select(k => k.Name).ToList();
        _zoOrder = _zoTypes.Keys.OrderBy(k => order.IndexOf(k)).ToList();
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
            var kind = _zoOrder[_zoStep];
            BuildAreaSurfaces(_zoArrayMesh, _zoTypes[kind], ZoneTint(kind), ZoneFill, 6, _zoBudget);
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
        var n = ZoneList().Count;
        return $"{n} zone{(n == 1 ? "" : "s")} on the map" + (_zoBudget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    void ApplyZoneMaterials()
    {
        var sel = SelectedZoneKind;
        foreach (var (kind, t) in _zoTypes)
        {
            var colour = ZoneTint(kind);
            var isSel = sel != null && kind == sel;
            var dim = sel != null && !isSel;
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, isSel ? ZoneFillSelected : dim ? ZoneFillDim : ZoneFill);
            if (t.Edge != null) t.Edge.AlbedoColor = isSel ? new Color(colour.Lightened(0.55f), 0.95f) : new Color(colour, dim ? GroundMapEdgeDim : 0.8f);
        }
    }

    // ---- pointing ----

    /// <summary>Hover in the Zones tool: the zones at the cursor, as a label near it.</summary>
    string? ZoneHoverText(Vector3 point)
    {
        var (x, y) = RawAbs(point);
        var hits = ZoneList().Where(z => z.Contains(x, y)).OrderByDescending(z => ShapeNumber(z.Id)).ThenBy(z => KindOrder(z.Kind)).ToList();
        if (hits.Count == 0) return "no zone here";
        var lines = hits.Take(5).Select(z => $"{z.Label}  {ZoneLabel(z.Kind)} zone").ToList();
        if (hits.Count > 5) lines.Add($"+{hits.Count - 5} more");
        return string.Join("\n", lines) + "\nclick to select";
    }

    int KindOrder(string kind) { var i = Map.ZoneKinds.ToList().FindIndex(k => k.Name == kind); return i < 0 ? int.MaxValue : i; }

    /// <summary>The zone to select at a point: where zones overlap the selected kind's win, then a drawn one over the others, the higher ID, the kind earlier in the catalog.</summary>
    string? ZoneAtPoint(double x, double y)
    {
        var hits = ZoneList().Where(z => z.Contains(x, y)).ToList();
        if (hits.Count == 0) return null;
        return hits.OrderBy(z => z.Kind == SelectedZoneKind ? 1 : 0).ThenBy(z => ShapeNumber(z.Id)).ThenByDescending(z => KindOrder(z.Kind)).ThenBy(z => z.Id, StringComparer.Ordinal).Last().Id;
    }

    // test hooks
    public int ZoneKindRowCount => _zKindRows.Count;
    public string ZoneKindRowText(string kind) => _zKindRows.TryGetValue(kind, out var b) ? b.Text : "";
    public bool ZoneErrorShown => _zError.Visible;
    public string ZoneOverlayText => _zOverlayNote.Text;
    public void PressZoneKindRow(string kind) { if (_zKindRows.TryGetValue(kind, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
    public string ZoneKindShown => _zKindOpt.Selected >= 0 && _zKindOpt.Selected < Map.ZoneKinds.Count ? Map.ZoneKinds[_zKindOpt.Selected].Name : "";
    public int ZoneCount(string kind) => ZoneList().Count(z => z.Kind == kind);
    public int ZoneCutCount => CutList(Disk()).Count;
    /// <summary>Run the zone overlay now, until every kind is built (smoke tests; normally one kind per frame).</summary>
    public void ZoneOverlayNow()
    {
        for (var i = 0; i < 128 && (_zoStep < Math.Max(_zoOrder.Count, 1) || _zoArrayMesh == null); i++) { UpdateZoneOverlay(); if (_zoOrder.Count == 0) break; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Drycastle.MapLoader;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using ShapeState = Drycastle.ShooterCore.EditorShapeState;

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
/// The overlay (M toggles it, remembered in editor_ui.cfg) draws every zone translucent in its kind's tint with a thin edge. A
/// selected kind is shown alone (also with the overlay off): the other kinds are not drawn and not pointed at, and its zones get
/// moving bands of its tint and white, so they read on ground of the same colour (the bands stand still with reduce motion), and
/// are drawn a second time, faint and without a depth test, so they still show where buildings, trees and hills cover them.
/// Hovering names the zones at the cursor, clicking selects one. Zones have no paint and no hot reload; the build turns them into
/// candidate points. Nothing is saved by the overlay.
///
/// Mass delete: zones are marked (the tick of a list row, Ctrl+click on the map, X for the selected one, "Mark all") and
/// outlined in the danger colour; "Delete N marked zones" (Shift+Del) asks, then removes them all in one save = one undo step
/// (<see cref="DeleteZones"/>). Marks are not saved and live until deleted, unmarked or the editor closes.
/// </summary>
public partial class EditorMode
{
    // The fill is striped: a full band of the kind's colour, then a faint one (StripeTexture), so a zone reads at a glance on any ground.
    const float ZoneFill = 0.55f, ZoneFillSelected = 0.85f;
    const float ZoneFillGhost = 0.3f, ZoneEdgeGhost = 0.5f; // the selected kind where an obstacle covers it
    const float StripeTileM = 24f; // one texture tile on the ground: bands 4 m wide, 8.5 m apart (period)
    const float ZoneMarchTilesPerS = 0.125f; // the bands of the selected kind move 3 m/s
    const float ZoneMarchWhite = 0.7f;       // alpha of the white band between two bands of the tint
    public const string OutlineZoneMarked = "outline_zone_marked";

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
    /// <summary>Zones are drawn and pointed at: the overlay is on, or a kind is selected (it is then shown alone, also with the overlay off).</summary>
    public bool ZonesDrawn => _zoOn || SelectedZoneKind != null;
    /// <summary>A zone that can be hovered and clicked: any when no kind is selected, else only the selected kind's (the others are not drawn).</summary>
    bool ZonePickable(ZoneInfo z) => SelectedZoneKind == null || z.Kind == SelectedZoneKind;
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
            case Key.X when SelectedZoneId != null && ShapeMode == ShapeState.Selected: ToggleZoneMark(SelectedZoneId); return true;
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
            ? "zones: Tab or click a kind to show its zones alone, click a zone to select it, Ctrl+click marks it for deletion, M overlay, N draws a zone of the selected kind, Ctrl+P commands, Esc leaves the tool"
            : $"{ZoneLabel(SelectedZoneKind)} shown alone: click a zone to select it, Ctrl+click marks it for deletion{(_zMarked.Count > 0 ? $", Shift+Del deletes the {_zMarked.Count} marked" : "")}, N draws one, Esc shows all kinds");

    // ---- the page ----

    void BuildZonesPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Zones", ThemeTypeVariation = "Header" });
        page.AddChild(Note("A zone marks what an area means in play (Bigfoot, deer, player spawn, ...). It never changes how the ground looks, and zones overlap freely. Pick a kind to show its zones alone, with moving bands; click any zone to select it, then drag its corners, change its kind or delete it. N draws a new one."));
        _zError = Note("", "Danger");
        page.AddChild(_zError);
        _zOverlayCheck = new CheckBox
        {
            Text = "Show zones on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = _zoOn,
            TooltipText = "Draw every zone on the terrain in its kind's tint (M). A selected kind is shown alone, also when this is off. Hover names the zones, click selects one.",
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
        BuildZoneMarks(page);
        BuildShapeList(page, ShapeKind.Zone, "All zones", "No zones to list. Pick a kind, press N, click corners on the ground, Enter creates the zone.");
        page.AddChild(Note("Zones overlap freely and add up. Esc steps back: cancel drawing, deselect the zone, deselect the kind (all kinds show again), leave the tool."));
    }

    void RefreshZonesPanel()
    {
        if (Tool != EditorTool.Zones) return;
        var zones = ZoneList();
        if (_restoreZoneKind != null) { if (ZoneKindOf(_restoreZoneKind) != null) SelectedZoneKind = _restoreZoneKind; _restoreZoneKind = null; }
        if (_restoreZone != null) { if (zones.Any(z => z.Id == _restoreZone)) SelectedZoneId = _restoreZone; _restoreZone = null; }
        if (SelectedZoneId != null && zones.All(z => z.Id != SelectedZoneId)) { SelectedZoneId = null; EndCornerGesture(); }
        if (SelectedZoneKind != null && ZoneKindOf(SelectedZoneKind) == null) SelectedZoneKind = null;
        if (_zMarked.RemoveWhere(id => !_zoneById.ContainsKey(id)) > 0) _zMarkVer++;
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
                    TooltipText = $"{k.Label} ({k.Group}): {n} zone{(n == 1 ? "" : "s")}. Select it to show its zones alone on the map (moving bands, the other kinds hidden), list only them and draw a new one of it (N).",
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
            head: SelectedZoneKind == null ? null : $"{ZoneLabel(SelectedZoneKind)} zones", cap: ZoneListCap,
            marks: _zMarked, onMark: (id, on) => MarkZones(new[] { id }, on, false));
        RefreshZoneMarks();

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
        if (_zoMesh != null) _zoMesh.Visible = on && SelectedZoneKind == null && Tool == EditorTool.Zones;
        _panelDirty = true;
        Say(on ? "zone overlay on: every zone in its kind's tint (M hides it)"
            : SelectedZoneKind != null ? $"zone overlay hidden; {ZoneLabel(SelectedZoneKind)} stays shown while it is selected (M shows all again)" : "zone overlay hidden (M shows it)");
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
        var show = Tool == EditorTool.Zones && ZonesDrawn && Map.OriginEpsg5514 != null;
        var sel = SelectedZoneKind;
        if (_zoMesh != null) _zoMesh.Visible = show && sel == null;
        if (_zoSelMesh != null) _zoSelMesh.Visible = show && sel != null;
        UpdateZoneMarkOutline();
        if (Tool != EditorTool.Zones) return;
        EnsureZoneIndex();
        if (_zOverlayNote != null) _zOverlayNote.Text = ZoneOverlayNote();
        if (!show || _zoOrder.Count == 0) return;
        if (sel != null) { UpdateSelectedKindMesh(sel); return; } // shown alone, in its own mesh; the mesh of all kinds waits
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
            BuildAreaSurfaces(_zoArrayMesh, _zoTypes[kind], ZoneTint(kind), ZoneFill, 6, _zoBudget, StripeTexture());
            _zoMatSig = ""; // new materials: colour them again
            _zoStep++;
            _zoMesh.Mesh = _zoArrayMesh;
        }
        ApplyZoneMaterials();
    }

    string ZoneOverlayNote()
    {
        if (Map.ZoneKinds.Count == 0) return "No zone kinds in this map's parcels.json. Build it again (make map), then reopen the editor.";
        if (!_zoOn && SelectedZoneKind == null) return "The zone overlay is off. Pick a kind to show its zones alone.";
        if (SelectedZoneKind is { } k)
            return $"Showing only {ZoneLabel(k)} ({ZoneCount(k)}); Esc shows all kinds{(_zoOn ? "" : " (the overlay is off: M)")}." + (_zoSelBudget.Truncated ? " Too many to draw: the smallest triangles are left out." : "");
        if (_zoStep < _zoOrder.Count) return $"Drawing zones {_zoStep + 1}/{_zoOrder.Count}...";
        var n = ZoneList().Count;
        return $"{n} zone{(n == 1 ? "" : "s")} on the map" + (_zoBudget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    static ImageTexture? _stripes;

    /// <summary>The stripe tile: diagonal bands, alternating full and faint (alpha 1 and 0.15), seamless. Made once.</summary>
    static ImageTexture StripeTexture()
    {
        if (_stripes != null) return _stripes;
        const int n = 64, period = 32; // 2 periods per tile; the bands run at 45 degrees
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                img.SetPixel(x, y, new Color(1, 1, 1, (x + y) % period < period / 2 ? 1f : 0.15f));
        img.GenerateMipmaps();
        return _stripes = ImageTexture.CreateFromImage(img);
    }

    static readonly Dictionary<Color, ImageTexture> _marchTextures = new();

    /// <summary>The tile of a selected kind: the same bands as <see cref="StripeTexture"/>, the tint alternating with white, so the zones read on ground of their own colour. One per tint.</summary>
    static ImageTexture MarchTexture(Color tint)
    {
        if (_marchTextures.TryGetValue(tint, out var tex)) return tex;
        const int n = 64, period = 32;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        var full = new Color(tint, 1f); var white = new Color(1, 1, 1, ZoneMarchWhite);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                img.SetPixel(x, y, (x + y) % period < period / 2 ? full : white);
        img.GenerateMipmaps();
        return _marchTextures[tint] = ImageTexture.CreateFromImage(img);
    }

    string _zoMatSig = "";

    /// <summary>The mesh of all kinds (no kind selected): every kind in its tint.</summary>
    void ApplyZoneMaterials()
    {
        var sig = _zoStep + "#" + string.Join("|", Map.ZoneKinds.Select(k => k.Name + k.Tint));
        if (sig == _zoMatSig) return;
        _zoMatSig = sig;
        foreach (var (kind, t) in _zoTypes)
        {
            var colour = ZoneTint(kind);
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, ZoneFill);
            if (t.Edge != null) t.Edge.AlbedoColor = new Color(colour, 0.8f);
        }
    }

    // The selected kind has a mesh of its own, with its own triangle budget: the mesh of all kinds shares one budget in catalog
    // order, so on a big map the later kinds (the wildlife ones cover the same forest and fields many times) are cut short there.
    MeshInstance3D? _zoSelMesh;
    string _zoSelSig = "";
    StandardMaterial3D? _zoSelFill;
    readonly TriangleBudget _zoSelBudget = new() { Max = 700000 }; // one kind over every field and forest of the village map is about 250 000

    /// <summary>
    /// A kind is selected: only it is drawn, in bands of its tint and white that move (one material offset per frame; still with
    /// reduce motion), and a second, faint pass without a depth test shows it where buildings, trees and hills cover it.
    /// Built when the kind, its tint or the zones change.
    /// </summary>
    void UpdateSelectedKindMesh(string kind)
    {
        var colour = ZoneTint(kind);
        var sig = kind + "#" + colour.ToHtml() + "#" + _zoSig;
        if (sig != _zoSelSig)
        {
            _zoSelSig = sig;
            _zoSelFill = null;
            _zoSelBudget.Count = 0; _zoSelBudget.Truncated = false;
            if (_zoSelMesh == null)
            {
                _zoSelMesh = new MeshInstance3D { Name = "ZoneOverlaySelected", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                OutlineRoot.AddChild(_zoSelMesh);
            }
            var mesh = new ArrayMesh();
            if (_zoTypes.GetValueOrDefault(kind) is { } all)
            {
                var t = new GroundMapType { Name = kind }; // a copy: the materials of the mesh of all kinds stay its own
                t.Outers.AddRange(all.Outers); t.Holes.AddRange(all.Holes);
                BuildAreaSurfaces(mesh, t, colour, ZoneFillSelected, 6, _zoSelBudget, MarchTexture(colour));
                if (t.Fill != null) { t.Fill.AlbedoColor = new Color(1, 1, 1, ZoneFillSelected); t.Fill.NextPass = GhostFill(colour); }
                if (t.Edge != null) { t.Edge.AlbedoColor = new Color(colour.Lightened(0.55f), 0.95f); t.Edge.NextPass = GhostEdge(colour); }
                _zoSelFill = t.Fill;
            }
            _zoSelMesh.Mesh = mesh;
            _zoSelMesh.Visible = true;
        }
        if (_zoSelFill != null && !WaterSurfaces.ReduceMotion())
        {
            _zoSelFill.Uv1Offset = new Vector3((float)(Time.GetTicksMsec() / 1000.0 * ZoneMarchTilesPerS % 0.5), 0, 0); // 0.5 tile = one period: seamless
            if (_zoGhostFill != null) _zoGhostFill.Uv1Offset = _zoSelFill.Uv1Offset;
        }
    }

    StandardMaterial3D? _zoGhostFill, _zoGhostEdge;

    /// <summary>The second pass of the selected kind's fill: the same bands, faint and without a depth test, so the part behind an obstacle still shows.</summary>
    StandardMaterial3D GhostFill(Color tint)
    {
        if (_zoGhostFill == null)
        {
            _zoGhostFill = GroundMapMaterial(5);
            _zoGhostFill.NoDepthTest = true;
            _zoGhostFill.Uv1Triplanar = true;
            _zoGhostFill.Uv1WorldTriplanar = true;
            _zoGhostFill.Uv1Scale = Vector3.One / StripeTileM;
        }
        _zoGhostFill.AlbedoTexture = MarchTexture(tint);
        _zoGhostFill.AlbedoColor = new Color(1, 1, 1, ZoneFillGhost);
        return _zoGhostFill;
    }

    StandardMaterial3D GhostEdge(Color tint)
    {
        _zoGhostEdge ??= GroundMapMaterial(5);
        _zoGhostEdge.NoDepthTest = true;
        _zoGhostEdge.AlbedoColor = new Color(tint.Lightened(0.55f), ZoneEdgeGhost);
        return _zoGhostEdge;
    }

    // ---- marking zones and deleting the marked ones ----

    readonly HashSet<string> _zMarked = new();
    int _zMarkVer;
    object? _zMarkOutlineFor;
    Label _zMarkHead = null!;
    Button _zMarkAll = null!, _zMarkNone = null!, _zMarkDelete = null!;

    public int MarkedZoneCount => _zMarked.Count;
    public bool ZoneMarked(string id) => _zMarked.Contains(id);

    /// <summary>The zones "Mark all" marks: those of the selected kind, or every zone when none is (not capped as the list is).</summary>
    List<ZoneInfo> ListedZones() => SelectedZoneKind is { } k ? ZoneList().Where(z => z.Kind == k).ToList() : ZoneList();

    void BuildZoneMarks(VBoxContainer page)
    {
        page.AddChild(new HSeparator());
        _zMarkHead = new Label { ThemeTypeVariation = "Header" };
        page.AddChild(_zMarkHead);
        page.AddChild(Note("Delete many zones at once: tick them in the list below, Ctrl+click them on the map, or press X for the selected one. Marked zones are outlined in red on the map."));
        var row = new HBoxContainer();
        _zMarkAll = FocusBtn("", MarkListedZones);
        _zMarkAll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _zMarkAll.ClipText = true;
        _zMarkNone = FocusBtn("Unmark all", ClearZoneMarks);
        _zMarkNone.TooltipText = "Take every mark back. Nothing is deleted.";
        row.AddChild(_zMarkAll); row.AddChild(_zMarkNone);
        page.AddChild(row);
        _zMarkDelete = FocusBtn("", () => RequestDeleteMarkedZones(), "DangerButton");
        page.AddChild(_zMarkDelete);
    }

    void RefreshZoneMarks()
    {
        var n = _zMarked.Count;
        var listed = ListedZones();
        var unmarked = listed.Count(z => !_zMarked.Contains(z.Id));
        _zMarkHead.Text = $"Marked for deletion ({n})";
        _zMarkAll.Text = SelectedZoneKind is { } k ? $"Mark all {ZoneLabel(k)} ({listed.Count})" : $"Mark all zones ({listed.Count})";
        _zMarkAll.Disabled = unmarked == 0;
        _zMarkAll.TooltipText = listed.Count == 0 ? "There are no zones to mark." : unmarked == 0 ? "All of them are marked already."
            : SelectedZoneKind != null ? "Mark every zone of the selected kind for deletion. Nothing is deleted yet." : "Mark every zone of the map for deletion. Nothing is deleted yet. Pick a kind first to mark only its zones.";
        _zMarkNone.Disabled = n == 0;
        _zMarkDelete.Text = n == 0 ? "Delete marked zones  (Shift+Del)" : $"Delete {n} marked zone{(n == 1 ? "" : "s")}  (Shift+Del)";
        _zMarkDelete.Disabled = n == 0;
        _zMarkDelete.TooltipText = n == 0 ? "No zone is marked. Tick zones in the list or Ctrl+click them on the map first." : "Delete every marked zone in one step (asks first; one Ctrl+Z brings them all back)";
    }

    /// <summary>Mark or unmark zones for the mass delete. Nothing is saved.</summary>
    public void MarkZones(IEnumerable<string> ids, bool on, bool say = true)
    {
        ZoneList();
        var n = 0;
        foreach (var id in ids) if (on ? _zoneById.ContainsKey(id) && _zMarked.Add(id) : _zMarked.Remove(id)) n++;
        if (n == 0) return;
        _zMarkVer++; _panelDirty = true;
        if (say) Say($"{n} zone{(n == 1 ? "" : "s")} {(on ? "marked for deletion" : "unmarked")}: {_zMarked.Count} marked{(_zMarked.Count > 0 ? " (Shift+Del deletes them)" : "")}");
    }

    public bool ToggleZoneMark(string id)
    {
        if (Zone(id) is not { } z) { Say($"zone {id} does not exist", true); return false; }
        var on = !_zMarked.Contains(id);
        MarkZones(new[] { id }, on, false);
        Say($"{z.Label} {(on ? "marked for deletion" : "unmarked")}: {_zMarked.Count} marked{(_zMarked.Count > 0 ? " (Shift+Del deletes them)" : "")}");
        return true;
    }

    public void MarkListedZones() => MarkZones(ListedZones().Select(z => z.Id).ToList(), true);

    public void ClearZoneMarks() => MarkZones(_zMarked.ToList(), false);

    /// <summary>Shift+Del, the button: ask first, naming how many zones of which kinds go; accepting deletes them (one undo brings all back).</summary>
    public bool RequestDeleteMarkedZones()
    {
        ZoneList();
        var zones = _zMarked.Select(id => _zoneById.GetValueOrDefault(id)).Where(z => z != null).Select(z => z!).ToList();
        if (zones.Count == 0) { Say("no zone is marked: tick zones in the list or Ctrl+click them on the map", true); return false; }
        var ids = zones.Select(z => z.Id).ToList();
        var kinds = string.Join(", ", zones.GroupBy(z => z.Kind).OrderBy(g => KindOrder(g.Key)).Select(g => $"{g.Count()} {ZoneLabel(g.Key)}"));
        var names = string.Join(", ", zones.OrderBy(z => z.Label, StringComparer.Ordinal).Take(8).Select(z => z.Label)) + (zones.Count > 8 ? $" and {zones.Count - 8} more" : "");
        var s = zones.Count == 1 ? "" : "s";
        ShowConfirm($"Delete {zones.Count} marked zone{s}?", $"{kinds} ({zones.Sum(z => z.AreaM2):F0} m2 in all): {names}. They are removed; the ground is not changed. One undo (Ctrl+Z) brings them all back.",
            $"Delete {zones.Count} zone{s}", () => DeleteZones(ids));
        return true;
    }

    /// <summary>
    /// Delete several zones as one save and one undo step: a drawn one leaves edits.json `zones`, one that comes from a rule gets a
    /// cut (as <see cref="EditRuleZone"/>). A rule zone whose outline cannot be cut is kept and named. The deleted ones lose their mark.
    /// </summary>
    public bool DeleteZones(IReadOnlyCollection<string> ids)
    {
        ZoneList();
        var zones = ids.Distinct().Select(id => _zoneById.GetValueOrDefault(id)).Where(z => z != null).Select(z => z!).ToList();
        var kept = zones.Where(z => z.FromRule && ObjectEdits.RingProblemCode(z.Points) != ObjectEdits.RingOk).ToList();
        var todo = zones.Except(kept).ToList();
        if (todo.Count == 0) { Say(kept.Count > 0 ? $"nothing deleted: the outline of {kept[0].Label}{(kept.Count > 1 ? $" and {kept.Count - 1} more" : "")} cannot be cut" : "nothing to delete: those zones no longer exist", true); return false; }
        var ok = Mutate(root =>
        {
            var drawn = ShapesOf(root, ShapeKind.Zone);
            var n = 0;
            foreach (var z in todo)
            {
                if (!z.FromRule) { if (drawn != null && drawn.Remove(z.Id)) n++; continue; }
                if (root.GetValueOrDefault("zone_cuts") is not Obj cuts) root["zone_cuts"] = cuts = EditsFile.NewObject();
                var cut = EditsFile.NewObject();
                cut["kind"] = z.Kind;
                cut["polygon"] = PolygonValue(z.Points);
                cuts[NextId(root, "zone_cuts", "ZC-")] = cut;
                n++;
            }
            if (drawn is { Count: 0 }) root.Remove(KeyOf(ShapeKind.Zone));
            if (n == 0) return (false, "nothing deleted: those zones no longer exist");
            return (true, $"deleted {n} zone{(n == 1 ? "" : "s")} (Ctrl+Z undoes)" + (kept.Count > 0 ? $"; {kept.Count} kept, their outline cannot be cut" : ""));
        });
        if (!ok) return false;
        MarkZones(todo.Select(z => z.Id).ToList(), false, false);
        if (SelectedZoneId != null && todo.Any(z => z.Id == SelectedZoneId)) SelectShape(ShapeKind.Zone, null);
        return true;
    }

    /// <summary>The outline of every marked zone in the danger colour, above the overlay; rebuilt when the marks or the file change.</summary>
    void UpdateZoneMarkOutline()
    {
        if (Tool != EditorTool.Zones || _zMarked.Count == 0 || Map.OriginEpsg5514 == null)
        {
            if (_zMarkOutlineFor != null) { _zMarkOutlineFor = null; SetOutline(OutlineZoneMarked, new(), EditorTheme.Danger, 0, 0); }
            return;
        }
        var key = (Disk(), _zMarkVer);
        if (_zMarkOutlineFor is ValueTuple<Obj, int> was && ReferenceEquals(was.Item1, key.Item1) && was.Item2 == key.Item2) return;
        _zMarkOutlineFor = key;
        ZoneList();
        var segs = new List<(Vector2, Vector2)>();
        foreach (var id in _zMarked) if (_zoneById.GetValueOrDefault(id) is { } z) segs.AddRange(RingSegs(z.Points, true));
        SetOutline(OutlineZoneMarked, segs, EditorTheme.Danger, GroundMapLift + 0.3f, 0.9f);
        if (_outlines[OutlineZoneMarked].MaterialOverride is StandardMaterial3D m) { m.NoDepthTest = true; m.RenderPriority = 9; } // seen through obstacles, over the zone fill
    }

    // ---- pointing ----

    /// <summary>Hover in the Zones tool: the zones at the cursor, as a label near it.</summary>
    string? ZoneHoverText(Vector3 point)
    {
        var (x, y) = RawAbs(point);
        var hits = ZoneList().Where(z => ZonePickable(z) && z.Contains(x, y)).OrderByDescending(z => ShapeNumber(z.Id)).ThenBy(z => KindOrder(z.Kind)).ToList();
        if (hits.Count == 0) return SelectedZoneKind is { } k ? $"no {ZoneLabel(k)} zone here" : "no zone here";
        var lines = hits.Take(5).Select(z => $"{z.Label}  {ZoneLabel(z.Kind)} zone{(_zMarked.Contains(z.Id) ? "  (marked for deletion)" : "")}").ToList();
        if (hits.Count > 5) lines.Add($"+{hits.Count - 5} more");
        return string.Join("\n", lines) + "\nclick to select, Ctrl+click to mark for deletion";
    }

    int KindOrder(string kind) { var i = Map.ZoneKinds.ToList().FindIndex(k => k.Name == kind); return i < 0 ? int.MaxValue : i; }

    /// <summary>The zone to select at a point (of the selected kind only, when one is: the others are not drawn): where zones overlap a drawn one wins over the others, then the higher ID, the kind earlier in the catalog.</summary>
    string? ZoneAtPoint(double x, double y)
    {
        var hits = ZoneList().Where(z => ZonePickable(z) && z.Contains(x, y)).ToList();
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
    public string ZoneMarkDeleteText => _zMarkDelete.Text;
    public bool ZoneMarkDeleteEnabled => !_zMarkDelete.Disabled;
    /// <summary>Alpha of a kind's fill in the overlay (0: not drawn, a kind other than the selected one); -1 when it has no surface.</summary>
    public float ZoneFillAlpha(string kind) => SelectedZoneKind is { } sel
        ? (kind == sel && _zoSelMesh is { Visible: true } && _zoSelFill != null ? _zoSelFill.AlbedoColor.A : 0f)
        : _zoTypes.GetValueOrDefault(kind)?.Fill is { } m && _zoMesh is { Visible: true } ? m.AlbedoColor.A : -1f;
    /// <summary>Run the zone overlay now, until every kind is built (smoke tests; normally one kind per frame).</summary>
    public void ZoneOverlayNow()
    {
        for (var i = 0; i < 128 && (_zoStep < Math.Max(_zoOrder.Count, 1) || _zoArrayMesh == null); i++) { UpdateZoneOverlay(); if (_zoOrder.Count == 0 || SelectedZoneKind != null) break; }
    }
}

using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Zones tool (GLOSSARY "Zone", "Zone kind", ADR 0022): what an area means in play, drawn over the ground without changing it.
/// A zone is a polygon of exactly one zone kind; zones overlap freely. To the user every zone is the same: counted, listed, selected,
/// reshaped, retyped and deleted alike. Where they come from is internal: hand-drawn ones are edits.json `zones` ({Z-NNNN: {kind,
/// polygon}}), and a kind with `where_ground` (parcels.json `zone_kinds`) also has one zone per ground area of those types
/// (parcels.json `ground_areas`). Editing a rule-derived zone detaches it lazily (<see cref="EditRuleZone"/>): a drawn zone with its
/// polygon (nothing for a delete) plus a cut (edits.json `zone_cuts`, {ZC-NNNN: {kind, polygon}}, see <see cref="ZoneCut"/>) that the
/// export subtracts from the kind's rule zone, all in one save = one undo step. The polygon gestures are the shared shape editor
/// (<see cref="ShapeEditor"/>). A kind can be selected: its zones are then shown alone, listed alone and drawn.
/// This class holds the zone list, the selected kind, the keys and the hover; the page is <see cref="ZonesPanel"/>, the overlay on the
/// terrain is <see cref="ZoneOverlay"/>, the marks and the mass delete are <see cref="ZoneMarks"/>.
/// </summary>
public sealed class ZonesTool : EditorPart
{
    const char RuleSep = '~';

    public ZonesPanel Panel { get; }
    public ZoneOverlay Overlay { get; }
    public ZoneMarks Marks { get; }

    public string? SelectedKind { get; private set; }

    string? _restoreKind;
    Obj? _zonesFor;
    object? _kindsFor, _rulePartsFor;
    List<ZoneInfo>? _cache;
    List<RulePart>? _ruleParts;
    readonly Dictionary<string, ZoneInfo> _byId = new();

    public ZonesTool(IEditorHost host) : base(host)
    {
        Marks = new ZoneMarks(host, this);
        Panel = new ZonesPanel(host, this);
        Overlay = new ZoneOverlay(host, this);
    }

    // ---- the parts' hooks ----

    public override void Update(double delta) => Overlay.Update();

    public override void LoadConfig(ConfigFile cfg)
    {
        Overlay.On = (bool)cfg.GetValue("ui", "zone_overlay_on", true);
        var kind = (string)cfg.GetValue("ui", "zone_kind", "");
        if (kind != "") _restoreKind = kind;
    }

    public override void SaveConfig(ConfigFile cfg)
    {
        cfg.SetValue("ui", "zone_overlay_on", Overlay.On);
        cfg.SetValue("ui", "zone_kind", SelectedKind ?? "");
    }

    public void BuildPage(VBoxContainer page) => Panel.Build(page);

    public void RefreshPanel()
    {
        if (Host.Tool != EditorTool.Zones) return;
        Panel.Refresh();
    }

    /// <summary>Tell the validator which kinds exist (it checks a zone's kind against the map's catalog; no catalog: no check).</summary>
    public void SyncCatalog() => ObjectEdits.ZoneKinds = Host.Map.ZoneKinds.Select(k => k.Name).ToList();

    // ---- the catalog ----

    public ZoneKindInfo? KindOf(string name) => Host.Map.ZoneKinds.FirstOrDefault(k => k.Name == name);
    public string KindLabel(string name) => KindOf(name)?.Label ?? name;

    /// <summary>Place of a kind in the catalog (kinds the catalog lacks go last).</summary>
    public int KindOrder(string kind) { var i = Host.Map.ZoneKinds.ToList().FindIndex(k => k.Name == kind); return i < 0 ? int.MaxValue : i; }

    // ---- selection ----

    /// <summary>Zones are drawn and pointed at: the overlay is on, or a kind is selected (it is then shown alone, also with the overlay off).</summary>
    public bool Drawn => Overlay.On || SelectedKind != null;

    /// <summary>A zone that can be hovered and clicked: any when no kind is selected, else only the selected kind's (the others are not drawn).</summary>
    bool Pickable(ZoneInfo z) => SelectedKind == null || z.Kind == SelectedKind;

    public bool SelectKind(string? name)
    {
        if (name != null && KindOf(name) == null) { Host.Say($"zone kind {name} is not in this map's catalog", true); return false; }
        if (name == SelectedKind) return true;
        SelectedKind = name;
        Host.MarkPanelDirty();
        Host.SaveUiConfig();
        return true;
    }

    /// <summary>The panel is redrawn: bring back what editor_ui.cfg remembered (kind, zone), and drop selections whose zone, kind or mark is gone.</summary>
    internal void SettleSelection(List<ZoneInfo> zones)
    {
        var shapes = Host.Shapes;
        if (_restoreKind != null) { if (KindOf(_restoreKind) != null) SelectedKind = _restoreKind; _restoreKind = null; }
        if (shapes.RestoreZone != null) { if (zones.Any(z => z.Id == shapes.RestoreZone)) shapes.SelectedZoneId = shapes.RestoreZone; shapes.RestoreZone = null; }
        if (shapes.SelectedZoneId != null && zones.All(z => z.Id != shapes.SelectedZoneId)) { shapes.SelectedZoneId = null; shapes.EndCornerGesture(); }
        if (SelectedKind != null && KindOf(SelectedKind) == null) SelectedKind = null;
        Marks.Prune();
    }

    public string? CannotDraw()
    {
        if (Host.Map.ZoneKinds.Count == 0) return "this map lists no zone kinds: build it again (make map), then reopen the editor";
        if (SelectedKind is not { } k) return "pick a zone kind in the list first: a zone is drawn of the selected kind";
        if (KindOf(k) == null) return $"{k} is not in this map's catalog any more";
        return null;
    }

    /// <summary>Give a zone another kind of the catalog (one save).</summary>
    public bool SetKind(string id, string kind)
    {
        if (Find(id) is not { } z) { Host.Say($"zone {id} does not exist", true); return false; }
        if (KindOf(kind) == null) { Host.Say($"zone kind {kind} is not in this map's catalog", true); return false; }
        if (z.Kind == kind) return true;
        if (z.FromRule) return EditRuleZone(z, kind, z.Points.ToList(), $"{z.Label} is now {KindLabel(kind)}");
        var ok = Host.Edits.Mutate(root =>
        {
            if (ShapeDoc.ShapesOf(root, ShapeKind.Zone)?.GetValueOrDefault(id) is not Obj e) return (false, $"zone {id} no longer exists");
            e["kind"] = kind;
            return (true, $"{id} is now {KindLabel(kind)}");
        });
        if (ok && Host.Shapes.SelectedZoneId == id) SelectKind(kind);
        return ok;
    }

    // ---- keys ----

    /// <summary>Zones tool keys besides the shape keys (not reached while a text field has the focus). True when used.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (Host.Tool != EditorTool.Zones || Host.Chrome.CheatSheetShown || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.M: Overlay.Toggle(); return true;
            case Key.X when Host.Shapes.SelectedZoneId != null && Host.Shapes.Mode == ShapeState.Selected: Marks.Toggle(Host.Shapes.SelectedZoneId); return true;
            case Key.Tab when Host.GetViewport().GuiGetFocusOwner() == null: Panel.FocusList(); return true;
        }
        return false;
    }

    /// <summary>Esc: one level up (the kind; the zone and the drawing went first, the tool is left by the main handler). True when it deselected.</summary>
    public bool Escape()
    {
        if (SelectedKind == null) return false;
        SelectKind(null);
        return true;
    }

    public string HintText() => Host.Shapes.ShapeHintText()
        ?? (SelectedKind == null
            ? "zones: Tab or click a kind to show its zones alone, click a zone to select it, Ctrl+click marks it for deletion, M overlay, N draws a zone of the selected kind, Ctrl+P commands, Esc leaves the tool"
            : $"{KindLabel(SelectedKind)} shown alone: click a zone to select it, Ctrl+click marks it for deletion{(Marks.Count > 0 ? $", Shift+Del deletes the {Marks.Count} marked" : "")}, N draws one, Esc shows all kinds");

    // ---- the zones: drawn ones and the ones a rule gives ----

    /// <summary>
    /// Every zone of the map: the drawn ones (edits.json `zones`) and those the rules give (a ground area of a kind's where_ground
    /// types, one outline each), without the cut ones. One concept to the user. Cached per file read and map.
    /// </summary>
    public List<ZoneInfo> List()
    {
        var map = Host.Map;
        var disk = Host.Edits.Disk();
        var kinds = map.ZoneKinds;
        if (ReferenceEquals(disk, _zonesFor) && ReferenceEquals(kinds, _kindsFor) && _cache != null) return _cache;
        var list = new List<ZoneInfo>();
        var sig = new StringBuilder();
        _zonesFor = disk; _kindsFor = kinds; _cache = list;
        _byId.Clear();
        if (ShapeDoc.ShapesOf(disk, ShapeKind.Zone) is { } zo)
            foreach (var (id, e) in zo)
                if (e is Obj r && r.GetValueOrDefault("kind") is string kind && ShapeDoc.ParsePolygon(r.GetValueOrDefault("polygon")) is { } ring)
                {
                    list.Add(new ZoneInfo(id, kind, ring));
                    sig.Append(id).Append(':').Append(kind).Append(':').AppendJoin(',', ring.Select(c => $"{c.x:F1},{c.y:F1}")).Append(';');
                }
        var cuts = ZoneCut.ListOf(disk);
        foreach (var c in cuts) sig.Append("cut:").Append(c.Kind).Append(':').AppendJoin(',', c.Ring.Select(v => $"{v.x:F1},{v.y:F1}")).Append(';');
        if (map.OriginEpsg5514 != null)
            foreach (var k in kinds.Where(k => k.WhereGround != null))
            {
                var mine = cuts.Where(c => c.Kind == k.Name).ToList();
                var n = 0;
                foreach (var part in RuleParts().Where(p => k.WhereGround!.Contains(p.GroundKind)))
                {
                    if (mine.Any(c => c.Covers(part))) continue;
                    list.Add(new ZoneInfo(part.Part + RuleSep + k.Name, k.Name, part.Abs, part.Part, part.HoleAbs.Count > 0 ? part.HoleAbs : null));
                    n++;
                }
                sig.Append(k.Name).Append('=').Append(n).Append(';');
            }
        Signature = sig.ToString();
        foreach (var z in list) _byId[z.Id] = z;
        return list;
    }

    public ZoneInfo? Find(string id) { List(); return _byId.GetValueOrDefault(id); }

    /// <summary>What the zones are made of (drawn ones, cuts, rule zones per kind) as of the last <see cref="List"/>: the overlay is rebuilt when it changes.</summary>
    internal string Signature { get; private set; } = "";

    List<RulePart> RuleParts()
    {
        var kinds = Host.Map.ZoneKinds;
        if (_ruleParts != null && ReferenceEquals(_rulePartsFor, kinds)) return _ruleParts;
        _rulePartsFor = kinds;
        return _ruleParts = RulePart.Of(Host.Map);
    }

    public int Count(string kind) => List().Count(z => z.Kind == kind);
    public int CutCount => ZoneCut.ListOf(Host.Edits.Disk()).Count;

    /// <summary>
    /// Edit a zone that comes from a rule (move a corner, change its kind, delete it) as one save and one undo step: a cut of its
    /// outline is recorded in its kind (edits.json `zone_cuts`) and, unless it is a delete (<paramref name="ring"/> null), a drawn zone
    /// of <paramref name="kind"/> (default: its own) with <paramref name="ring"/> is written. The new zone is selected.
    /// </summary>
    public bool EditRuleZone(ZoneInfo z, string? kind, List<(double x, double y)>? ring, string message)
    {
        var outline = ObjectEdits.Despiked(z.Points); // zero-width spikes of the rule outline would fail the ring check
        if (ObjectEdits.RingProblemCode(outline) is var code && code != ObjectEdits.RingOk) { Host.Say($"{z.Label} cannot be edited: its outline {ShapeDoc.RingMessage(code)}", true); return false; }
        if (ring != null && ring.Count == z.Points.Count && ring.SequenceEqual(z.Points)) ring = outline;
        string? newId = null;
        var ok = Host.Edits.Mutate(root =>
        {
            AddCut(root, z);
            if (ring != null)
            {
                if (ShapeDoc.ShapesOf(root, ShapeKind.Zone) is not { } zones) root[ShapeDoc.KeyOf(ShapeKind.Zone)] = zones = EditsFile.NewObject();
                newId = ShapeDoc.NextShapeId(root, ShapeKind.Zone);
                var e = EditsFile.NewObject();
                e["kind"] = kind ?? z.Kind;
                e["polygon"] = ShapeDoc.PolygonValue(ring);
                zones[newId] = e;
            }
            return (true, message);
        });
        if (ok && newId != null) { var corner = Host.Shapes.ActiveCorner; Host.Shapes.SelectShape(ShapeKind.Zone, newId); Host.Shapes.ActiveCorner = corner; }
        return ok;
    }

    /// <summary>Record the cut of a rule zone's (despiked) outline in its kind: edits.json `zone_cuts`.</summary>
    internal static void AddCut(Obj root, ZoneInfo z)
    {
        if (root.GetValueOrDefault("zone_cuts") is not Obj cuts) root["zone_cuts"] = cuts = EditsFile.NewObject();
        var cut = EditsFile.NewObject();
        cut["kind"] = z.Kind;
        cut["polygon"] = ShapeDoc.PolygonValue(ObjectEdits.Despiked(z.Points));
        cuts[ShapeDoc.NextId(root, "zone_cuts", "ZC-")] = cut;
    }

    // ---- pointing ----

    /// <summary>Hover in the Zones tool: the zones at the cursor, as a label near it.</summary>
    public string? HoverText(Vector3 point)
    {
        var (x, y) = Host.Map.RawAbs(point);
        var hits = List().Where(z => Pickable(z) && z.Contains(x, y)).OrderByDescending(z => ShapeDoc.ShapeNumber(z.Id)).ThenBy(z => KindOrder(z.Kind)).ToList();
        if (hits.Count == 0) return SelectedKind is { } k ? $"no {KindLabel(k)} zone here" : "no zone here";
        var lines = hits.Take(5).Select(z => $"{z.Label}  {KindLabel(z.Kind)} zone{(Marks.Contains(z.Id) ? "  (marked for deletion)" : "")}").ToList();
        if (hits.Count > 5) lines.Add($"+{hits.Count - 5} more");
        return string.Join("\n", lines) + "\nclick to select, Ctrl+click to mark for deletion";
    }

    /// <summary>The zone to select at a point (of the selected kind only, when one is: the others are not drawn): where zones overlap a drawn one wins over the others, then the higher ID, the kind earlier in the catalog.</summary>
    public string? AtPoint(double x, double y)
    {
        var hits = List().Where(z => Pickable(z) && z.Contains(x, y)).ToList();
        if (hits.Count == 0) return null;
        return hits.OrderBy(z => z.Kind == SelectedKind ? 1 : 0).ThenBy(z => ShapeDoc.ShapeNumber(z.Id)).ThenByDescending(z => KindOrder(z.Kind)).ThenBy(z => z.Id, StringComparer.Ordinal).Last().Id;
    }
}

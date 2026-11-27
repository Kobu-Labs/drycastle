using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>The editor's modal pieces (<see cref="ConfirmDialog"/>, <see cref="CommandPalette"/>): what they offer and which keys they take.</summary>
public partial class EditorMode
{
    public ConfirmDialog Confirm { get; } = new();
    public CommandPalette Commands { get; }

    public bool Modal => Confirm.Visible || Commands.Visible || Photo.ViewerOpen;

    /// <summary>Ctrl+P and the keys of an open confirm dialog or palette. True when used.</summary>
    bool HandleModalKey(InputEventKey k)
    {
        if (k.CtrlPressed && k.Keycode == Key.P) { if (Commands.Visible) Commands.Close(); else Commands.Open(); return true; }
        if (Confirm.Visible)
        {
            if (k.Keycode == Key.Escape) Confirm.Cancel();
            return k.Keycode == Key.Escape; // Enter and Tab go to the focused button
        }
        if (Commands.Visible) { if (k.Keycode == Key.Escape) Commands.Close(); return k.Keycode == Key.Escape; }
        if (Photo.ViewerOpen) { Photo.ViewerKey(k); return true; }
        return false;
    }

    IEnumerable<(string label, Action run)> PaletteAll(string filter)
    {
        foreach (var (tool, _, name, key) in EditorChrome.Tools) { var t = tool; yield return ($"Tool: {name}  ({OS.GetKeycodeString(key)})", () => SelectTool(t)); }
        foreach (var g in Ground.Rows()) { var gn = g.Name; yield return ($"{g.Name}  ground type  {GroundTypeRows.RecipeWord(g)}  {GroundTypeRows.StateWord(g.State)}", () => { SelectTool(EditorTool.Ground); Ground.SelectType(gn); }); }
        yield return ("Ground: draw a patch of the selected type  (N)", () => { SelectTool(EditorTool.Ground); Shapes.StartShape(); });
        yield return (Ground.Overlay.On ? "Ground: hide the ground map  (M)" : "Ground: show the ground map  (M)", () => { SelectTool(EditorTool.Ground); Ground.Overlay.Toggle(); });
        yield return ("Zones: draw a zone of the selected kind  (N)", () => { SelectTool(EditorTool.Zones); Shapes.StartShape(); });
        if (Zones.Marks.Count > 0)
        {
            yield return ($"Zones: delete the {Zones.Marks.Count} marked zones  (Shift+Del)", () => { SelectTool(EditorTool.Zones); Zones.Marks.RequestDelete(); });
            yield return ("Zones: unmark all zones", () => { SelectTool(EditorTool.Zones); Zones.Marks.Clear(); });
        }
        yield return (Zones.SelectedKind != null ? $"Zones: mark all {Zones.KindLabel(Zones.SelectedKind)} zones for deletion" : "Zones: mark all zones for deletion", () => { SelectTool(EditorTool.Zones); Zones.Marks.MarkListed(); });
        yield return (Zones.Overlay.On ? "Zones: hide the zone overlay  (M)" : "Zones: show the zone overlay  (M)", () => { SelectTool(EditorTool.Zones); Zones.Overlay.Toggle(); });
        yield return (FloorPlan.FloorPlanParcel != null ? $"View: floor plan off, show the building of {FloorPlan.FloorPlanParcel}  (B)" : "View: floor plan of the selected parcel  (B)", FloorPlan.ToggleFloorPlan);
        yield return ("Photo to building: open the tool for the selected parcel  (0)", () => SelectTool(EditorTool.Photo));
        yield return ("Sketch: mark points  (P)", () => { SelectTool(EditorTool.Sketch); Sketches.SetMode(SketchKind.Point); });
        yield return ("Sketch: draw a line  (L)", () => { SelectTool(EditorTool.Sketch); Sketches.SetMode(SketchKind.Line); });
        if (Sketches.Doc.Count > 0) yield return ("Sketch: clear the active sketch", () => { SelectTool(EditorTool.Sketch); Sketches.RequestClear(); });
        yield return ("Poles: plan a line  (P)", () => { SelectTool(EditorTool.Poles); Poles.Plan.Start(); });
        yield return ("Poles: connect  (C)", () => { SelectTool(EditorTool.Poles); Poles.StartConnecting(); });
        yield return ("Edit: undo  (Ctrl+Z)", () => Edits.Undo());
        yield return ("Edit: redo  (Ctrl+Y)", () => Edits.Redo());
        yield return ("View: top-down  (T)", ToggleTopDown);
        yield return ("Walk: drop to the ground here  (F3)", WalkFromHere);
        yield return ("Help: controls  (F1)", Chrome.ToggleCheatSheet);
        foreach (var r in Shapes.Patches()) { var id = r.Id; yield return ($"{r.Id}  ground patch  {r.Type}  {r.Points.Count} corners", () => { SelectTool(EditorTool.Ground); Shapes.SelectShape(ShapeKind.Patch, id); }); }
        foreach (var z in Zones.List()) { var id = z.Id; yield return ($"{z.Label}  zone  {Zones.KindLabel(z.Kind)}  {z.Points.Count} corners", () => { SelectTool(EditorTool.Zones); Shapes.SelectShape(ShapeKind.Zone, id); }); }
        foreach (var k in Map.ZoneKinds) { var n = k.Name; yield return ($"{k.Label}  zone kind", () => { SelectTool(EditorTool.Zones); Zones.SelectKind(n); }); }
        foreach (var p in Sketches.Doc.Points) { var id = p.Id; yield return ($"{p.Id}  sketch point", () => { SelectTool(EditorTool.Sketch); Sketches.SelectItem(id); }); }
        foreach (var l in Sketches.Doc.Lines) { var id = l.Id; yield return ($"{l.Id}  sketch {(l.Closed ? "closed line" : "line")}  {l.Points.Count} corners", () => { SelectTool(EditorTool.Sketch); Sketches.SelectItem(id); }); }
        if (filter.Length >= 2)
            foreach (var p in Map.Parcels.Values.OrderBy(p => p.Id, StringComparer.Ordinal)) { var id = p.Id; yield return ($"{p.Id}  parcel  {p.Kind}", () => { SelectTool(EditorTool.Select); Click(id, false); }); }
    }
}

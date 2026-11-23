using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The ground patches half of the Ground tool (GLOSSARY "Ground patch", ADR 0022): a hand-drawn polygon of exactly one ground type,
/// plus a water level when the type is water. Stored in edits.json `ground_patches` ({GP-NNNN: {type, polygon, water_y?}}); where
/// patches overlap the higher ID wins. The polygon gestures (draw, close, corners, delete) are the shared machinery of
/// EditorMode.Shapes.cs; this file is what is specific to a patch: the type it is of, its water level, its form and list. Until the
/// next full map build a patch shows as paint in its type's first terrain colour (patch-run through the EditsWatcher); the ground
/// map (EditorMode.GroundMap.cs) shows it as an area of its type at once.
/// </summary>
public partial class EditorMode
{
    OptionButton _pType = null!;
    HBoxContainer _pTypeRow = null!, _pCropRow = null!;
    TextureRect _pCropSwatch = null!;
    Label _pCropName = null!;
    public const string OutlineGroundHover = "outline_ground_hover";
    PatchInfo? _gHoverPatch;
    Label _pNote = null!;
    bool _pSync;
    string _pTypeSig = "\0", _pScrolledFor = "";
    List<(string Name, string Colour, bool Water)> _pChoices = new();

    // ---- what a patch needs ----

    /// <summary>Why a patch cannot be drawn now (null: it can): it is of the selected ground type, which must be there and usable.</summary>
    string? CannotDrawPatch()
    {
        if (SelectedGroundType is not { } t) return "pick a ground type in the list first: a patch is drawn of the selected type";
        if (GroundRow(t) is not { } r) return $"{t} is not a ground type of this map any more";
        if (r.Deleted) return $"{t} is deleted: Reset to rules brings it back, or pick another type";
        if (r.Effective.Rule != null) return $"{t} is made by a rule ({r.Effective.Rule}); a patch cannot be of it";
        return null;
    }

    bool PatchTypeIsWater(string? type) => type != null && GroundRow(type)?.Effective.Water == true;

    /// <summary>A starting water level for a patch: the terrain height at the middle of its corners, to one decimal; the form then asks for the real one.</summary>
    double DefaultWaterLevel(IReadOnlyList<(double x, double y)> ring)
    {
        if (ring.Count == 0) return 0;
        var l = Map.FromAbsolute(ring.Average(p => p.x), ring.Average(p => p.y));
        return Math.Round(Map.GroundHeightOrMean(l.X, l.Y), 1);
    }

    // ---- the edits (each is one Mutate: one save, one undo step) ----

    /// <summary>Give a patch another ground type (one save). A water type needs a water level: the patch gets the terrain height at its middle to start from (edit it in the form); a land type takes the level away.</summary>
    public bool SetPatchType(string id, string type)
    {
        if (Patch(id) is not { } p) { Say($"patch {id} does not exist", true); return false; }
        if (p.Type == type) return true;
        if (PatchTypeChoices().All(c => c.Name != type)) { Say($"{type} is not a ground type a patch can be of (deleted, a rule's, or not in this map)", true); return false; }
        var water = PatchTypeIsWater(type);
        var level = DefaultWaterLevel(p.Points);
        return Edits.Mutate(root =>
        {
            if (ShapesOf(root, ShapeKind.Patch)?.GetValueOrDefault(id) is not Obj e) return (false, $"patch {id} no longer exists");
            e["type"] = type;
            if (water) { if (!e.ContainsKey("water_y")) e["water_y"] = N3(level); }
            else { e.Remove("water_y"); e.Remove("max_depth"); }
            return (true, $"{id} is now {type}" + (water && p.WaterY == null ? $", water level {level:0.0} m to start from" : ""));
        });
    }

    /// <summary>Set the water level of a patch of a water type (one save).</summary>
    public bool SetPatchWater(string id, double level)
    {
        if (Patch(id) is not { } p) { Say($"patch {id} does not exist", true); return false; }
        if (!PatchTypeIsWater(p.Type)) { Say($"{id} is of {p.Type}, which is not water: it has no water level", true); return false; }
        var v = Math.Round(level, 3, MidpointRounding.AwayFromZero);
        if (p.WaterY == v) return true;
        return Edits.Mutate(root =>
        {
            if (ShapesOf(root, ShapeKind.Patch)?.GetValueOrDefault(id) is not Obj e) return (false, $"patch {id} no longer exists");
            e["water_y"] = N3(v);
            return (true, $"{id} water level {v:0.0###} m");
        });
    }

    void OnWaterLevelChanged(double v)
    {
        if (ShapeMode == ShapeState.Level) { _newWaterY = v; return; }
        if (CurrentKind == ShapeKind.Patch && ShapeMode == ShapeState.Selected && SelectedPatchId is { } id) SetPatchWater(id, v);
    }

    /// <summary>The patch under the cursor of the Ground tool, for the hover label.</summary>
    string? PatchHoverText(Vector3 point)
    {
        var (x, y) = Map.RawAbs(point);
        if (ShapeAt(ShapeKind.Patch, x, y) is not { } id || Patch(id) is not { } p) return null;
        return $"{p.Id}  patch of {p.Type}{(EditorTheme.CropName(p.Type) is { } crop ? $" (crop: {crop})" : "")}{(p.WaterY is { } w ? $", water {w:0.0} m" : "")}  click to select";
    }

    /// <summary>
    /// The one outline of the Ground tool besides the selected patch: the patch under the cursor, lighter and thinner than the selection
    /// (crop types in their crop colour, others white). Not drawn for the selected patch (its own outline is stronger) nor while a
    /// gesture is under way. <paramref name="point"/> null: nothing hovered. Rebuilt only when the hovered patch changes.
    /// </summary>
    void UpdateGroundHoverOutline(Vector3? point)
    {
        PatchInfo? hot = null;
        if (point is { } pt)
        {
            var (x, y) = Map.RawAbs(pt);
            if (ShapeAt(ShapeKind.Patch, x, y) is { } id && id != SelectedPatchId) hot = Patch(id);
        }
        if (ReferenceEquals(hot, _gHoverPatch)) return;
        _gHoverPatch = hot;
        var colour = hot != null && EditorTheme.CropColour(hot.Type) is { } c ? c : EditorTheme.Text;
        SetOutline(OutlineGroundHover, hot == null ? new() : RingSegs(hot.Points, true), new Color(colour, GroundHoverAlpha), 0.27f, GroundHoverWidth);
    }
    const float GroundHoverAlpha = 0.65f, GroundHoverWidth = 0.28f;

    // ---- the page ----

    void BuildPatchSection(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Ground patches", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("A patch is a hand-drawn polygon of one ground type. Select a type above, press N (or the button), click corners on the ground and press Enter. Click a patch to select it, drag its corners, change its type or delete it."));
        _pType = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _pType.ItemSelected += i =>
        {
            if (!_pSync && SelectedPatchId != null && i >= 0 && i < _pChoices.Count) SetPatchType(SelectedPatchId, _pChoices[(int)i].Name);
            _pType.ReleaseFocus();
        };
        _pTypeRow = FieldRow("Type", _pType, "The ground type of the selected patch. A water type needs a water level.");
        // a crop_* type also says its crop in words next to its outline colour, so the colour is never the only cue
        _pCropSwatch = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _pCropName = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var cropField = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        cropField.AddChild(_pCropSwatch);
        cropField.AddChild(_pCropName);
        _pCropRow = FieldRow("Crop", cropField, "The crop of the selected patch (its ground type is a crop_ one). The patch is outlined in this colour on the map. Change the crop by changing the Type above.");
        var form = new VBoxContainer();
        form.AddChild(_pTypeRow);
        form.AddChild(_pCropRow);
        BuildShapeActions(page, ShapeKind.Patch, form);
        _pNote = Ui.Note("");
        page.AddChild(_pNote);
        BuildShapeDelete(page, ShapeKind.Patch);
        BuildShapeList(page, ShapeKind.Patch, "All patches", "No patches yet. Select a ground type, press N, click corners on the ground, Enter creates the patch.");
        page.AddChild(Ui.Note("Where patches overlap the higher ID wins. Esc steps back: cancel drawing, deselect the patch, deselect the type, leave the tool."));
    }

    void RefreshPatches()
    {
        var patches = PatchList();
        if (_restorePatch != null) { if (patches.Any(p => p.Id == _restorePatch)) SelectedPatchId = _restorePatch; _restorePatch = null; _groundFocusPatch = SelectedPatchId != null; }
        if (SelectedPatchId != null && patches.All(p => p.Id != SelectedPatchId)) { SelectedPatchId = null; EndCornerGesture(); _groundFocusPatch = false; }
        var sel = SelectedPatchId == null ? null : Patch(SelectedPatchId);
        var mode = ShapeMode;
        var typeText = SelectedGroundType ?? "the selected type";

        RefreshShapeActions(ShapeKind.Patch, $"Draw patch of {typeText}  (N)", s => ((PatchInfo)s).Type + (EditorTheme.CropName(((PatchInfo)s).Type) is { } cn ? $", crop {cn}" : "") + (((PatchInfo)s).WaterY is { } w ? $", water {w:0.0} m" : ""));
        var u = _su[ShapeKind.Patch];
        var showForm = mode == ShapeState.Selected && sel != null;
        _pTypeRow.Visible = showForm;
        var crop = showForm ? EditorTheme.CropName(sel!.Type) : null;
        _pCropRow.Visible = crop != null;
        if (crop != null) { _pCropSwatch.Texture = Ui.Swatch(EditorTheme.CropColour(sel!.Type)!.Value); _pCropName.Text = crop; }
        if (showForm)
        {
            FillPatchTypes(sel!.Type);
            var editable = PatchTypeChoices().Count > 0;
            _pType.Disabled = !editable;
            _pType.TooltipText = editable ? "The ground type of the selected patch. A water type needs a water level." : "This map lists no ground types: build it again (make map)";
        }
        var water = showForm && sel!.WaterY != null;
        if (mode != ShapeState.Level) u.LevelRow.Visible = water;
        if (water && !u.Level.GetLineEdit().HasFocus()) { u.Sync = true; u.Level.SetValueNoSignal(sel!.WaterY!.Value); u.Sync = false; }
        _pNote.Visible = showForm;
        if (showForm)
            _pNote.Text = sel!.WaterY != null
                ? $"A body of water at {sel.WaterY:0.0###} m. The preview is paint; the water and its detail come with the next full map build (make map)."
                : $"Shown as paint in {sel.Type}'s colour for now. Its grass, trees and detail come with the next full map build (make map).";

        RefreshShapeList(ShapeKind.Patch, "patch", s =>
        {
            var p = (PatchInfo)s;
            var colour = GroundRow(p.Type)?.Effective.Colour ?? "#808080";
            var text = $"{p.Id}   {p.Type}{(p.WaterY is { } w ? $"   water {w:0.0} m" : "")}   {p.Points.Count} corners";
            return (text, colour, $"Select {p.Id} (patch of {p.Type}, {p.Points.Count} corners, {p.AreaM2:F0} m2)");
        }, GroundRows().Count.ToString() + string.Concat(GroundRows().Select(r => r.Effective.Colour)));

        if (sel != null && _pScrolledFor != sel.Id)
        {
            _pScrolledFor = sel.Id;
            if (_groundFocusPatch) Callable.From(() => _scroll.EnsureControlVisible(u.State)).CallDeferred();
        }
        else if (sel == null) _pScrolledFor = "";
    }

    /// <summary>The Type dropdown of the patch form lists the types as they are now (your edits included); rebuilt only when they change and the list is closed.</summary>
    void FillPatchTypes(string current)
    {
        var choices = PatchTypeChoices();
        if (choices.All(c => c.Name != current) && GroundRow(current) is { } cur) choices = choices.Append((cur.Name, cur.Effective.Colour, cur.Effective.Water)).ToList(); // a deleted type stays visible
        var sig = string.Join("|", choices.Select(c => c.Name + c.Colour + c.Water));
        if (!_pType.GetPopup().Visible && sig != _pTypeSig)
        {
            _pTypeSig = sig;
            _pChoices = choices;
            _pSync = true;
            _pType.Clear();
            foreach (var t in choices) _pType.AddIconItem(Ui.Swatch(t.Colour), PatchTypeText(t.Name, t.Water));
            _pSync = false;
        }
        var at = _pChoices.FindIndex(c => c.Name == current);
        if (!_pType.GetPopup().Visible && _pType.Selected != at) { _pSync = true; _pType.Selected = at; _pSync = false; }
    }

    /// <summary>A type in the Type dropdown: crops read as "crop_grain  (crop: Grain)", water types get "(water)".</summary>
    static string PatchTypeText(string name, bool water)
        => EditorTheme.CropName(name) is { } crop ? $"{name}  (crop: {crop})" : water ? name + "  (water)" : name;

    // test hooks
    public string PatchTypeShown => _pType.Selected >= 0 && _pType.Selected < _pChoices.Count ? _pChoices[_pType.Selected].Name : "";
    public bool PatchWaterRowShown => _su[ShapeKind.Patch].LevelRow.Visible;
    public void ChoosePatchType(string type) { if (SelectedPatchId != null) SetPatchType(SelectedPatchId, type); }
    public void SetDrawWaterLevel(double v) { _newWaterY = v; var u = _su[ShapeKind.Patch]; u.Sync = true; u.Level.SetValueNoSignal(v); u.Sync = false; }
}

using Godot;
using System.Collections.Generic;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The ground patches section of the Ground page (GLOSSARY "Ground patch", ADR 0022): the form of the selected patch (its type, its crop) and the list.
/// The polygon gestures, the patch type and the water level are the shape editor's (<see cref="ShapeEditor"/>, shared with Zones); this is what is specific to a patch page.
/// Until the next full map build a patch shows as paint in its type's first terrain colour (patch-run through the EditsWatcher); the ground map shows it as an area of its type at once.
/// </summary>
public sealed class GroundPatchPage
{
    readonly IEditorHost _host;
    readonly GroundTool _tool;

    OptionButton _type = null!;
    HBoxContainer _typeRow = null!, _cropRow = null!;
    TextureRect _cropSwatch = null!;
    Label _cropName = null!, _note = null!;
    bool _sync;
    string _typeSig = "\0", _scrolledFor = "";
    List<(string Name, string Colour, bool Water)> _choices = new();

    public GroundPatchPage(IEditorHost host, GroundTool tool)
    {
        _host = host;
        _tool = tool;
    }

    // what the smoke reads
    public string TypeShown => _type.Selected >= 0 && _type.Selected < _choices.Count ? _choices[_type.Selected].Name : "";
    public bool WaterRowShown => _host.Shapes.Panel.Of(ShapeKind.Patch).LevelRow.Visible;

    public void Build(VBoxContainer page)
    {
        var shapes = _host.Shapes;
        page.AddChild(new Label { Text = "Ground patches", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("A patch is a hand-drawn polygon of one ground type. Select a type above, press N (or the button), click corners on the ground and press Enter. Click a patch to select it, drag its corners, change its type or delete it."));
        _type = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _type.ItemSelected += i =>
        {
            if (!_sync && shapes.SelectedPatchId != null && i >= 0 && i < _choices.Count) shapes.SetPatchType(shapes.SelectedPatchId, _choices[(int)i].Name);
            _type.ReleaseFocus();
        };
        _typeRow = Ui.FieldRow("Type", _type, "The ground type of the selected patch. A water type needs a water level.");
        // a crop_* type also says its crop in words next to its outline colour, so the colour is never the only cue
        _cropSwatch = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _cropName = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var cropField = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        cropField.AddChild(_cropSwatch);
        cropField.AddChild(_cropName);
        _cropRow = Ui.FieldRow("Crop", cropField, "The crop of the selected patch (its ground type is a crop_ one). The patch is outlined in this colour on the map. Change the crop by changing the Type above.");
        var form = new VBoxContainer();
        form.AddChild(_typeRow);
        form.AddChild(_cropRow);
        shapes.Panel.BuildActions(page, ShapeKind.Patch, form);
        _note = Ui.Note("");
        page.AddChild(_note);
        shapes.Panel.BuildDelete(page, ShapeKind.Patch);
        shapes.Panel.BuildList(page, ShapeKind.Patch, "All patches", "No patches yet. Select a ground type, press N, click corners on the ground, Enter creates the patch.");
        page.AddChild(Ui.Note("Where patches overlap the higher ID wins. Esc steps back: cancel drawing, deselect the patch, deselect the type, leave the tool."));
    }

    public void Refresh()
    {
        var shapes = _host.Shapes;
        var patches = shapes.PatchList();
        if (shapes.RestorePatch != null) { if (patches.Any(p => p.Id == shapes.RestorePatch)) shapes.SelectedPatchId = shapes.RestorePatch; shapes.RestorePatch = null; _tool.FocusPatch = shapes.SelectedPatchId != null; }
        if (shapes.SelectedPatchId != null && patches.All(p => p.Id != shapes.SelectedPatchId)) { shapes.SelectedPatchId = null; shapes.EndCornerGesture(); _tool.FocusPatch = false; }
        var sel = shapes.SelectedPatchId == null ? null : shapes.Patch(shapes.SelectedPatchId);
        var mode = shapes.Mode;
        var typeText = _tool.SelectedType ?? "the selected type";

        shapes.Panel.RefreshActions(ShapeKind.Patch, $"Draw patch of {typeText}  (N)", s => ((PatchInfo)s).Type + (EditorTheme.CropName(((PatchInfo)s).Type) is { } cn ? $", crop {cn}" : "") + (((PatchInfo)s).WaterY is { } w ? $", water {w:0.0} m" : ""));
        var u = shapes.Panel.Of(ShapeKind.Patch);
        var showForm = mode == ShapeState.Selected && sel != null;
        _typeRow.Visible = showForm;
        var crop = showForm ? EditorTheme.CropName(sel!.Type) : null;
        _cropRow.Visible = crop != null;
        if (crop != null) { _cropSwatch.Texture = Ui.Swatch(EditorTheme.CropColour(sel!.Type)!.Value); _cropName.Text = crop; }
        if (showForm)
        {
            FillTypes(sel!.Type);
            var editable = _tool.PatchTypeChoices().Count > 0;
            _type.Disabled = !editable;
            _type.TooltipText = editable ? "The ground type of the selected patch. A water type needs a water level." : "This map lists no ground types: build it again (make map)";
        }
        var water = showForm && sel!.WaterY != null;
        if (mode != ShapeState.Level) u.LevelRow.Visible = water;
        if (water && !u.Level.GetLineEdit().HasFocus()) { u.Sync = true; u.Level.SetValueNoSignal(sel!.WaterY!.Value); u.Sync = false; }
        _note.Visible = showForm;
        if (showForm)
            _note.Text = sel!.WaterY != null
                ? $"A body of water at {sel.WaterY:0.0###} m. The preview is paint; the water and its detail come with the next full map build (make map)."
                : $"Shown as paint in {sel.Type}'s colour for now. Its grass, trees and detail come with the next full map build (make map).";

        shapes.Panel.RefreshList(ShapeKind.Patch, "patch", s =>
        {
            var p = (PatchInfo)s;
            var colour = _tool.Row(p.Type)?.Effective.Colour ?? "#808080";
            var text = $"{p.Id}   {p.Type}{(p.WaterY is { } w ? $"   water {w:0.0} m" : "")}   {p.Points.Count} corners";
            return (text, colour, $"Select {p.Id} (patch of {p.Type}, {p.Points.Count} corners, {p.AreaM2:F0} m2)");
        }, _tool.Rows().Count.ToString() + string.Concat(_tool.Rows().Select(r => r.Effective.Colour)));

        if (sel != null && _scrolledFor != sel.Id)
        {
            _scrolledFor = sel.Id;
            if (_tool.FocusPatch) _host.Chrome.RevealInPanel(u.State);
        }
        else if (sel == null) _scrolledFor = "";
    }

    /// <summary>The Type dropdown of the patch form lists the types as they are now (your edits included); rebuilt only when they change and the list is closed.</summary>
    void FillTypes(string current)
    {
        var choices = _tool.PatchTypeChoices();
        if (choices.All(c => c.Name != current) && _tool.Row(current) is { } cur) choices = choices.Append((cur.Name, cur.Effective.Colour, cur.Effective.Water)).ToList(); // a deleted type stays visible
        var sig = string.Join("|", choices.Select(c => c.Name + c.Colour + c.Water));
        if (!_type.GetPopup().Visible && sig != _typeSig)
        {
            _typeSig = sig;
            _choices = choices;
            _sync = true;
            _type.Clear();
            foreach (var t in choices) _type.AddIconItem(Ui.Swatch(t.Colour), TypeText(t.Name, t.Water));
            _sync = false;
        }
        var at = _choices.FindIndex(c => c.Name == current);
        if (!_type.GetPopup().Visible && _type.Selected != at) { _sync = true; _type.Selected = at; _sync = false; }
    }

    /// <summary>A type in the Type dropdown: crops read as "crop_grain  (crop: Grain)", water types get "(water)".</summary>
    static string TypeText(string name, bool water)
        => EditorTheme.CropName(name) is { } crop ? $"{name}  (crop: {crop})" : water ? name + "  (water)" : name;
}

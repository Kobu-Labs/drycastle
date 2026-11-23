using Drycastle.Viewer.Edits;
using Godot;
using System.Collections.Generic;
using System.Linq;
using GtRow = Drycastle.Viewer.Edits.GroundTypeEdits.Row;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Ground tool (GLOSSARY "Ground type", "Ground patch", ADR 0022): list, edit, create, duplicate, reset and delete the ground types
/// of the map, and draw patches of them. A type is terrain only: its colours, detail recipe and whether it is water (the fixed catalog of
/// <see cref="GroundTypeEdits"/>, Appearance and Water groups); nothing a game system reads, that is a zone. The user's changes are
/// stored in edits.json `ground_types` over the entries of rules.yaml; the types of rules.yaml as the last build wrote them
/// come from parcels.json (`Map.GroundTypeTable`). Selecting a type (list or click on the ground) says which type the next patch is
/// of and shows its form. A type change needs a full map build (make map): the banner says so. Esc goes one level up: leave a text
/// field, deselect the patch, deselect the type, leave the tool. Names are IDs, so there is no rename.
/// This class holds the selection, the table of types, the keys and the hover; the edits are <see cref="GroundTypeOps"/>, the sidebar is
/// <see cref="GroundTypesPanel"/> with <see cref="GroundPatchPage"/>, the overlay is <see cref="GroundMap"/>. The polygon gestures of
/// a patch are the shape editor's.
/// </summary>
public sealed class GroundTool : EditorPart
{
    const float HoverAlpha = 0.65f, HoverWidth = 0.28f;
    public const string OutlineHover = "outline_ground_hover";

    public GroundTypeOps Ops { get; }
    public GroundTypesPanel Panel { get; }
    public GroundPatchPage PatchPage { get; }
    public GroundMap Overlay { get; }

    public string? SelectedType { get; private set; }
    /// <summary>True while the last thing selected was a patch (Del, Esc and the hints then mean the patch), false while it was a type.</summary>
    public bool FocusPatch { get; set; }

    string? _restore;
    Obj? _rowsFor;
    object? _tableFor;
    List<GtRow> _rows = new();
    PatchInfo? _hoverPatch;

    public GroundTool(IEditorHost host) : base(host)
    {
        Ops = new GroundTypeOps(host, this);
        Panel = new GroundTypesPanel(host, this);
        PatchPage = new GroundPatchPage(host, this);
        Overlay = new GroundMap(host, this);
    }

    // ---- the parts' hooks ----

    public override void Update(double delta) => Overlay.Update();

    public override void LoadConfig(ConfigFile cfg)
    {
        Overlay.On = (bool)cfg.GetValue("ui", "ground_map_on", true);
        var type = (string)cfg.GetValue("ui", "ground_type", "");
        if (type != "") _restore = type;
    }

    public override void SaveConfig(ConfigFile cfg)
    {
        cfg.SetValue("ui", "ground_map_on", Overlay.On);
        cfg.SetValue("ui", "ground_type", SelectedType ?? "");
    }

    public void BuildPage(VBoxContainer page) => Panel.Build(page);

    public void RefreshPanel() => Panel.Refresh();

    // ---- the table ----

    /// <summary>The ground types as they stand: rules.yaml's (as of the last build) with the edits on disk laid over them, then the user's own.</summary>
    public IReadOnlyList<GtRow> Rows()
    {
        var map = Host.Map;
        var disk = Host.Edits.Disk();
        if (ReferenceEquals(disk, _rowsFor) && ReferenceEquals(map.GroundTypeTable, _tableFor)) return _rows;
        _rowsFor = disk;
        _tableFor = map.GroundTypeTable;
        var edits = disk.GetValueOrDefault("ground_types") as Obj ?? EditsFile.NewObject();
        var bases = new List<(string, Obj?)>();
        var known = new Dictionary<string, Obj>();
        foreach (var t in map.GroundTypeTable)
        {
            if (t.Custom) continue;
            Obj? raw;
            if (t.RulesJson != null) raw = EditsFile.ParseValue(t.RulesJson) is Obj parsed ? GroundTypeEdits.UpgradeRules(parsed) : null; // a map built before ADR 0015 has them flat
            else
            {
                // built before ground types were editable: the colour only
                raw = EditsFile.NewObject();
                var attrs = EditsFile.NewObject();
                attrs["colours"] = new List<object?> { t.Colour };
                raw["attributes"] = attrs;
            }
            bases.Add((t.Name, raw));
            if (raw != null && !t.Legacy) known[t.Name] = raw;
        }
        GroundTypeEdits.RulesTypes = known;
        GroundTypeEdits.DefaultType = map.GroundDefault;
        _rows = GroundTypeEdits.Table(bases, edits);
        return _rows;
    }

    public GtRow? Row(string name) => Rows().FirstOrDefault(r => r.Name == name);

    /// <summary>The types a patch can be given now (name, colour of its first shade, whether it is water): the table without deleted types and the types a rule makes.</summary>
    public List<(string Name, string Colour, bool Water)> PatchTypeChoices()
        => Rows().Where(r => !r.Deleted && r.Effective.Rule == null).Select(r => (r.Name, r.Effective.Colour, r.Effective.Water)).ToList();

    // ---- selection ----

    public bool SelectType(string? name)
    {
        if (name != null && Row(name) == null) { Host.Say($"ground type {name} does not exist", true); return false; }
        if (name != null) FocusPatch = false;
        if (name == SelectedType) return true;
        SelectedType = name;
        Host.MarkPanelDirty();
        Host.SaveUiConfig();
        return true;
    }

    /// <summary>The panel is redrawn: bring back the type remembered in editor_ui.cfg, and drop a selection whose type is gone.</summary>
    internal void SettleSelection(IReadOnlyList<GtRow> rows)
    {
        if (_restore != null) { if (rows.Any(r => r.Name == _restore)) SelectedType = _restore; _restore = null; }
        if (SelectedType != null && rows.All(r => r.Name != SelectedType)) SelectedType = null;
    }

    /// <summary>A click on bare ground in the Ground tool (no patch there): select the type under the cursor (click the same one again, or empty ground, to deselect).</summary>
    public void Pick(Vector3 hit)
    {
        var type = Overlay.TypeAt(new Vector2(hit.X, hit.Z));
        if (type == null) { SelectType(null); Host.Say("no ground area there (a property, or the road)"); return; }
        if (Row(type) == null) { Host.Say($"{type} is not a type of this map any more", true); return; }
        SelectType(type == SelectedType ? null : type);
    }

    // ---- what a patch needs ----

    /// <summary>Why a patch cannot be drawn now (null: it can): it is of the selected ground type, which must be there and usable.</summary>
    public string? CannotDrawPatch()
    {
        if (SelectedType is not { } t) return "pick a ground type in the list first: a patch is drawn of the selected type";
        if (Row(t) is not { } r) return $"{t} is not a ground type of this map any more";
        if (r.Deleted) return $"{t} is deleted: Reset to rules brings it back, or pick another type";
        if (r.Effective.Rule != null) return $"{t} is made by a rule ({r.Effective.Rule}); a patch cannot be of it";
        return null;
    }

    public bool PatchTypeIsWater(string? type) => type != null && Row(type)?.Effective.Water == true;

    // ---- keys ----

    /// <summary>Ground tool keys besides the shape keys (not reached while a text field has the focus). True when used.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (Host.Tool != EditorTool.Ground || Host.CheatSheetShown || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.N when k.ShiftPressed: Panel.FocusName(); return true;
            case Key.M: Overlay.Toggle(); return true;
            case Key.Delete when SelectedType != null: Ops.RequestDelete(); return true;
            case Key.Tab when Host.GetViewport().GuiGetFocusOwner() == null: Panel.FocusList(); return true;
        }
        return false;
    }

    public string HintText() => Host.Shapes.ShapeHintText()
        ?? (SelectedType == null
            ? "ground: Tab or click the list, click the ground to pick its type or a patch, N draws a patch of the selected type, M shows or hides the map, Shift+N a new type, Ctrl+P commands, Esc leaves the tool"
            : $"{SelectedType}: N draws a patch of it, Tab moves between the fields, Space ticks an attribute, Enter applies a typed value, Del deletes the type, Ctrl+Z undoes, Esc deselects");

    /// <summary>Esc: one level up (the type; the patch and the drawing went first, the tool is left by the main handler). True when it deselected.</summary>
    public bool Escape()
    {
        if (SelectedType != null) { SelectType(null); return true; }
        if (Host.Shapes.SelectedPatchId != null) { Host.Shapes.SelectShape(ShapeKind.Patch, null); return true; } // a patch left selected while the type had the focus
        return false;
    }

    // ---- pointing ----

    /// <summary>Hover in the Ground tool: the patch or type under the cursor, as a label near it. Null: nothing to say.</summary>
    public string? HoverText(Vector3 point)
    {
        if (PatchHoverText(point) is { } patch) return patch;
        var type = Overlay.TypeAt(new Vector2(point.X, point.Z));
        if (type == null) return "no ground area here (a property, or the road)";
        return $"{type}  ({Overlay.AreaText(type)} on the map)  click to select";
    }

    /// <summary>The patch under the cursor of the Ground tool, for the hover label.</summary>
    string? PatchHoverText(Vector3 point)
    {
        var (x, y) = Host.Map.RawAbs(point);
        if (Host.Shapes.ShapeAt(ShapeKind.Patch, x, y) is not { } id || Host.Shapes.Patch(id) is not { } p) return null;
        return $"{p.Id}  patch of {p.Type}{(EditorTheme.CropName(p.Type) is { } crop ? $" (crop: {crop})" : "")}{(p.WaterY is { } w ? $", water {w:0.0} m" : "")}  click to select";
    }

    /// <summary>
    /// The one outline of the Ground tool besides the selected patch: the patch under the cursor, lighter and thinner than the selection
    /// (crop types in their crop colour, others white). Not drawn for the selected patch (its own outline is stronger) nor while a
    /// gesture is under way. <paramref name="point"/> null: nothing hovered. Rebuilt only when the hovered patch changes.
    /// </summary>
    public void UpdateHoverOutline(Vector3? point)
    {
        PatchInfo? hot = null;
        if (point is { } pt)
        {
            var (x, y) = Host.Map.RawAbs(pt);
            if (Host.Shapes.ShapeAt(ShapeKind.Patch, x, y) is { } id && id != Host.Shapes.SelectedPatchId) hot = Host.Shapes.Patch(id);
        }
        if (ReferenceEquals(hot, _hoverPatch)) return;
        _hoverPatch = hot;
        var colour = hot != null && EditorTheme.CropColour(hot.Type) is { } c ? c : EditorTheme.Text;
        Host.SetOutline(OutlineHover, hot == null ? new() : Host.RingSegs(hot.Points, true), new Color(colour, HoverAlpha), 0.27f, HoverWidth);
    }
}

using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The ground map of the Ground tool: every ground area of the map drawn on the terrain in its type's colour, so you can see
/// what is where. The areas are those of the last build (parcels.json `ground_areas`, <see cref="MapLoader.GroundAreas"/>) plus the
/// hand-drawn ground patches (ground_patches of edits.json; they show before the next build). Selecting a type in the list brightens its
/// areas and dims the rest (fill only: no outline round every area; the hovered or selected patch alone is outlined, see <see cref="GroundTool"/>); hovering the ground names the patch or type under the cursor and clicking it selects it. M toggles
/// the overlay (remembered in editor_ui.cfg). The mesh is built one type per frame (a labelled progress line says so) and is only
/// rebuilt when a patch changes; a colour or selection change only touches the materials. Nothing is saved here. The area meshes
/// (<see cref="AreaSurfaces"/>) are also what the Zones overlay draws.
/// </summary>
public sealed class GroundMap
{
    const float Fill = 0.40f, FillSelected = 0.62f, FillDim = 0.16f, EdgeDim = 0.45f;

    readonly IEditorHost _host;
    readonly GroundTool _tool;

    MeshInstance3D? _mesh;
    bool _indexed;
    CheckBox _check = null!;
    Label _note = null!;
    object? _disk, _rows;
    string _sig = "";
    int _buildStep;
    readonly TriangleBudget _budget = new();
    List<(string Type, List<Vector2[]> Rings, bool Drawn)> _entries = new();
    List<string> _order = new();
    readonly Dictionary<string, AreaSet> _types = new();
    ArrayMesh? _arrayMesh;

    public GroundMap(IEditorHost host, GroundTool tool)
    {
        _host = host;
        _tool = tool;
    }

    /// <summary>The overlay is on (remembered in editor_ui.cfg).</summary>
    public bool On { get; set; } = true;

    public void Toggle() => Set(!On);

    public void Set(bool on, bool save = true)
    {
        On = on;
        if (_check != null) _check.SetPressedNoSignal(on);
        if (_mesh != null) _mesh.Visible = on && _host.Tool == EditorTool.Ground;
        _host.MarkPanelDirty();
        _host.Say(on ? "ground map on: every ground area in its type's colour (M hides it)" : "ground map hidden (M shows it)");
        if (save) _host.SaveUiConfig();
    }

    /// <summary>The areas of a type as a short phrase for its list row and tooltip.</summary>
    public string AreaText(string name)
    {
        EnsureIndex();
        if (!_types.TryGetValue(name, out var t) || t.Areas + t.Drawn == 0) return "none on the map";
        var n = t.Areas + t.Drawn;
        var ha = t.AreaM2 / 10000f;
        return $"{n} area{(n == 1 ? "" : "s")}, {(ha >= 10 ? $"{ha:F0}" : $"{ha:F1}")} ha";
    }

    public void BuildToggle(VBoxContainer page)
    {
        _check = new CheckBox
        {
            Text = "Show areas on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = On,
            TooltipText = "Draw every ground area on the terrain in its type's colour (M). A selected type is brightened, the others dimmed. Hover names the type and outlines the patch under the cursor, click selects it.",
        };
        _check.Toggled += on => Set(on);
        page.AddChild(_check);
        _note = Ui.Note("");
        page.AddChild(_note);
    }

    // ---- the index: which areas there are (baked + drawn), per type ----

    /// <summary>The effective colour of a type for the overlay (grey if the type is unknown).</summary>
    Color ColourOf(string type) => _tool.Row(type) is { } r && EditSchema.ParseColour(r.Effective.Colour) is { } c ? Color.FromHtml(c) : new Color(0.5f, 0.5f, 0.5f);

    void EnsureIndex()
    {
        var map = _host.Map;
        var disk = _host.Edits.Disk();
        var rows = _tool.Rows();
        if (_indexed && ReferenceEquals(disk, _disk) && ReferenceEquals(rows, _rows)) return;
        _indexed = true; _disk = disk; _rows = rows;

        var entries = new List<(string, List<Vector2[]>, bool)>();
        foreach (var a in map.GroundAreas) entries.Add((a.Kind, a.Rings.ToList(), false));
        if (map.OriginEpsg5514 != null)
            foreach (var p in _host.Shapes.PatchList())
                entries.Add((p.Type, new List<Vector2[]> { p.Points.Select(c => map.FromAbsolute(c.x, c.y)).ToArray() }, true));
        // geometry only changes with a patch (its type or corners) or a new map; colours and selection only touch the materials
        var sig = map.GroundAreas.Count + "#" + string.Join(";", entries.Where(e => e.Item3).Select(e => e.Item1 + ":" + string.Join(",", e.Item2[0].Select(v => $"{v.X:F1},{v.Y:F1}"))));
        var geometryChanged = sig != _sig;
        _sig = sig;

        _entries = entries;
        var prev = _types.ToDictionary(p => p.Key, p => (p.Value.Fill, p.Value.Edge));
        _types.Clear();
        foreach (var (type, rings, drawn) in entries)
        {
            if (!_types.TryGetValue(type, out var t)) _types[type] = t = new AreaSet { Name = type };
            if (drawn) t.Drawn++; else t.Areas++;
            AreaSurfaces.Split(rings, t, out var area);
            t.AreaM2 += area;
        }
        if (geometryChanged) { _buildStep = 0; _budget.Count = 0; _budget.Truncated = false; _arrayMesh = null; if (_mesh != null) _mesh.Mesh = null; }
        else foreach (var (name, t) in _types) if (prev.TryGetValue(name, out var m)) { t.Fill = m.Fill; t.Edge = m.Edge; }
        _order = _types.Keys.OrderBy(k => k == map.GroundDefault ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>The ground type at a ground point (x, z): a patch first (they win on the next build), then the baked areas. Null: no ground area (a property, or no data).</summary>
    public string? TypeAt(Vector2 p)
    {
        EnsureIndex();
        foreach (var t in _types.Values.Where(t => t.Drawn > 0))
            if (AreaSurfaces.Hit(t, p) && DrawnAt(t.Name, p)) return t.Name;
        foreach (var t in _types.Values)
            if (t.Areas > 0 && AreaSurfaces.Hit(t, p)) return t.Name;
        return null;
    }

    bool DrawnAt(string type, Vector2 p) => _entries.Any(e => e.Drawn && e.Type == type && AreaSurfaces.PointInRing(e.Rings[0], p));

    // ---- the overlay ----

    /// <summary>Per frame while the tool is open: index, build one type of the mesh, and keep colours and selection in the materials.</summary>
    public void Update()
    {
        var show = _host.Tool == EditorTool.Ground && On && _host.Map.OriginEpsg5514 != null;
        if (_mesh != null) _mesh.Visible = show;
        if (_host.Tool != EditorTool.Ground) return;
        EnsureIndex();
        if (_note != null) _note.Text = Note();
        if (!show || _order.Count == 0) return;
        if (_mesh == null)
        {
            _mesh = new MeshInstance3D { Name = "GroundMap", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _host.OutlineRoot.AddChild(_mesh);
        }
        if (_buildStep < _order.Count)
        {
            _arrayMesh ??= new ArrayMesh();
            if (_buildStep == 0 && _arrayMesh.GetSurfaceCount() > 0) _arrayMesh = new ArrayMesh();
            var t = _types[_order[_buildStep]];
            AreaSurfaces.Build(_host.Map, _arrayMesh, t, ColourOf(t.Name), Fill, 2, _budget, edges: false); // fill only: an outline round every field is noise; the hovered and selected patch get one
            _buildStep++;
            _mesh.Mesh = _arrayMesh;
        }
        ApplyMaterials();
    }

    string Note()
    {
        if (_host.Map.GroundAreas.Count == 0 && _types.Values.All(t => t.Drawn == 0))
            return "No ground areas in this map's parcels.json (built before they were listed). Build it again (make map), then reopen the editor.";
        if (!On) return "The map overlay is off.";
        if (_buildStep < _order.Count) return $"Drawing ground areas {_buildStep + 1}/{_order.Count}...";
        var drawn = _types.Values.Sum(t => t.Drawn);
        return $"{_types.Values.Sum(t => t.Areas + t.Drawn)} areas of {_types.Count} types as of the last build" + (drawn > 0 ? $", {drawn} patch{(drawn == 1 ? "" : "es")} shown before it" : "")
            + (_budget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    void ApplyMaterials()
    {
        var sel = _tool.SelectedType;
        foreach (var t in _types.Values)
        {
            var colour = ColourOf(t.Name);
            var isSel = sel != null && t.Name == sel;
            var dim = sel != null && !isSel;
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, isSel ? FillSelected : dim ? FillDim : Fill);
            if (t.Edge != null) t.Edge.AlbedoColor = isSel ? new Color(1, 1, 1, 0.95f) : new Color(colour, dim ? EdgeDim : 0.95f);
        }
    }
}

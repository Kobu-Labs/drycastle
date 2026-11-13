using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// The attribute layers (ADR 0015): the hand-drawn regions whose ground stack adds attributes that add up (spawning, events)
/// are drawn as a translucent overlay on the terrain, with a thin edge in the same colour, so a layer never hides the ground
/// it lies on (a type with only an Events attribute over forest leaves the forest as it is). The colour is the region's own
/// (the overlay colour; new layer regions start with the attribute's tint). V toggles the overlay, the View page's Layers
/// section and the Regions page have the switch; the choice is remembered (editor_ui.cfg). The mesh is rebuilt when the
/// edits file or the ground types change (they are keyed by the identity of what was read), and nothing is saved here.
/// </summary>
public partial class EditorMode
{
    const float LayerLift = 0.45f;       // above the terrain; above the region paint (0.3) and the aerial photo (0.25)
    const float LayerFillAlpha = 0.34f;
    const float LayerEdgeAlpha = 0.95f;
    const float LayerEdgeWidth = 0.4f;
    const float LayerMaxEdge = 4f;       // metres: the terrain's own cells are 4 m, so the overlay follows them
    const int LayerMaxTriangles = 200000;

    MeshInstance3D? _layersMesh;
    bool _layersOn = true, _layersBuilt;
    object? _layersDisk, _layersTable;
    int _layerRegions;
    CheckBox _layersCheck = null!;
    Label _layersNote = null!;

    public bool LayersShown => _layersOn;

    /// <summary>The regions that are drawn as a layer: those whose stack has a type that adds spawning or events (not counting the type that decides the terrain).</summary>
    public IReadOnlyList<string> LayerRegionIds() => RegionList().Where(r => !r.Water && r.Ground.Count > 0 && StackRoles(r.Ground).Layers.Count > 0).Select(r => r.Id).ToList();

    public void ToggleLayers() => SetLayers(!_layersOn);

    public void SetLayers(bool on, bool save = true)
    {
        _layersOn = on;
        if (_layersCheck != null) _layersCheck.SetPressedNoSignal(on);
        if (_rgLayersCheck != null) _rgLayersCheck.SetPressedNoSignal(on);
        if (_layersMesh != null) _layersMesh.Visible = on;
        _layersBuilt = false; // build it now if it is wanted and was never built
        if (on) Say(_layerRegions == 0 ? "attribute layers on (no region adds spawning or events yet)" : $"attribute layers on: {_layerRegions} region{(_layerRegions == 1 ? "" : "s")} (V hides them)");
        else Say("attribute layers hidden (V shows them)");
        if (save) SaveUiConfig();
    }

    /// <summary>The View page's switch (the same one the Regions page has).</summary>
    void BuildLayersToggle(VBoxContainer page)
    {
        _layersCheck = new CheckBox
        {
            Text = "Attribute layers  (V)", FocusMode = Control.FocusModeEnum.None, ButtonPressed = _layersOn,
            TooltipText = "Draw the regions that add spawning or events as a translucent overlay on the terrain (V). They never hide the ground.",
        };
        _layersCheck.Toggled += on => SetLayers(on);
        page.AddChild(_layersCheck);
        _layersNote = new Label { ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "No region adds spawning or events yet." };
        page.AddChild(_layersNote);
    }

    /// <summary>Per frame: rebuild the overlay when the edits or the ground types changed (cheap otherwise).</summary>
    void UpdateLayers()
    {
        var disk = Disk();
        var table = Map.GroundTypeTable;
        var want = _layersOn && Map.OriginEpsg5514 != null;
        if (_layersBuilt && ReferenceEquals(disk, _layersDisk) && ReferenceEquals(table, _layersTable)) return;
        _layersBuilt = true;
        _layersDisk = disk;
        _layersTable = table;
        var ids = LayerRegionIds();
        _layerRegions = ids.Count;
        if (_layersNote != null)
            _layersNote.Text = ids.Count == 0 ? "No region adds spawning or events yet." : $"{ids.Count} region{(ids.Count == 1 ? "" : "s")} add spawning or events: {string.Join(", ", ids.Take(6))}{(ids.Count > 6 ? ", ..." : "")}.";
        if (Map.OriginEpsg5514 == null || ids.Count == 0 || !want && _layersMesh == null)
        {
            if (_layersMesh != null) _layersMesh.Mesh = null;
            return;
        }
        var items = RegionList().Where(r => ids.Contains(r.Id)).ToList();
        if (_layersMesh == null)
        {
            _layersMesh = new MeshInstance3D { Name = "AttributeLayers", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            OutlineRoot.AddChild(_layersMesh); // follows the editor's visibility: gone in walk mode
        }
        _layersMesh.Mesh = BuildLayerMesh(items);
        _layersMesh.Visible = _layersOn;
    }

    static StandardMaterial3D LayerMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = 2,
    };

    /// <summary>One surface of translucent triangles draped on the terrain and one of edge ribbons, both in the colour of each region.</summary>
    ArrayMesh BuildLayerMesh(List<RegionInfo> regions)
    {
        var fill = new List<Vector3>(); var fillCol = new List<Color>();
        var edge = new List<Vector3>(); var edgeCol = new List<Color>(); var edgeIdx = new List<int>();
        Vector3 Up(Vector2 p, float extra = 0f) => new(p.X, Map.GroundHeightOrMean(p.X, p.Y) + LayerLift + extra, p.Y);

        void Triangle(Vector2 a, Vector2 b, Vector2 c, Color col, int depth)
        {
            // split the longest edge until every edge is within one terrain cell, so the triangle follows the ground
            var ab = a.DistanceTo(b); var bc = b.DistanceTo(c); var ca = c.DistanceTo(a);
            var longest = Mathf.Max(ab, Mathf.Max(bc, ca));
            if (longest > LayerMaxEdge && depth < 12 && fill.Count < LayerMaxTriangles * 3)
            {
                if (ab >= bc && ab >= ca) { var m = (a + b) / 2; Triangle(a, m, c, col, depth + 1); Triangle(m, b, c, col, depth + 1); }
                else if (bc >= ca) { var m = (b + c) / 2; Triangle(a, b, m, col, depth + 1); Triangle(a, m, c, col, depth + 1); }
                else { var m = (c + a) / 2; Triangle(a, b, m, col, depth + 1); Triangle(m, b, c, col, depth + 1); }
                return;
            }
            fill.Add(Up(a)); fill.Add(Up(b)); fill.Add(Up(c));
            fillCol.Add(col); fillCol.Add(col); fillCol.Add(col);
        }

        foreach (var r in regions)
        {
            var baseColour = Color.FromHtml(r.Colour);
            var ring = r.Points.Select(p => FromAbsolute(p.x, p.y)).ToArray();
            var fillColour = new Color(baseColour, LayerFillAlpha);
            var edgeColour = new Color(baseColour, LayerEdgeAlpha);
            var tri = Geometry2D.TriangulatePolygon(ring);
            for (var i = 0; i + 2 < tri.Length; i += 3) Triangle(ring[tri[i]], ring[tri[i + 1]], ring[tri[i + 2]], fillColour, 0);
            // the edge: a ribbon along each side, split every 2 m so it follows the ground too
            for (var i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var dir = (b - a).Normalized();
                if (!float.IsFinite(dir.X)) continue;
                var side = new Vector2(-dir.Y, dir.X) * (LayerEdgeWidth / 2f);
                var n = Mathf.Max(1, (int)Mathf.Ceil(a.DistanceTo(b) / 2f));
                for (var k = 0; k < n; k++)
                {
                    var p0 = a.Lerp(b, (float)k / n); var p1 = a.Lerp(b, (float)(k + 1) / n);
                    var at = edge.Count;
                    edge.Add(Up(p0 + side, 0.02f)); edge.Add(Up(p0 - side, 0.02f)); edge.Add(Up(p1 + side, 0.02f)); edge.Add(Up(p1 - side, 0.02f));
                    for (var q = 0; q < 4; q++) edgeCol.Add(edgeColour);
                    edgeIdx.AddRange(new[] { at, at + 1, at + 2, at + 1, at + 3, at + 2 });
                }
            }
        }

        var mesh = new ArrayMesh();
        if (fill.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = fill.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = fillCol.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, LayerMaterial());
        }
        if (edge.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = edge.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = edgeCol.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = edgeIdx.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var m = LayerMaterial();
            m.RenderPriority = 3;
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, m);
        }
        return mesh;
    }

    // test hooks
    public bool LayerMeshBuilt => _layersMesh?.Mesh is ArrayMesh { } m && m.GetSurfaceCount() > 0;
    public int LayerRegionCount => _layerRegions;
    /// <summary>Run the overlay update now (smoke tests; normally per frame).</summary>
    public void LayersNow() { _layersBuilt = false; UpdateLayers(); }
}

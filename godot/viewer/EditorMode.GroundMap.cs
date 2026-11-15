using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Drycastle.MapLoader;

/// <summary>
/// The ground map of the Ground tool: every ground area of the map drawn on the terrain in its type's colour, so you can see
/// what is where. The areas are those of the last build (parcels.json `ground_areas`, <see cref="MapLoader.GroundAreas"/>) plus the
/// hand-drawn ground patches (ground_patches of edits.json; they show before the next build). Selecting a type in the list brightens its
/// areas (white edge) and dims the rest; hovering the ground names the patch or type under the cursor and clicking it selects it. M toggles
/// the overlay (remembered in editor_ui.cfg). The mesh is built one type per frame (a labelled progress line says so) and is only
/// rebuilt when a patch changes; a colour or selection change only touches the materials. Nothing is saved here. The area meshes
/// (<see cref="BuildAreaSurfaces"/>) are also what the Zones overlay draws.
/// </summary>
public partial class EditorMode
{
    const float GroundMapLift = 1.0f;       // above the terrain; the 12 m triangles do not follow every bump, so it floats a little
    const float GroundMapMaxEdge = 12f;
    const float GroundMapEdgeWidth = 1.2f;
    const int GroundMapMaxTriangles = 260000;
    const float GroundMapFill = 0.40f, GroundMapFillSelected = 0.62f, GroundMapFillDim = 0.16f, GroundMapEdgeDim = 0.45f;

    sealed class TriangleBudget { public int Count; public bool Truncated; }

    sealed class GroundMapType
    {
        public string Name = "";
        public int Areas, Drawn;
        public float AreaM2;
        public readonly List<Vector2[]> Outers = new();                 // for the point test: one entry per outer ring
        public readonly List<List<Vector2[]>> Holes = new();            // the holes of each outer
        public StandardMaterial3D? Fill, Edge;
    }

    MeshInstance3D? _gmMesh;
    bool _gmOn = true, _gmIndexed;
    CheckBox _gmCheck = null!;
    Label _gmNote = null!;
    object? _gmDisk, _gmRows;
    string _gmSig = "";
    int _gmBuildStep;
    readonly TriangleBudget _gmBudget = new();
    List<(string Type, List<Vector2[]> Rings, bool Drawn)> _gmEntries = new();
    List<string> _gmOrder = new();
    readonly Dictionary<string, GroundMapType> _gmTypes = new();
    ArrayMesh? _gmArrayMesh;

    public bool GroundMapShown => _gmOn;
    public bool GroundMapBuilt => _gmArrayMesh != null && _gmBuildStep >= _gmOrder.Count && _gmOrder.Count > 0;
    public int GroundMapAreas(string type) => _gmTypes.TryGetValue(type, out var t) ? t.Areas + t.Drawn : 0;

    public void ToggleGroundMap() => SetGroundMap(!_gmOn);

    public void SetGroundMap(bool on, bool save = true)
    {
        _gmOn = on;
        if (_gmCheck != null) _gmCheck.SetPressedNoSignal(on);
        if (_gmMesh != null) _gmMesh.Visible = on && Tool == EditorTool.Ground;
        _panelDirty = true;
        Say(on ? "ground map on: every ground area in its type's colour (M hides it)" : "ground map hidden (M shows it)");
        if (save) SaveUiConfig();
    }

    /// <summary>The areas of a type as a short phrase for its list row and tooltip.</summary>
    string GroundAreaText(string name)
    {
        EnsureGroundMapIndex();
        if (!_gmTypes.TryGetValue(name, out var t) || t.Areas + t.Drawn == 0) return "none on the map";
        var n = t.Areas + t.Drawn;
        var ha = t.AreaM2 / 10000f;
        return $"{n} area{(n == 1 ? "" : "s")}, {(ha >= 10 ? $"{ha:F0}" : $"{ha:F1}")} ha";
    }

    void BuildGroundMapToggle(VBoxContainer page)
    {
        _gmCheck = new CheckBox
        {
            Text = "Show areas on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = _gmOn,
            TooltipText = "Draw every ground area on the terrain in its type's colour (M). A selected type is brightened, the others dimmed. Hover names the type, click selects it.",
        };
        _gmCheck.Toggled += on => SetGroundMap(on);
        page.AddChild(_gmCheck);
        _gmNote = Note("");
        page.AddChild(_gmNote);
    }

    // ---- the index: which areas there are (baked + drawn), per type ----

    /// <summary>The effective colour of a type for the overlay (grey if the type is unknown).</summary>
    Color GroundMapColour(string type) => GroundRow(type) is { } r && ParseColour(r.Effective.Colour) is { } c ? Color.FromHtml(c) : new Color(0.5f, 0.5f, 0.5f);

    void EnsureGroundMapIndex()
    {
        var disk = Disk();
        var rows = GroundRows();
        if (_gmIndexed && ReferenceEquals(disk, _gmDisk) && ReferenceEquals(rows, _gmRows)) return;
        _gmIndexed = true; _gmDisk = disk; _gmRows = rows;

        var entries = new List<(string, List<Vector2[]>, bool)>();
        foreach (var a in Map.GroundAreas) entries.Add((a.Kind, a.Rings.ToList(), false));
        if (Map.OriginEpsg5514 != null)
            foreach (var p in PatchList())
                entries.Add((p.Type, new List<Vector2[]> { p.Points.Select(c => FromAbsolute(c.x, c.y)).ToArray() }, true));
        // geometry only changes with a patch (its type or corners) or a new map; colours and selection only touch the materials
        var sig = Map.GroundAreas.Count + "#" + string.Join(";", entries.Where(e => e.Item3).Select(e => e.Item1 + ":" + string.Join(",", e.Item2[0].Select(v => $"{v.X:F1},{v.Y:F1}"))));
        var geometryChanged = sig != _gmSig;
        _gmSig = sig;

        _gmEntries = entries;
        var prev = _gmTypes.ToDictionary(p => p.Key, p => (p.Value.Fill, p.Value.Edge));
        _gmTypes.Clear();
        foreach (var (type, rings, drawn) in entries)
        {
            if (!_gmTypes.TryGetValue(type, out var t)) _gmTypes[type] = t = new GroundMapType { Name = type };
            if (drawn) t.Drawn++; else t.Areas++;
            SplitRings(rings, t, out var area);
            t.AreaM2 += area;
        }
        if (geometryChanged) { _gmBuildStep = 0; _gmBudget.Count = 0; _gmBudget.Truncated = false; _gmArrayMesh = null; if (_gmMesh != null) _gmMesh.Mesh = null; }
        else foreach (var (name, t) in _gmTypes) if (prev.TryGetValue(name, out var m)) { t.Fill = m.Fill; t.Edge = m.Edge; }
        _gmOrder = _gmTypes.Keys.OrderBy(k => k == Map.GroundDefault ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>Sort the rings of one area into outers and holes by how many other rings enclose them (even depth: outer; odd: a hole of the smallest outer around it).</summary>
    static void SplitRings(List<Vector2[]> rings, GroundMapType t, out float area)
    {
        area = 0f;
        var outers = new List<(Vector2[] Ring, float Area)>();
        var holes = new List<Vector2[]>();
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            var depth = rings.Count(o => !ReferenceEquals(o, ring) && o.Length >= 3 && PointInRing(o, ring[0]));
            if (depth % 2 == 0) outers.Add((ring, Mathf.Abs(ParcelInfo.RingArea(ring)))); else holes.Add(ring);
        }
        foreach (var (ring, a) in outers) { t.Outers.Add(ring); t.Holes.Add(new List<Vector2[]>()); area += a; }
        foreach (var h in holes)
        {
            var best = -1; var bestArea = float.MaxValue;
            for (var i = t.Outers.Count - outers.Count; i < t.Outers.Count; i++)
            {
                var a = Mathf.Abs(ParcelInfo.RingArea(t.Outers[i]));
                if (a < bestArea && PointInRing(t.Outers[i], h[0])) { best = i; bestArea = a; }
            }
            if (best >= 0) { t.Holes[best].Add(h); area -= Mathf.Abs(ParcelInfo.RingArea(h)); }
        }
    }

    static bool PointInRing(Vector2[] ring, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X) inside = !inside;
        return inside;
    }

    /// <summary>The ground type at a ground point (x, z): a patch first (they win on the next build), then the baked areas. Null: no ground area (a property, or no data).</summary>
    public string? GroundTypeAtPoint(Vector2 p)
    {
        EnsureGroundMapIndex();
        foreach (var t in _gmTypes.Values.Where(t => t.Drawn > 0))
            if (HitType(t, p) && DrawnAt(t.Name, p)) return t.Name;
        foreach (var t in _gmTypes.Values)
            if (t.Areas > 0 && HitType(t, p)) return t.Name;
        return null;
    }

    bool DrawnAt(string type, Vector2 p) => _gmEntries.Any(e => e.Drawn && e.Type == type && PointInRing(e.Rings[0], p));

    static bool HitType(GroundMapType t, Vector2 p)
    {
        for (var i = 0; i < t.Outers.Count; i++)
            if (PointInRing(t.Outers[i], p) && !t.Holes[i].Any(h => PointInRing(h, p))) return true;
        return false;
    }

    // ---- the overlay ----

    /// <summary>Per frame while the tool is open: index, build one type of the mesh, and keep colours and selection in the materials.</summary>
    void UpdateGroundMap()
    {
        var show = Tool == EditorTool.Ground && _gmOn && Map.OriginEpsg5514 != null;
        if (_gmMesh != null) _gmMesh.Visible = show;
        if (Tool != EditorTool.Ground) return;
        EnsureGroundMapIndex();
        if (_gmNote != null) _gmNote.Text = GroundMapNote();
        if (!show || _gmOrder.Count == 0) return;
        if (_gmMesh == null)
        {
            _gmMesh = new MeshInstance3D { Name = "GroundMap", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            OutlineRoot.AddChild(_gmMesh);
        }
        if (_gmBuildStep < _gmOrder.Count)
        {
            _gmArrayMesh ??= new ArrayMesh();
            if (_gmBuildStep == 0 && _gmArrayMesh.GetSurfaceCount() > 0) _gmArrayMesh = new ArrayMesh();
            var t = _gmTypes[_gmOrder[_gmBuildStep]];
            BuildAreaSurfaces(_gmArrayMesh, t, GroundMapColour(t.Name), GroundMapFill, 2, _gmBudget);
            _gmBuildStep++;
            _gmMesh.Mesh = _gmArrayMesh;
        }
        ApplyGroundMapMaterials();
    }

    string GroundMapNote()
    {
        if (Map.GroundAreas.Count == 0 && _gmTypes.Values.All(t => t.Drawn == 0))
            return "No ground areas in this map's parcels.json (built before they were listed). Build it again (make map), then reopen the editor.";
        if (!_gmOn) return "The map overlay is off.";
        if (_gmBuildStep < _gmOrder.Count) return $"Drawing ground areas {_gmBuildStep + 1}/{_gmOrder.Count}...";
        var drawn = _gmTypes.Values.Sum(t => t.Drawn);
        return $"{_gmTypes.Values.Sum(t => t.Areas + t.Drawn)} areas of {_gmTypes.Count} types as of the last build" + (drawn > 0 ? $", {drawn} patch{(drawn == 1 ? "" : "es")} shown before it" : "")
            + (_gmBudget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    void ApplyGroundMapMaterials()
    {
        var sel = SelectedGroundType;
        foreach (var t in _gmTypes.Values)
        {
            var colour = GroundMapColour(t.Name);
            var isSel = sel != null && t.Name == sel;
            var dim = sel != null && !isSel;
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, isSel ? GroundMapFillSelected : dim ? GroundMapFillDim : GroundMapFill);
            if (t.Edge != null) t.Edge.AlbedoColor = isSel ? new Color(1, 1, 1, 0.95f) : new Color(colour, dim ? GroundMapEdgeDim : 0.95f);
        }
    }

    static StandardMaterial3D GroundMapMaterial(int priority) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = priority,
    };

    /// <summary>One fill surface and one edge surface for the areas of <paramref name="t"/> (its materials are stored on it). Polygons with holes are cut open along a thin slit (Clipper) so they triangulate.</summary>
    void BuildAreaSurfaces(ArrayMesh mesh, GroundMapType t, Color colour, float fillAlpha, int priority, TriangleBudget budget)
    {
        var fill = new List<Vector3>();
        var edge = new List<Vector3>(); var edgeIdx = new List<int>();
        Vector3 Up(Vector2 p, float extra = 0f) => new(p.X, Map.GroundHeightOrMean(p.X, p.Y) + GroundMapLift + extra, p.Y);

        void Triangle(Vector2 a, Vector2 b, Vector2 c, int depth)
        {
            var ab = a.DistanceTo(b); var bc = b.DistanceTo(c); var ca = c.DistanceTo(a);
            var longest = Mathf.Max(ab, Mathf.Max(bc, ca));
            if (longest > GroundMapMaxEdge && depth < 14)
            {
                if (budget.Count >= GroundMapMaxTriangles) { budget.Truncated = true; return; }
                if (ab >= bc && ab >= ca) { var m = (a + b) / 2; Triangle(a, m, c, depth + 1); Triangle(m, b, c, depth + 1); }
                else if (bc >= ca) { var m = (b + c) / 2; Triangle(a, b, m, depth + 1); Triangle(a, m, c, depth + 1); }
                else { var m = (c + a) / 2; Triangle(a, b, m, depth + 1); Triangle(m, b, c, depth + 1); }
                return;
            }
            if (budget.Count >= GroundMapMaxTriangles) { budget.Truncated = true; return; }
            fill.Add(Up(a)); fill.Add(Up(b)); fill.Add(Up(c));
            budget.Count++;
        }

        void Ribbon(Vector2[] ring)
        {
            for (var i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var dir = (b - a).Normalized();
                if (!float.IsFinite(dir.X)) continue;
                var side = new Vector2(-dir.Y, dir.X) * (GroundMapEdgeWidth / 2f);
                var n = Mathf.Max(1, (int)Mathf.Ceil(a.DistanceTo(b) / 8f));
                for (var k = 0; k < n; k++)
                {
                    var p0 = a.Lerp(b, (float)k / n); var p1 = a.Lerp(b, (float)(k + 1) / n);
                    var at = edge.Count;
                    edge.Add(Up(p0 + side, 0.02f)); edge.Add(Up(p0 - side, 0.02f)); edge.Add(Up(p1 + side, 0.02f)); edge.Add(Up(p1 - side, 0.02f));
                    edgeIdx.AddRange(new[] { at, at + 1, at + 2, at + 1, at + 3, at + 2 });
                }
            }
        }

        for (var i = 0; i < t.Outers.Count; i++)
        {
            var pieces = new List<Vector2[]> { t.Outers[i] };
            foreach (var hole in t.Holes[i])
            {
                // slit from the hole's right-most corner to the east, merged with the hole, cut from the pieces: no hole is left
                var far = hole[0]; foreach (var v in hole) if (v.X > far.X) far = v;
                var reach = 1e5f;
                var slit = new[] { far + new Vector2(-0.05f, -0.05f), far + new Vector2(reach, -0.05f), far + new Vector2(reach, 0.05f), far + new Vector2(-0.05f, 0.05f) };
                var merged = Geometry2D.MergePolygons(hole, slit);
                var cutter = merged.Count > 0 ? merged[0] : hole;
                var next = new List<Vector2[]>();
                foreach (var p in pieces)
                    foreach (var q in Geometry2D.ClipPolygons(p, cutter))
                        if (!Geometry2D.IsPolygonClockwise(q)) next.Add(q); // a clockwise piece would be a hole: the slit reaches the outside, so none is left
                pieces = next.Count > 0 ? next : pieces;
            }
            foreach (var piece in pieces)
            {
                var tri = Geometry2D.TriangulatePolygon(piece);
                for (var k = 0; k + 2 < tri.Length; k += 3) Triangle(piece[tri[k]], piece[tri[k + 1]], piece[tri[k + 2]], 0);
            }
            Ribbon(t.Outers[i]);
            foreach (var h in t.Holes[i]) Ribbon(h);
        }

        if (fill.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = fill.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            t.Fill = GroundMapMaterial(priority);
            t.Fill.AlbedoColor = new Color(colour, fillAlpha);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, t.Fill);
        }
        if (edge.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = edge.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = edgeIdx.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            t.Edge = GroundMapMaterial(priority + 1);
            t.Edge.AlbedoColor = new Color(colour, 0.95f);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, t.Edge);
        }
    }

    // ---- pointing ----

    /// <summary>Hover in the Ground tool: the patch or type under the cursor, as a label near it. Null: nothing to say.</summary>
    string? GroundHoverText(Vector3 point)
    {
        if (PatchHoverText(point) is { } patch) return patch;
        var type = GroundTypeAtPoint(new Vector2(point.X, point.Z));
        if (type == null) return "no ground area here (a property, or the road)";
        return $"{type}  ({GroundAreaText(type)} on the map)  click to select";
    }

    /// <summary>Click on the ground in the Ground tool (no patch there): select the type under the cursor (click the same one again, or empty ground, to deselect).</summary>
    void GroundPick(Vector3 hit)
    {
        var type = GroundTypeAtPoint(new Vector2(hit.X, hit.Z));
        if (type == null) { SelectGroundType(null); Say("no ground area there (a property, or the road)"); return; }
        if (GroundRow(type) == null) { Say($"{type} is not a type of this map any more", true); return; }
        SelectGroundType(type == SelectedGroundType ? null : type);
    }

    // test hooks
    /// <summary>Run the ground map now, until every type is built (smoke tests; normally one type per frame).</summary>
    public void GroundMapNow()
    {
        for (var i = 0; i < 64 && (_gmBuildStep < Math.Max(_gmOrder.Count, 1) || _gmArrayMesh == null); i++) { UpdateGroundMap(); if (_gmOrder.Count == 0) break; }
    }
}

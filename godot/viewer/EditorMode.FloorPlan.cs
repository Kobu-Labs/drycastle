using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// Floor plan of one parcel (B): the buildings of the selected parcel are hidden and what is left on the ground is their plan,
/// to sketch an interior on with the Sketch tool. The plan is a horizontal cut through the walls (so it is the building as it
/// was built: wall lines, recessed doors and windows) over a 1 m grid on the Sketch tool's own grid, with a stronger line
/// every 5 m. The ground under a building was always clickable (the ground ray ignores buildings); this only makes it visible.
/// A view: nothing is saved to the map, the collision stays, and walk mode shows the building again. The parcel is remembered
/// (editor_ui.cfg). B again, or the View page's switch, brings the building back.
/// </summary>
public partial class EditorMode
{
    const string FloorWalls = "floorplan_walls", FloorGrid = "floorplan_grid", FloorGridMajor = "floorplan_grid_major";
    const float FloorCutM = 1.2f;        // above the ground at the middle of the building: through doors and most windows
    const float FloorGridLift = 0.5f;    // above the patch paint (0.3) and the aerial photo (0.25)
    const float FloorWallLift = 0.56f;
    const float FloorMarginM = 3f;
    const int FloorMajorEvery = 5;

    string? _floorParcel, _floorRestore;
    int _floorVersion = -1;
    readonly List<Node3D> _floorHidden = new();
    CheckBox _floorCheck = null!;
    Label _floorNote = null!;

    /// <summary>The parcel shown as a floor plan; null when none is.</summary>
    public string? FloorPlanParcel => _floorParcel;

    public void ToggleFloorPlan()
    {
        if (_floorParcel != null) { SetFloorPlan(null); return; }
        var withModel = Selection.Where(id => Map.Parcels.TryGetValue(id, out var p) && p.HasModel).ToList();
        if (withModel.Count != 1)
        {
            Say(Selection.Count == 0 ? "floor plan: select the parcel of a building first (Select tool, 1), then press B"
                : withModel.Count == 0 ? "floor plan: the selected parcel has no building; select one that has"
                : "floor plan: select one parcel only (it shows one at a time)", true);
            SyncFloorPlanUi();
            return;
        }
        SetFloorPlan(withModel[0]);
    }

    public void SetFloorPlan(string? parcelId, bool save = true)
    {
        if (parcelId != null && (!Map.Parcels.TryGetValue(parcelId, out var info) || !info.HasModel))
        {
            Say($"floor plan: {parcelId} has no building on this map", true);
            parcelId = null;
        }
        ShowFloorBuildings();
        _floorParcel = parcelId;
        _floorVersion = -1; // UpdateFloorPlan builds it
        if (parcelId == null)
        {
            foreach (var n in new[] { FloorWalls, FloorGrid, FloorGridMajor }) SetOutline(n, new(), EditorTheme.Text, 0f, 0.1f);
            Say("floor plan off: the building is back");
        }
        else Say($"floor plan of {parcelId}: the building is hidden, draw on it with the Sketch tool (9); B brings it back");
        SyncFloorPlanUi();
        if (save) SaveUiConfig();
    }

    /// <summary>The remembered parcel, once the map is up (called when the editor first opens).</summary>
    void RestoreFloorPlan()
    {
        if (_floorRestore is not { } id) return;
        _floorRestore = null;
        if (Map.Parcels.TryGetValue(id, out var p) && p.HasModel) SetFloorPlan(id, save: false);
    }

    /// <summary>Leaving the editor shows the building again (walk mode must not walk into an invisible house); coming back hides it.</summary>
    void FloorPlanActive(bool on)
    {
        if (_floorParcel == null) return;
        if (on) _floorVersion = -1; else ShowFloorBuildings();
    }

    void ShowFloorBuildings()
    {
        foreach (var n in _floorHidden) if (GodotObject.IsInstanceValid(n)) n.Visible = true;
        _floorHidden.Clear();
    }

    /// <summary>Per frame: (re)build when the plan was switched on or the map's geometry changed (a re-exported chunk brings new, visible building nodes).</summary>
    void UpdateFloorPlan()
    {
        if (_floorParcel is not { } id || _floorVersion == Map.GeometryVersion) return;
        _floorVersion = Map.GeometryVersion;
        ShowFloorBuildings();
        if (!Map.Parcels.TryGetValue(id, out var info) || Map.ParcelVisual(id) is not { } vis || !GodotObject.IsInstanceValid(vis))
        {
            _floorParcel = null;
            foreach (var n in new[] { FloorWalls, FloorGrid, FloorGridMajor }) SetOutline(n, new(), EditorTheme.Text, 0f, 0.1f);
            Say($"floor plan off: {id} has no building any more", true);
            SyncFloorPlanUi();
            return;
        }
        // the cut, before anything is hidden
        var walls = new List<(Vector2 a, Vector2 b)>();
        var box = new Aabb();
        var first = true;
        foreach (var mi in MeshesOf(vis))
        {
            var xf = mi.GlobalTransform;
            var b = xf * mi.GetAabb();
            box = first ? b : box.Merge(b);
            first = false;
        }
        if (!first)
        {
            var mid = box.GetCenter();
            var cut = Mathf.Clamp(Map.GroundHeightOrMean(mid.X, mid.Z) + FloorCutM, box.Position.Y + 0.3f, box.End.Y - 0.3f);
            foreach (var mi in MeshesOf(vis)) CutMesh(mi, cut, walls);
        }
        // hide the building and its window panes (window_<building>_<id>, siblings in the chunk)
        vis.Visible = false;
        _floorHidden.Add(vis);
        if (vis.GetParent() is { } chunk)
            foreach (var c in chunk.GetChildren())
                if (c is Node3D n && n.Visible && info.BuildingIds.Any(bid => n.Name.ToString().StartsWith($"window_{bid}_"))) { n.Visible = false; _floorHidden.Add(n); }

        SetOutline(FloorWalls, walls, EditorTheme.Text, FloorWallLift, 0.14f);
        var (minor, major) = first || Map.OriginEpsg5514 == null ? (new List<(Vector2, Vector2)>(), new List<(Vector2, Vector2)>()) : FloorGridLines(box);
        SetOutline(FloorGrid, minor, EditorTheme.Dim with { A = 0.35f }, FloorGridLift, 0.03f);
        SetOutline(FloorGridMajor, major, EditorTheme.Dim with { A = 0.7f }, FloorGridLift, 0.07f);
    }

    static IEnumerable<MeshInstance3D> MeshesOf(Node n)
    {
        if (n is MeshInstance3D { Mesh: not null } mi) yield return mi;
        foreach (var c in n.GetChildren())
            foreach (var m in MeshesOf(c)) yield return m;
    }

    /// <summary>Where the triangles of a mesh cross the height <paramref name="y"/>: one segment each, in ground axes (x east, z).</summary>
    static void CutMesh(MeshInstance3D mi, float y, List<(Vector2 a, Vector2 b)> into)
    {
        var xf = mi.GlobalTransform;
        var f = mi.Mesh.GetFaces();
        Span<Vector2> hit = stackalloc Vector2[3];
        for (var i = 0; i + 2 < f.Length; i += 3)
        {
            var n = 0;
            for (var e = 0; e < 3; e++)
            {
                var p = xf * f[i + e];
                var q = xf * f[i + (e + 1) % 3];
                if ((p.Y > y) == (q.Y > y)) continue;
                var t = (y - p.Y) / (q.Y - p.Y);
                hit[n++] = new Vector2(p.X + (q.X - p.X) * t, p.Z + (q.Z - p.Z) * t);
            }
            if (n == 2 && hit[0].DistanceSquaredTo(hit[1]) > 1e-6f) into.Add((hit[0], hit[1]));
        }
    }

    /// <summary>The 1 m grid around the building, on whole metres of the map's absolute frame (where the Sketch tool snaps), and its 5 m lines.</summary>
    (List<(Vector2 a, Vector2 b)> minor, List<(Vector2 a, Vector2 b)> major) FloorGridLines(Aabb box)
    {
        var (ax, ay) = RawAbs(new Vector3(box.Position.X, 0f, box.End.Z));   // south-west corner (y north = -z)
        var (bx, by) = RawAbs(new Vector3(box.End.X, 0f, box.Position.Z));   // north-east corner
        long x0 = (long)Math.Floor(ax - FloorMarginM), x1 = (long)Math.Ceiling(bx + FloorMarginM);
        long y0 = (long)Math.Floor(ay - FloorMarginM), y1 = (long)Math.Ceiling(by + FloorMarginM);
        var minor = new List<(Vector2 a, Vector2 b)>();
        var major = new List<(Vector2 a, Vector2 b)>();
        for (var x = x0; x <= x1; x++) (x % FloorMajorEvery == 0 ? major : minor).Add((FromAbsolute(x, y0), FromAbsolute(x, y1)));
        for (var y = y0; y <= y1; y++) (y % FloorMajorEvery == 0 ? major : minor).Add((FromAbsolute(x0, y), FromAbsolute(x1, y)));
        return (minor, major);
    }

    // ---- the View page section ----

    void BuildFloorPlanToggle(VBoxContainer page)
    {
        _floorCheck = new CheckBox
        {
            Text = "Floor plan of the selected parcel  (B)", FocusMode = Control.FocusModeEnum.None, ButtonPressed = _floorParcel != null,
            TooltipText = "Hide the buildings of the selected parcel and show their plan on a 1 m grid, to sketch an interior on (B). Nothing is saved to the map.",
        };
        _floorCheck.Toggled += _ => ToggleFloorPlan();
        page.AddChild(_floorCheck);
        _floorNote = new Label { ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        page.AddChild(_floorNote);
        SyncFloorPlanUi();
    }

    void SyncFloorPlanUi()
    {
        if (_floorCheck == null) return;
        _floorCheck.SetPressedNoSignal(_floorParcel != null);
        _floorNote.Text = _floorParcel != null
            ? $"{_floorParcel}: building hidden, wall lines and a 1 m grid shown (strong line every 5 m). Draw with the Sketch tool (9)."
            : "Select the parcel of a building, then switch this on.";
    }
}

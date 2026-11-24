using Drycastle.MapLoader.Objects;
using Drycastle.Viewer.Edits;
using Drycastle.Viewer.Poles;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// Plan mode of the Poles tool (P): click where poles should stand (left click) and where houses are to be connected
/// (right click on a wall or roof), then Build poles (Enter) places a pole at every point, strings the cables along the
/// roads and connects the houses, in one save. What gets built is PolePlanner's answer, drawn while planning: cyan for
/// what will be built, red for a house with no pole to go to. The plan is kept in memory only.
/// </summary>
public sealed class PolePlan
{
    sealed record PlanPoint(bool House, double X, double Y, BuildingHit? Hit);

    const double PlanPickM = 1.0;

    readonly IEditorHost _host;
    readonly PolesTool _poles;
    readonly List<PlanPoint> _plan = new();
    PolePlanner.Graph? _roads;
    PolePlanner.Result? _planResult;
    bool _planDirty = true;
    MeshInstance3D _ok = null!, _bad = null!;

    public PolePlan(IEditorHost host, PolesTool poles)
    {
        _host = host;
        _poles = poles;
    }

    public bool Planning { get; private set; }
    public int PoleCount => _plan.Count(p => !p.House);
    public int HouseCount => _plan.Count(p => p.House);
    internal bool Empty => _plan.Count == 0;

    public void Start()
    {
        if (_host.Map.OriginEpsg5514 == null) { _host.Say("this map has no origin_epsg5514", true); return; }
        _host.Objects.DisarmPlacement(false);
        _poles.StopConnecting(false);
        if (_host.Fence.Gates.Armed) _host.Fence.Gates.Cancel();
        _poles.SelectCable(null);
        _host.Objects.SelectObject(null);
        Planning = true;
        _host.Say("planning: left click where poles go, right click a house to connect it, Enter builds");
        _host.MarkPanelDirty();
    }

    public void Stop(bool say = true)
    {
        if (!Planning) return;
        Planning = false;
        if (say) _host.Say(_plan.Count > 0 ? "planning off; the plan is kept" : "planning off");
        _host.MarkPanelDirty();
    }

    public void Toggle() { if (Planning) Stop(); else Start(); }

    void Changed() { _planDirty = true; _host.MarkPanelDirty(); }

    /// <summary>A left click: a pole point on the ground there (0.5 m grid), or the removal of the one already there.</summary>
    public bool PoleAt(Vector3 ground)
    {
        var (x, y) = _host.Map.SnapAbs(ground, false);
        var at = _plan.FindIndex(p => !p.House && Math.Abs(p.X - x) <= PlanPickM && Math.Abs(p.Y - y) <= PlanPickM);
        if (at >= 0) { _plan.RemoveAt(at); Changed(); return true; }
        _plan.Add(new PlanPoint(false, x, y, null));
        Changed();
        return true;
    }

    /// <summary>A right click on a house: a connection at that spot of the wall or roof, or the removal of the one already there.</summary>
    public bool HouseAt(BuildingHit hit)
    {
        if (PolesTool.MountOf(hit.Normal) == null) { _host.Say("click a wall or the roof, not the underside of something", true); return false; }
        var at = _plan.FindIndex(p => p.House && p.Hit!.Value.Point.DistanceTo(hit.Point) <= PlanPickM);
        if (at >= 0) { _plan.RemoveAt(at); Changed(); return true; }
        var (x, y) = _host.Map.RawAbs(hit.Point);
        _plan.Add(new PlanPoint(true, Math.Round(x, 3), Math.Round(y, 3), hit));
        Changed();
        return true;
    }

    public bool UndoLast()
    {
        if (_plan.Count == 0) return false;
        _plan.RemoveAt(_plan.Count - 1);
        Changed();
        return true;
    }

    public void Clear() { _plan.Clear(); Changed(); }

    public void LeftPress(Vector2 screen)
    {
        if (_host.Picker.RayToGround(_host.FlyCam.ProjectRayOrigin(screen), _host.FlyCam.ProjectRayNormal(screen), out var g)) PoleAt(g);
        else _host.Say("click on the ground", true);
    }

    public void RightClick(Vector2 screen)
    {
        if (_poles.BuildingUnder(_host.FlyCam.ProjectRayOrigin(screen), _host.FlyCam.ProjectRayNormal(screen)) is { } hit) HouseAt(hit);
        else _host.Say("right click a house wall or roof to connect it (left click places a pole)", true);
    }

    /// <summary>The plan in map metres (x east, y north) and what the planner makes of it.</summary>
    PolePlanner.Result Solve()
    {
        _roads ??= PolePlanner.Parse(_host.Map.RoadsJson);
        var o = _host.Map.OriginEpsg5514!.Value;
        return PolePlanner.Solve(_roads,
            _plan.Where(p => !p.House).Select(p => (p.X - o.X, p.Y - o.Y)).ToList(),
            _plan.Where(p => p.House).Select(p => (p.X - o.X, p.Y - o.Y)).ToList());
    }

    /// <summary>Place a pole at every planned point, string the cables and connect the houses: one save, one undo step.</summary>
    public bool Build()
    {
        var poles = _plan.Where(p => !p.House).ToList();
        var houses = _plan.Where(p => p.House).ToList();
        if (poles.Count == 0) { _host.Say("the plan has no poles: left click where they should stand", true); return false; }
        var res = Solve();
        int built = 0, cables = 0, connected = 0, skipped = 0;
        var ok = _host.Edits.Mutate(root =>
        {
            built = cables = connected = skipped = 0;
            var ids = new string?[poles.Count];
            for (var i = 0; i < poles.Count; i++)
            {
                ids[i] = ObjectEdits.NextId(root); // a pole needs no parcel: beside a road is ground
                ObjectEdits.Add(root, ObjectEdits.Make(ids[i]!, res.Poles[i].Asset, poles[i].X, poles[i].Y, res.Poles[i].RotationDeg));
                built++;
            }
            foreach (var (a, b) in res.Links)
                if (ids[a] != null && ids[b] != null && EditsFile.Connect(root, ids[a]!, ids[b]!).ok) cables++;
            for (var h = 0; h < houses.Count; h++)
            {
                var hit = houses[h].Hit!.Value;
                var to = res.HouseTo[h];
                if (to < 0 || ids[to] == null || PolesTool.MountOf(hit.Normal) is not { } mount || _poles.PickConnector(mount, hit.Point) is not { } asset) { skipped++; continue; }
                var rot = mount == "wall" ? Math.Atan2(-hit.Normal.Z, hit.Normal.X) : Math.Atan2(poles[to].Y - houses[h].Y, poles[to].X - houses[h].X);
                var id = ObjectEdits.NextId(root);
                var o = ObjectEdits.Make(id, asset, houses[h].X, houses[h].Y, Math.Round(MapFrame.Mod360(rot * 180.0 / Math.PI), 1));
                ObjectEdits.SetZ(o, Math.Round(hit.Point.Y, 3));
                ObjectEdits.Add(root, o);
                if (EditsFile.Connect(root, ids[to]!, id).ok) connected++;
            }
            return built == 0 ? (false, "nothing to build")
                : (true, $"built {built} poles, {cables} cables, {connected} house connections" + (skipped > 0 ? $"; skipped {skipped}" : ""));
        }, $"build {poles.Count} poles");
        if (ok) Clear();
        return ok;
    }

    // ---- the plan in the 3D view ----

    public void BuildWorld()
    {
        _ok = new MeshInstance3D { Name = "PlanOk", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Gfx.Unshaded(new Color(0.1f, 0.95f, 1f), true) };
        _bad = new MeshInstance3D { Name = "PlanBad", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Gfx.Unshaded(new Color(1f, 0.25f, 0.2f), true) };
        _host.MarkerRoot.AddChild(_ok);
        _host.MarkerRoot.AddChild(_bad);
    }

    public void UpdateWorld()
    {
        var show = _host.Tool == EditorTool.Poles && _plan.Count > 0 && _host.Map.OriginEpsg5514 != null;
        _ok.Visible = _bad.Visible = show;
        if (!show || !_planDirty) return;
        _planDirty = false;
        _planResult = Solve();
        var res = _planResult;
        const float tall = 9f;
        Vector3 Foot(double east, double north) => new((float)east, _host.Map.GroundHeightOrMean((float)east, (float)-north), (float)-north);
        var tops = res.Poles.Select(p => Foot(p.X, p.Y) + Vector3.Up * tall).ToList();
        var ok = new List<Vector3>(); var bad = new List<Vector3>();
        for (var i = 0; i < res.Poles.Count; i++)
        {
            var p = res.Poles[i];
            ok.Add(Foot(p.X, p.Y)); ok.Add(tops[i]);
            // the crossarm, as the pole will stand: across the line
            var a = Mathf.DegToRad((float)p.RotationDeg);
            var across = new Vector3(-Mathf.Sin(a), 0, -Mathf.Cos(a)) * 0.9f;
            ok.Add(tops[i] - across); ok.Add(tops[i] + across);
        }
        foreach (var (a, b) in res.Links) { ok.Add(tops[a]); ok.Add(tops[b]); }
        var houses = _plan.Where(p => p.House).ToList();
        for (var h = 0; h < houses.Count; h++)
        {
            var at = houses[h].Hit!.Value.Point;
            var list = res.HouseTo[h] >= 0 ? ok : bad;
            foreach (var axis in new[] { Vector3.Right, Vector3.Up, Vector3.Back }) { list.Add(at - axis * 0.4f); list.Add(at + axis * 0.4f); }
            if (res.HouseTo[h] >= 0) { ok.Add(at); ok.Add(tops[res.HouseTo[h]]); }
        }
        _ok.Mesh = ok.Count == 0 ? null : Gfx.Strip(ok, Enumerable.Range(0, ok.Count).ToList(), Mesh.PrimitiveType.Lines);
        _bad.Mesh = bad.Count == 0 ? null : Gfx.Strip(bad, Enumerable.Range(0, bad.Count).ToList(), Mesh.PrimitiveType.Lines);
    }

    /// <summary>What Build would do, for the sidebar: poles, cables, houses connected, and what is out of reach.</summary>
    public string Summary()
    {
        if (_plan.Count == 0) return "Empty plan";
        var res = _planDirty || _planResult == null ? Solve() : _planResult;
        var far = res.HouseTo.Count(t => t < 0);
        return $"{res.Poles.Count} poles, {res.Links.Count} cables, {res.HouseTo.Length - far} houses"
            + (far > 0 ? $"\n{far} house{(far == 1 ? "" : "s")} out of reach (red)" : "");
    }
}

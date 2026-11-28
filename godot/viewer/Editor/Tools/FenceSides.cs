using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The sides of the Fence page: [Pick side] arms it, a click near the fence picks the side (the boundary between two corners,
/// a vertex turning more than <see cref="CornerDeg"/>, as the pipeline splits them: edits/schema.py SIDE_CORNER_DEG). A side
/// that has no entry yet gets one (no keys: it looks like the fence) and is selected; while a side is selected the page's
/// type, height, thickness, colour and house-colour fields edit that side. Sides with an entry are drawn as ribbons.
/// </summary>
public sealed class FenceSides
{
    public const double CornerDeg = 10.0;
    static readonly Color SideColour = new(1f, 0.55f, 0.1f), SelectedColour = new(1f, 0.2f, 0.9f), UnfencedColour = new(1f, 0.2f, 0.2f);

    readonly IEditorHost _host;
    readonly FenceTool _fence;
    Node3D _root = null!;

    public FenceSides(IEditorHost host, FenceTool fence)
    {
        _host = host;
        _fence = fence;
    }

    public bool Armed { get; private set; }
    /// <summary>The entry of the fence's sides list whose look the page edits (-1: the whole fence).</summary>
    public int Selected { get; private set; } = -1;
    /// <summary>The side entry being edited, or null.</summary>
    public SideDraft? Current => Selected >= 0 && Selected < _fence.Current.Sides.Count ? _fence.Current.Sides[Selected] : null;

    public void BuildWorld()
    {
        _root = new Node3D { Name = "EditorFenceSides" };
        _host.MarkerRoot.AddChild(_root);
    }

    public void Select(int index)
    {
        Selected = index >= 0 && index < _fence.Current.Sides.Count ? index : -1;
        _host.MarkPanelDirty();
    }

    internal void Deselect() => Selected = -1;

    public bool Remove(int index)
    {
        if (index < 0 || index >= _fence.Current.Sides.Count) return false;
        var ok = _fence.Edit(d => d.Sides.RemoveAt(index), true);
        if (ok) Selected = -1;
        return ok;
    }

    /// <summary>The side's length in metres and its 1-based number along the boundary (for the list), or null when it no longer finds one.</summary>
    public (int number, double length)? Describe(SideDraft s)
    {
        if (Geometry() is not { } g) return null;
        var i = SideAt(g, _host.Map.FromAbsolute(s.X, s.Y), 1000f);
        return i < 0 ? null : (i + 1, g.sides[i].Sum(k => g.segs[k].a.DistanceTo(g.segs[k].b)));
    }

    // ---- the gesture ----

    public bool Arm()
    {
        if (_host.Objects.PlacementAsset != null) _host.Objects.DisarmPlacement(false);
        if (_fence.Gates.Armed) _fence.Gates.Cancel();
        if (!_fence.Gates.Allowed) { _host.Say("sides: select one parcel or one property group", true); return false; }
        if (_host.Map.OriginEpsg5514 == null) { _host.Say("this map has no origin_epsg5514 (export it again, map 0.4.0+)", true); return false; }
        if (_fence.Current.EffectiveMode != "custom") _fence.SetMode("custom");
        Armed = true;
        _host.Say("click near a side of the fence (corners split the sides); Esc cancels");
        _host.MarkPanelDirty();
        return true;
    }

    public void Cancel()
    {
        var was = Armed;
        Armed = false;
        if (was) _host.Say("side pick cancelled");
        _host.MarkPanelDirty();
    }

    public void LeftPress(Vector2 screen)
    {
        if (_host.Picker.RayToGround(_host.FlyCam.ProjectRayOrigin(screen), _host.FlyCam.ProjectRayNormal(screen), out var gp)) Pick(gp);
        else _host.Say("click on the ground near the fence", true);
    }

    /// <summary>Pick the side nearest the ground point (within the gate pick radius): select its entry, adding one if it has none.</summary>
    public bool Pick(Vector3 ground)
    {
        if (!Armed || Geometry() is not { } g) return false;
        var p = new Vector2(ground.X, ground.Z);
        var side = SideAt(g, p, (float)EditSchema.GatePickRadius);
        if (side < 0) { _host.Say($"no fence side of {_fence.Target?.Header} within {EditSchema.GatePickRadius:F0} m of that point", true); return false; }
        Armed = false;
        var entries = _fence.Current.Sides;
        var have = entries.FindLastIndex(s => SideAt(g, _host.Map.FromAbsolute(s.X, s.Y), 1000f) == side);
        if (have >= 0) { Select(have); _host.Say($"side {side + 1} selected"); return true; }
        var near = FenceGates.NearestOnBoundary(g.sides[side].Select(k => g.segs[k]).ToList(), p, 1000f)!.Value.point;
        var (x, y) = _host.Map.ToAbsolute(near);
        var ok = _fence.Edit(d => d.Sides.Add(new SideDraft { X = x, Y = y }), true);
        if (ok)
        {
            Selected = _fence.Current.Sides.Count - 1;
            _host.Say($"side {side + 1} picked: set its type, or untick \"Fence on this side\" (Apply writes it)");
        }
        return ok;
    }

    // ---- geometry ----

    (List<(Vector2 a, Vector2 b)> segs, List<List<int>> sides)? Geometry()
    {
        var segs = _fence.Gates.Boundary();
        return segs == null || segs.Count == 0 ? null : (segs, Split(segs));
    }

    /// <summary>The boundary segments grouped into sides: two segments meeting at a point used by them alone, turning at most
    /// <see cref="CornerDeg"/>, are one side.</summary>
    public static List<List<int>> Split(IReadOnlyList<(Vector2 a, Vector2 b)> segs)
    {
        var parent = Enumerable.Range(0, segs.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        static (long, long) Key(Vector2 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
        var at = new Dictionary<(long, long), List<(int seg, Vector2 other)>>();
        for (var i = 0; i < segs.Count; i++)
        {
            var (a, b) = segs[i];
            (at.TryGetValue(Key(a), out var la) ? la : at[Key(a)] = new()).Add((i, b));
            (at.TryGetValue(Key(b), out var lb) ? lb : at[Key(b)] = new()).Add((i, a));
        }
        var cosMax = Math.Cos(CornerDeg * Math.PI / 180);
        foreach (var (key, list) in at)
        {
            if (list.Count != 2) continue;
            var p = new Vector2(key.Item1 / 1000f, key.Item2 / 1000f);
            var u = p - list[0].other;
            var v = list[1].other - p;
            if (u.Length() < 1e-6f || v.Length() < 1e-6f || u.Dot(v) / (u.Length() * v.Length()) < cosMax) continue;
            parent[Find(list[0].seg)] = Find(list[1].seg);
        }
        return Enumerable.Range(0, segs.Count).GroupBy(Find).Select(g => g.ToList()).OrderBy(g => g.Min()).ToList();
    }

    static int SideAt((List<(Vector2 a, Vector2 b)> segs, List<List<int>> sides) g, Vector2 p, float maxDist)
    {
        var best = -1;
        var bestD = float.MaxValue;
        for (var s = 0; s < g.sides.Count; s++)
            if (FenceGates.NearestOnBoundary(g.sides[s].Select(k => g.segs[k]).ToList(), p, maxDist) is { } n && n.dist < bestD) { best = s; bestD = n.dist; }
        return best;
    }

    // ---- markers ----

    /// <summary>Number of side ribbons drawn (sides with an entry).</summary>
    public int MarkerCount => _root.GetChildCount();

    internal void RebuildMarkers()
    {
        foreach (var c in _root.GetChildren()) { _root.RemoveChild(c); c.QueueFree(); }
        if (_fence.Target == null || !_fence.Gates.Allowed || _host.Map.OriginEpsg5514 == null || Geometry() is not { } g) return;
        var entries = _fence.Current.Sides;
        for (var i = 0; i < entries.Count; i++)
        {
            var side = SideAt(g, _host.Map.FromAbsolute(entries[i].X, entries[i].Y), 1000f);
            if (side < 0) continue;
            var colour = i == Selected ? SelectedColour : entries[i].Unfenced ? UnfencedColour : SideColour;
            var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            _root.AddChild(new MeshInstance3D
            {
                Mesh = Gfx.Ribbon(_host.Map, g.sides[side].Select(k => g.segs[k]).ToList(), 0.3f, i == Selected ? 0.5f : 0.35f),
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }
}

using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The gate tool of the Fence page: [Add gate] arms it, a click picks the fence point, a drag sets the width, the release
/// adds the gate to the pending fence of the target. Existing gates of the target are drawn as markers and listed in the panel.
/// </summary>
public sealed class FenceGates
{
    static readonly Color GateColour = new(1f, 0.55f, 0.1f), GateSelectedColour = new(1f, 0.2f, 0.9f), GatePreviewColour = new(0.1f, 0.95f, 1f);

    readonly IEditorHost _host;
    readonly FenceTool _fence;
    (Vector2 point, Vector2 tangent)? _start;
    Node3D _root = null!, _preview = null!;

    public FenceGates(IEditorHost host, FenceTool fence)
    {
        _host = host;
        _fence = fence;
    }

    public bool Armed { get; private set; }
    public bool Dragging => _start != null;
    public double Width { get; private set; } = EditSchema.GateDefault;
    /// <summary>The gate of the list that is highlighted (-1: none).</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>The target can have gates: one parcel or one property group.</summary>
    public bool Allowed => _fence.Target is { } t && (t.IsGroup || t.Parcels.Count == 1);

    /// <summary>The parents of the markers of the existing gates and of the one being dragged.</summary>
    public void BuildWorld()
    {
        _root = new Node3D { Name = "EditorGates" };
        _preview = new Node3D { Name = "EditorGatePreview" };
        _host.MarkerRoot.AddChild(_root);
        _host.MarkerRoot.AddChild(_preview);
    }

    // ---- selection and list ----

    public void Select(int index)
    {
        Selected = index >= 0 && index < _fence.Current.Gaps.Count ? index : -1;
        _host.MarkPanelDirty();
    }

    internal void Deselect() => Selected = -1;

    public bool Remove(int index)
    {
        if (index < 0 || index >= _fence.Current.Gaps.Count) return false;
        var ok = _fence.Edit(d => d.Gaps.RemoveAt(index), true);
        if (ok) Selected = -1;
        return ok;
    }

    public bool SetWidth(int index, double width)
    {
        if (index < 0 || index >= _fence.Current.Gaps.Count) return false;
        return _fence.Edit(d => d.Gaps[index] = d.Gaps[index] with { Width = Math.Max(EditSchema.GateMin, width) }, true);
    }

    // ---- the gesture ----

    /// <summary>[Add gate]: the next click picks the fence point.</summary>
    public bool Arm()
    {
        if (_host.Objects.PlacementAsset != null) _host.Objects.DisarmPlacement(false);
        if (!Allowed) { _host.Say("gates: select one parcel or one property group", true); return false; }
        if (_fence.Current.EffectiveMode != "custom") { _host.Say("gates need fence mode custom", true); return false; }
        if (_host.Map.OriginEpsg5514 == null) { _host.Say("this map has no origin_epsg5514 (export it again, map 0.4.0+)", true); return false; }
        Armed = true;
        _start = null;
        _host.Say("click the fence where the gate goes, drag for the width, release to add; Esc cancels");
        _host.MarkPanelDirty();
        return true;
    }

    public void Cancel()
    {
        var was = Armed;
        Armed = false;
        _start = null;
        ClearPreview();
        if (was) _host.Say("gate cancelled");
        _host.MarkPanelDirty();
    }

    /// <summary>A left click while armed: the point on the ground under it picks the gate's place.</summary>
    public void LeftPress(Vector2 screen)
    {
        if (_host.RayToGround(_host.FlyCam.ProjectRayOrigin(screen), _host.FlyCam.ProjectRayNormal(screen), out var gp)) Begin(gp);
        else _host.Say("click on the ground near the fence", true);
    }

    /// <summary>Press: pick the nearest fence point within 6 m of the ground point. False (tool stays armed) when none.</summary>
    public bool Begin(Vector3 ground)
    {
        if (!Armed) return false;
        var b = Boundary();
        var near = b == null ? null : NearestOnBoundary(b, new Vector2(ground.X, ground.Z), (float)EditSchema.GatePickRadius);
        if (near == null) { _host.Say($"no fence boundary of {_fence.Target?.Header} within {EditSchema.GatePickRadius:F0} m of that point", true); return false; }
        _start = (near.Value.point, near.Value.tangent);
        Width = EditSchema.GateDefault;
        UpdatePreview();
        return true;
    }

    /// <summary>Drag: the width is twice the distance from the picked point (the gate is centred there), snapped to 0.5 m, at least 1 m.</summary>
    public void Drag(Vector3 ground)
    {
        if (_start is not { } s) return;
        var d = new Vector2(ground.X, ground.Z).DistanceTo(s.point);
        Width = d < 0.25f ? EditSchema.GateDefault : Math.Max(EditSchema.GateMin, Math.Round(2.0 * d / EditSchema.GateSnap) * EditSchema.GateSnap);
        UpdatePreview();
    }

    /// <summary>Release: add the gate to the pending fence of the target.</summary>
    public bool End()
    {
        if (_start is not { } s) return false;
        var (x, y) = _host.Map.ToAbsolute(s.point);
        var width = Width;
        _start = null;
        Armed = false;
        ClearPreview();
        var ok = _fence.Edit(d => d.Gaps.Add(new Gap(x, y, width)), true);
        if (ok)
        {
            Selected = _fence.Current.Gaps.Count - 1;
            _host.Say($"gate added: {width:F1} m wide (not saved yet, Apply writes it)");
        }
        return ok;
    }

    internal void UpdateDrag()
    {
        if (_start == null) return;
        var mouse = _host.GetViewport().GetMousePosition();
        if (_host.RayToGround(_host.FlyCam.ProjectRayOrigin(mouse), _host.FlyCam.ProjectRayNormal(mouse), out var g)) Drag(g);
    }

    // ---- the fence boundary ----

    /// <summary>The fence boundary of the target in ground axes: a parcel's own edges, a group's outer perimeter; null for several loose parcels.</summary>
    List<(Vector2 a, Vector2 b)>? Boundary()
    {
        if (_fence.Target is not { } t) return null;
        if (t.IsGroup)
            return ParcelOutline.GroupPerimeter(t.Parcels.Where(_host.Map.Parcels.ContainsKey).Select(id => _host.Map.Parcels[id]).ToList());
        if (t.Parcels.Count == 1 && _host.Map.Parcels.TryGetValue(t.Parcels[0], out var p)) return ParcelOutline.Edges(p).ToList();
        return null;
    }

    /// <summary>The point of the segments nearest to p (within maxDist) and the segment's direction.</summary>
    public static (Vector2 point, Vector2 tangent, float dist)? NearestOnBoundary(IReadOnlyList<(Vector2 a, Vector2 b)> segs, Vector2 p, float maxDist)
    {
        (Vector2, Vector2, float)? best = null;
        foreach (var (a, b) in segs)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();
            var t = len2 < 1e-12f ? 0f : Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
            var q = a + ab * t;
            var d = q.DistanceTo(p);
            if (d <= maxDist && (best == null || d < best.Value.Item3)) best = (q, ab.Normalized(), d);
        }
        return best;
    }

    // ---- markers ----

    void ClearPreview()
    {
        foreach (var c in _preview.GetChildren()) { _preview.RemoveChild(c); c.QueueFree(); }
    }

    void UpdatePreview()
    {
        ClearPreview();
        if (_start is { } s) AddMarker(_preview, s.point, s.tangent, (float)Width, GatePreviewColour);
    }

    void AddMarker(Node3D parent, Vector2 point, Vector2 tangent, float width, Color colour)
    {
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var half = tangent * width / 2f;
        var ribbon = new MeshInstance3D { MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ribbon.Mesh = Gfx.Ribbon(_host.Map, new List<(Vector2, Vector2)> { (point - half, point + half) }, 0.45f, 0.6f);
        parent.AddChild(ribbon);
        foreach (var end in new[] { point - half, point + half })
        {
            var y = _host.Map.GroundHeightOrMean(end.X, end.Y);
            var post = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.1f, Height = 2.4f, RadialSegments = 8 },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(end.X, y + 1.2f, end.Y),
            };
            parent.AddChild(post);
        }
    }

    /// <summary>Number of gate markers currently drawn (existing gates of the target; the smoke checks it).</summary>
    public int MarkerCount => _root.GetChildCount() / 3;

    internal void RebuildMarkers()
    {
        foreach (var c in _root.GetChildren()) { _root.RemoveChild(c); c.QueueFree(); }
        if (_fence.Target == null || !Allowed || _host.Map.OriginEpsg5514 == null) return;
        var boundary = Boundary();
        if (boundary == null) return;
        var gaps = _fence.Current.Gaps;
        for (var i = 0; i < gaps.Count; i++)
        {
            var at = _host.Map.FromAbsolute(gaps[i].X, gaps[i].Y);
            var near = NearestOnBoundary(boundary, at, 1000f);
            if (near == null) continue;
            AddMarker(_root, near.Value.point, near.Value.tangent, (float)gaps[i].Width, i == Selected ? GateSelectedColour : GateColour);
        }
    }
}

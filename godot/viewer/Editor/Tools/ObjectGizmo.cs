using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>The 3D handles of the selected object: a box around it, a rotation ring and an arrow at its feet (a press on the ring starts a rotation).</summary>
public sealed class ObjectGizmo
{
    const float RingBand = 0.45f;

    readonly IEditorHost _host;
    readonly ObjectsTool _tool;
    Node3D _root = null!, _box = null!, _ring = null!;
    MeshInstance3D _boxMesh = null!, _ringMesh = null!, _arrowMesh = null!, _knob = null!;
    string _key = "";

    public ObjectGizmo(IEditorHost host, ObjectsTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public float Radius { get; private set; }
    public bool Visible => _root.Visible;

    public void Build()
    {
        _root = new Node3D { Name = "ObjectGizmo", Visible = false };
        _box = new Node3D { Name = "GizmoBox" };
        _boxMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Gfx.Unshaded(new Color(1f, 0.92f, 0.1f), false) };
        _box.AddChild(_boxMesh);
        _ring = new Node3D { Name = "GizmoRing" };
        var ringColour = new Color(0.1f, 0.95f, 1f);
        _ringMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Gfx.Unshaded(ringColour, true) };
        _arrowMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Gfx.Unshaded(ringColour, true) };
        _knob = new MeshInstance3D
        {
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = Gfx.Unshaded(new Color(1f, 0.2f, 0.9f), true),
        };
        _ring.AddChild(_ringMesh); _ring.AddChild(_arrowMesh); _ring.AddChild(_knob);
        _root.AddChild(_box);
        _root.AddChild(_ring);
        _host.MarkerRoot.AddChild(_root);
    }

    void RebuildShapes(Aabb box, float radius)
    {
        // wireframe box (12 edges) in the object's own frame
        var v = new List<Vector3>();
        for (var i = 0; i < 8; i++) v.Add(box.GetEndpoint(i));
        var edges = new[] { 0, 1, 1, 5, 5, 4, 4, 0, 2, 3, 3, 7, 7, 6, 6, 2, 0, 2, 1, 3, 4, 6, 5, 7 };
        _boxMesh.Mesh = Gfx.Strip(v, edges.ToList(), Mesh.PrimitiveType.Lines);
        // ring (a flat ribbon of 0.14 m) and the arrow from the centre to the knob, along local +X
        var rv = new List<Vector3>(); var ri = new List<int>();
        const int n = 72;
        for (var i = 0; i <= n; i++)
        {
            var a = i * Mathf.Tau / n;
            var d = new Vector3(Mathf.Cos(a), 0, -Mathf.Sin(a));
            rv.Add(d * (radius - 0.07f)); rv.Add(d * (radius + 0.07f));
            if (i > 0) { var k = rv.Count - 4; ri.AddRange(new[] { k, k + 1, k + 3, k, k + 3, k + 2 }); }
        }
        _ringMesh.Mesh = Gfx.Strip(rv, ri);
        var av = new List<Vector3> { new(0, 0, -0.06f), new(0, 0, 0.06f), new(radius, 0, 0.06f), new(radius, 0, -0.06f) };
        _arrowMesh.Mesh = Gfx.Strip(av, new List<int> { 0, 1, 2, 0, 2, 3 });
        _knob.Position = new Vector3(radius, 0, 0);
    }

    /// <summary>Follow the selected object: box, ring at its feet (radius from its footprint) and the arrow showing rotation_deg.</summary>
    public void Update()
    {
        var id = _tool.SelectedObjectId;
        var node = id != null && _host.Map.ObjectNodes.TryGetValue(id, out var n) && GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n : null;
        var box = node == null ? null : _tool.LocalAabb(node);
        _root.Visible = _host.Active && node != null && box != null;
        if (node == null || box == null) return;
        var b = box.Value;
        var (sx, sz) = (b.Size.X * node.Scale.X, b.Size.Z * node.Scale.Z); // size_m scales the axes apart
        var radius = Mathf.Max(0.9f, 0.5f * Mathf.Sqrt(sx * sx + sz * sz) + 0.6f);
        var key = $"{node.GetInstanceId()}|{radius:F2}";
        if (key != _key) { _key = key; Radius = radius; RebuildShapes(b, radius); }
        _box.GlobalTransform = node.GlobalTransform;
        _ring.GlobalPosition = node.GlobalPosition + Vector3.Up * 0.1f;
        _ring.GlobalRotation = new Vector3(0, node.GlobalRotation.Y, 0);
    }

    /// <summary>True when a ground point lies on the rotation ring of the selected object (a press there starts a rotation).</summary>
    public bool OnRing(Vector3 ground)
    {
        var id = _tool.SelectedObjectId;
        if (id == null || !_host.Map.ObjectNodes.TryGetValue(id, out var n) || !Visible) return false;
        var d = new Vector2(ground.X - n.GlobalPosition.X, ground.Z - n.GlobalPosition.Z).Length();
        return Mathf.Abs(d - Radius) <= RingBand;
    }
}

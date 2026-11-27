using Drycastle.ShooterCore.Editor;
using Godot;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The selected opening in the 3D view: a translucent plate on its wall with a frame round it, a little off the wall so it reads from outside. Map metres (x east, y north, z up)
/// become Godot's x, z, -y. Redrawn only when the opening, its size or its place changes.
/// </summary>
public sealed class PhotoMarker
{
    const double Lift = 0.1, Bar = 0.07;
    readonly IEditorHost _host;
    MeshInstance3D? _node;
    StandardMaterial3D _fill = null!, _frame = null!;
    string _sig = "";

    public PhotoMarker(IEditorHost host) => _host = host;

    public void Build()
    {
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = c.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled, RenderPriority = 10,
        };
        _fill = Mat(new Color(EditorTheme.Accent, 0.32f));
        _frame = Mat(EditorTheme.Accent);
        _node = new MeshInstance3D { Name = "PhotoOpening", Mesh = new ImmediateMesh(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _host.MarkerRoot.AddChild(_node);
    }

    public void Show(BuildingForm? form, string? openingId, bool active)
    {
        if (_node == null) return;
        var row = form?.Detail?.Openings.FirstOrDefault(o => o.Id == openingId);
        var edge = row == null ? default : form!.Edges.FirstOrDefault(e => e.Index == row.Edge);
        if (!active || row == null || edge.Length <= 0) { _node.Visible = false; _sig = ""; return; }
        var sig = $"{form!.Building}/{row.Id}/{row.Edge}/{row.At}/{row.Width}/{row.Height}/{row.Sill}/{form.BaseZ}";
        _node.Visible = true;
        if (sig == _sig) return;
        _sig = sig;
        var m = (ImmediateMesh)_node.Mesh;
        m.ClearSurfaces();
        Vector3 G((double X, double Y, double Z) p) => new((float)p.X, (float)p.Z, (float)-p.Y);
        var c = BuildingGeom.Corners(edge, row.Spec, form.BaseZ, Lift);
        m.SurfaceBegin(Mesh.PrimitiveType.Triangles, _fill);
        foreach (var i in new[] { 0, 1, 2, 0, 2, 3 }) m.SurfaceAddVertex(G(c[i]));
        m.SurfaceEnd();
        // the frame: the opening's own outline grown by Bar, as four strips
        var o = BuildingGeom.Corners(edge, row.Spec with { At = row.At - Bar, Width = row.Width + 2 * Bar, Sill = row.Sill - Bar, Height = row.Height + 2 * Bar }, form.BaseZ, Lift * 1.1);
        m.SurfaceBegin(Mesh.PrimitiveType.Triangles, _frame);
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            foreach (var v in new[] { (o[i], o[j], c[j]), (o[i], c[j], c[i]) }) { m.SurfaceAddVertex(G(v.Item1)); m.SurfaceAddVertex(G(v.Item2)); m.SurfaceAddVertex(G(v.Item3)); }
        }
        m.SurfaceEnd();
    }
}

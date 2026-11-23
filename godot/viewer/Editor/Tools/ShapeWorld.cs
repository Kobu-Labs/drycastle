using Drycastle.Viewer.Edits;
using Godot;
using System.Collections.Generic;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// What the shape tools draw in the 3D view: the outline of the shape being drawn (with a ghost of the next edge, red where it would
/// cross), of the selected shape and of a corner being dragged, and a handle at every corner. Redrawn only when what it shows changes.
/// </summary>
public sealed class ShapeWorld
{
    public const string OutlineDraft = "outline_shape_draft", OutlineGhost = "outline_shape_ghost",
        OutlineBad = "outline_shape_bad", OutlineSel = "outline_shape_sel", OutlineDrag = "outline_shape_drag";
    static readonly string[] Outlines = { OutlineDraft, OutlineGhost, OutlineBad, OutlineSel, OutlineDrag };

    readonly IEditorHost _host;
    readonly ShapeEditor _shapes;
    string _signature = "";
    Node3D _handleRoot = null!;
    readonly List<MeshInstance3D> _handles = new();
    StandardMaterial3D _handleMat = null!;

    public ShapeWorld(IEditorHost host, ShapeEditor shapes)
    {
        _host = host;
        _shapes = shapes;
    }

    /// <summary>One sphere for every corner handle (the handles differ only by transform and material), and the material of the hot one.</summary>
    public SphereMesh HandleMesh { get; } = new() { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };
    public StandardMaterial3D HandleHot { get; private set; } = null!;

    public int HandleCount => _handles.Count(h => h.Visible);

    public void Build()
    {
        _handleRoot = new Node3D { Name = "ShapeHandles" };
        _host.MarkerRoot.AddChild(_handleRoot);
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _handleMat = Mat(EditorTheme.Accent);
        HandleHot = Mat(Colors.White);
    }

    /// <summary>What is shown has changed in a way the signature cannot see (a gesture started or ended).</summary>
    public void Invalidate() => _signature = "";

    /// <summary>Redraw now (smoke tests; normally per frame).</summary>
    public void Redraw()
    {
        Invalidate();
        Draw();
    }

    /// <summary>The tool is not a shape tool: nothing is shown.</summary>
    public void Off()
    {
        if (_signature == "off") return;
        _signature = "off";
        foreach (var n in Outlines) _host.SetOutline(n, new(), Colors.White, 0, 0);
        foreach (var h in _handles) h.Visible = false;
    }

    /// <summary>The outline of a selected shape: its crop's colour for a patch of a crop_* type, else the accent.</summary>
    static Color OutlineColour(ShapeInfo s) => s is PatchInfo p && EditorTheme.CropColour(p.Type) is { } c ? c : EditorTheme.Accent;

    static string Tag(ShapeInfo s) => s is PatchInfo p ? p.Type + p.WaterY : s is ZoneInfo z ? z.Kind : "";

    void ClearOutlines()
    {
        foreach (var n in Outlines) _host.SetOutline(n, new(), Colors.White, 0, 0);
    }

    public void Draw()
    {
        var s = _shapes;
        var sel = s.SelectedShape(s.CurrentKind);
        var dragRing = s.DragRing();
        var sig = string.Join(";", s.ShapeDraft) + "|" + s.InLevel + "|" + (s.Cursor?.ToString() ?? "-") + "|" + (sel == null ? "-" : sel.Id + Tag(sel) + string.Join(",", sel.Points))
            + "|" + (dragRing == null ? "-" : s.DragPoint.ToString()) + "|" + s.HoverCorner + "|" + s.ActiveCorner;
        var changed = sig != _signature;
        var show = new List<(double x, double y)>();
        var hot = -1;
        if (s.Mode is ShapeState.Drawing or ShapeState.Level)
        {
            show = s.ShapeDraft.ToList();
            if (changed) DrawDraft();
            hot = s.HoverCorner;
        }
        else if (sel != null)
        {
            show = dragRing ?? sel.Points;
            if (changed) DrawSelected(sel, dragRing);
            hot = s.DragCorner >= 0 ? s.DragCorner : s.HoverCorner >= 0 ? s.HoverCorner : s.ActiveCorner;
        }
        else if (changed)
            ClearOutlines();
        _signature = sig;
        DrawHandles(show, hot);
    }

    /// <summary>The corners so far (closed once the shape asks for its level), the ghost of the edge the next click adds, and in red what would cross.</summary>
    void DrawDraft()
    {
        var s = _shapes;
        var draft = s.ShapeDraft;
        var map = _host.Map;
        var chain = _host.RingSegs(draft, s.InLevel);
        var ghost = new List<(Vector2, Vector2)>(); var bad = new List<(Vector2, Vector2)>();
        if (!s.InLevel && s.Cursor is { } cur && draft.Count > 0)
        {
            var ok = ObjectEdits.ChainAccepts(draft, cur);
            var a = map.FromAbsolute(draft[^1].x, draft[^1].y); var b = map.FromAbsolute(cur.x, cur.y);
            if (a.DistanceSquaredTo(b) > 1e-8f) (ok ? ghost : bad).Add((a, b));
            if (draft.Count >= 2 && ok) { var f = map.FromAbsolute(draft[0].x, draft[0].y); if (b.DistanceSquaredTo(f) > 1e-8f) ghost.Add((b, f)); }
        }
        var closedBad = s.InLevel && ShapeEditor.RingBad(draft);
        _host.SetOutline(OutlineDraft, closedBad ? new() : chain, EditorTheme.Accent, 0.31f, 0.45f);
        _host.SetOutline(OutlineGhost, ghost, new Color(EditorTheme.Accent, 0.5f), 0.30f, 0.35f);
        _host.SetOutline(OutlineBad, bad.Concat(closedBad ? chain : new()).ToList(), EditorTheme.Danger, 0.33f, 0.45f);
        _host.SetOutline(OutlineSel, new(), Colors.White, 0, 0);
        _host.SetOutline(OutlineDrag, new(), Colors.White, 0, 0);
    }

    /// <summary>The selected shape's outline, or while a corner is dragged the outline it would have (red when it would be refused).</summary>
    void DrawSelected(ShapeInfo sel, List<(double x, double y)>? dragRing)
    {
        var bad = dragRing != null && ShapeEditor.RingBad(dragRing);
        _host.SetOutline(OutlineSel, dragRing == null ? _host.RingSegs(sel.Points, true) : new(), OutlineColour(sel), 0.31f, 0.45f);
        _host.SetOutline(OutlineDrag, dragRing != null && !bad ? _host.RingSegs(dragRing, true) : new(), OutlineColour(sel), 0.32f, 0.45f);
        _host.SetOutline(OutlineBad, bad ? _host.RingSegs(dragRing!, true) : new(), EditorTheme.Danger, 0.33f, 0.45f);
        _host.SetOutline(OutlineDraft, new(), Colors.White, 0, 0);
        _host.SetOutline(OutlineGhost, new(), Colors.White, 0, 0);
    }

    /// <summary>Corner handles: sized by distance so they stay grabbable from any height.</summary>
    void DrawHandles(List<(double x, double y)> show, int hot)
    {
        while (_handles.Count < show.Count)
        {
            var mi = new MeshInstance3D { Mesh = HandleMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _handleRoot.AddChild(mi);
            _handles.Add(mi);
        }
        for (var i = 0; i < _handles.Count; i++)
        {
            var h = _handles[i];
            h.Visible = i < show.Count;
            if (i >= show.Count) continue;
            var p = _host.GroundPoint(show[i]);
            h.Position = p;
            var s = Mathf.Clamp(p.DistanceTo(_host.FlyCam.GlobalPosition) * 0.011f, 0.35f, 8f) * (i == hot ? 1.5f : 1f);
            h.Scale = new Vector3(s, s, s);
            h.MaterialOverride = i == hot ? HandleHot : _handleMat;
        }
    }
}

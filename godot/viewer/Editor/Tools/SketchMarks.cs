using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>What <see cref="SketchMarks"/> draws: the sketch, and the tool's selection, hover, draft and cursor.</summary>
public readonly record struct SketchView(
    SketchDoc Doc, int Version, string? Selected, string? Hover, IReadOnlyList<(double x, double y)> Draft, (double x, double y)? Cursor, SketchKind Mode);

/// <summary>
/// The sketch's marks in the 3D view: a handle on every point and corner (sized by distance, so they stay clickable from any height), the lines draped on the
/// terrain, the line being drawn with the ghost of the next click, and a name tag next to every mark. Visible in every tool; only the Sketch tool edits them.
/// </summary>
public sealed class SketchMarks
{
    const string OutlineSketch = "outline_sketch", OutlineSketchSel = "outline_sketch_sel",
        OutlineSketchDraft = "outline_sketch_draft", OutlineSketchGhost = "outline_sketch_ghost";
    const int HandlePlain = 0, HandleHot = 1, HandleGhost = 2;

    readonly IEditorHost _host;
    Node3D _root = null!;
    Control _tagLayer = null!;
    StandardMaterial3D _mat = null!, _ghostMat = null!;
    readonly List<MeshInstance3D> _handles = new();
    readonly List<Label> _tags = new();
    // what the handles and tags show; rebuilt when the sketch, the hover or the cursor changes, placed every frame
    readonly List<(Vector3 pos, float size, int look)> _fixed = new(), _live = new();
    readonly List<Vector3> _tagAt = new();
    (int version, string? hover) _fixedSig = (-1, null);
    (int version, (double x, double y)? cursor, int hoverDraft, bool active, string? hover) _liveSig = (-1, null, -1, false, null);

    public SketchMarks(IEditorHost host) => _host = host;

    public void BuildTags(Control root)
    {
        // added first, so the rail, the sidebar and the status bar cover the tags
        _tagLayer = new Control { Name = "SketchTags", MouseFilter = Control.MouseFilterEnum.Ignore };
        _tagLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_tagLayer);
    }

    public void BuildWorld()
    {
        _root = new Node3D { Name = "SketchHandles" };
        _host.MarkerRoot.AddChild(_root);
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = c.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        };
        _mat = Mat(EditorTheme.Sketch);
        _ghostMat = Mat(new Color(EditorTheme.Sketch, 0.5f));
    }

    /// <summary>A mark's tag in the 3D view: its name, with the sketch in front when there are several ("ROAD · L1").</summary>
    static string MarkTag(SketchDoc doc, string sketch, string id) => doc.Names.Count > 1 ? $"{sketch} · {id}" : id;

    static (double x, double y) TagAnchor(SketchLine l)
    {
        if (!l.Closed) return ((l.Points[0].x + l.Points[1].x) / 2, (l.Points[0].y + l.Points[1].y) / 2);
        return (l.Points.Average(p => p.x), l.Points.Average(p => p.y));
    }

    /// <summary>Redraw what changed, then place the handles and tags for this frame.</summary>
    public void Show(SketchView v, bool active, int hoverDraft)
    {
        if (_host.Map.OriginEpsg5514 == null) return;
        RebuildFixed(v);
        RebuildLive(v, active, hoverDraft);
        PlaceHandles();
        PlaceTags();
    }

    /// <summary>Show again from scratch (smoke tests).</summary>
    public void Invalidate() { _fixedSig = (-1, null); _liveSig = (-1, null, -1, false, null); }

    // the finished marks: handles, lines, tags
    void RebuildFixed(SketchView v)
    {
        var fixedSig = (v.Version, v.Hover);
        if (fixedSig.Equals(_fixedSig)) return;
        _fixedSig = fixedSig;
        _fixed.Clear(); _tagAt.Clear();
        var plain = new List<(Vector2, Vector2)>(); var sel = new List<(Vector2, Vector2)>();
        var tag = 0;
        void Tag((double x, double y) at, string text)
        {
            if (tag == _tags.Count)
            {
                var l = new Label { ThemeTypeVariation = "SketchTag", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
                _tagLayer.AddChild(l);
                _tags.Add(l);
            }
            _tags[tag++].Text = text;
            _tagAt.Add(_host.Picker.GroundPoint(at));
        }
        foreach (var p in v.Doc.Points)
        {
            _fixed.Add((_host.Picker.GroundPoint((p.X, p.Y)), 1.4f, p.Id == v.Selected || p.Id == v.Hover ? HandleHot : HandlePlain));
            Tag((p.X, p.Y), MarkTag(v.Doc, p.Sketch, p.Id));
        }
        foreach (var l in v.Doc.Lines)
        {
            (l.Id == v.Selected ? sel : plain).AddRange(_host.Picker.RingSegs(l.Points, l.Closed));
            var look = l.Id == v.Selected || l.Id == v.Hover ? HandleHot : HandlePlain;
            foreach (var c in l.Points) _fixed.Add((_host.Picker.GroundPoint(c), 0.7f, look));
            Tag(TagAnchor(l), MarkTag(v.Doc, l.Sketch, l.Id));
        }
        for (var i = tag; i < _tags.Count; i++) _tags[i].Visible = false;
        _host.Outlines.Set(OutlineSketch, plain, EditorTheme.Sketch, 0.36f, 0.4f);
        _host.Outlines.Set(OutlineSketchSel, sel, EditorTheme.Text, 0.37f, 0.55f);
    }

    // the line being drawn and the ghost of the next click
    void RebuildLive(SketchView v, bool active, int hoverDraft)
    {
        var liveSig = (v.Version, v.Cursor, hoverDraft, active, v.Hover);
        if (liveSig.Equals(_liveSig)) return;
        _liveSig = liveSig;
        _live.Clear();
        var ghost = new List<(Vector2, Vector2)>();
        for (var i = 0; i < v.Draft.Count; i++) _live.Add((_host.Picker.GroundPoint(v.Draft[i]), 1f, i == hoverDraft ? HandleHot : HandlePlain));
        if (active && v.Cursor is { } cur)
        {
            // the mark the next click makes: a ghost point, and the edge to it (to the first corner when the click would close the line)
            if (v.Draft.Count > 0)
            {
                var to = hoverDraft == 0 ? v.Draft[0] : cur;
                var a = _host.Map.FromAbsolute(v.Draft[^1].x, v.Draft[^1].y); var b = _host.Map.FromAbsolute(to.x, to.y);
                if (a.DistanceSquaredTo(b) > 1e-8f) ghost.Add((a, b));
            }
            if (hoverDraft < 0 && v.Hover == null) _live.Add((_host.Picker.GroundPoint(cur), v.Mode == SketchKind.Point ? 1.4f : 1f, HandleGhost));
        }
        _host.Outlines.Set(OutlineSketchDraft, _host.Picker.RingSegs(v.Draft, false), EditorTheme.Sketch, 0.38f, 0.4f);
        _host.Outlines.Set(OutlineSketchGhost, ghost, new Color(EditorTheme.Sketch, 0.5f), 0.35f, 0.35f);
    }

    // handles: sized by distance so they stay clickable from any height
    void PlaceHandles()
    {
        var n = _fixed.Count + _live.Count;
        while (_handles.Count < n)
        {
            var mi = new MeshInstance3D { Mesh = _host.Shapes.World.HandleMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _root.AddChild(mi);
            _handles.Add(mi);
        }
        var cam = _host.FlyCam.GlobalPosition;
        for (var i = 0; i < _handles.Count; i++)
        {
            var h = _handles[i];
            h.Visible = i < n;
            if (i >= n) continue;
            var (pos, size, look) = i < _fixed.Count ? _fixed[i] : _live[i - _fixed.Count];
            h.Position = pos;
            var s = Mathf.Clamp(pos.DistanceTo(cam) * 0.011f, 0.35f, 8f) * size * (look == HandleHot ? 1.3f : 1f);
            h.Scale = new Vector3(s, s, s);
            h.MaterialOverride = look == HandleHot ? _host.Shapes.World.HandleHot : look == HandleGhost ? _ghostMat : _mat;
        }
    }

    // name tags follow their marks on the screen
    void PlaceTags()
    {
        var offset = new Vector2(EditorTheme.Space, -EditorTheme.Space * 4);
        for (var i = 0; i < _tagAt.Count; i++)
        {
            var t = _tags[i];
            var behind = _host.FlyCam.IsPositionBehind(_tagAt[i]);
            t.Visible = !behind;
            if (!behind) t.Position = _host.FlyCam.UnprojectPosition(_tagAt[i]) + offset;
        }
    }
}

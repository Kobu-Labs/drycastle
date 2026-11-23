using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The overlay of the Zones tool (M toggles it, remembered in editor_ui.cfg): every zone drawn on the terrain translucent in its kind's
/// tint with a thin edge, in the area meshes of <see cref="AreaSurfaces"/> (the ones the ground map draws too). A selected kind is shown
/// alone (also with the overlay off): the other kinds are not drawn and not pointed at, and its zones get moving bands of its tint and
/// white, so they read on ground of the same colour (the bands stand still with reduce motion), and are drawn a second time, faint and
/// without a depth test, so they still show where buildings, trees and hills cover them. Zones have no paint and no hot reload; the
/// build turns them into candidate points. The mesh is built one kind per frame; nothing is saved here.
/// </summary>
public sealed class ZoneOverlay
{
    // The fill is striped: a full band of the kind's colour, then a faint one (StripeTexture), so a zone reads at a glance on any ground.
    const float Fill = 0.55f, FillSelected = 0.85f;
    const float FillGhost = 0.3f, EdgeGhost = 0.5f; // the selected kind where an obstacle covers it
    const float MarchTilesPerS = 0.125f; // the bands of the selected kind move 3 m/s
    const float MarchWhite = 0.7f;       // alpha of the white band between two bands of the tint

    readonly IEditorHost _host;
    readonly ZonesTool _tool;

    // the mesh of all kinds, one kind built per frame
    MeshInstance3D? _mesh;
    bool _indexed;
    object? _disk, _kindsFor;
    string _sig = "", _matSig = "";
    int _step;
    readonly TriangleBudget _budget = new();
    ArrayMesh? _arrayMesh;
    List<string> _order = new();
    readonly Dictionary<string, AreaSet> _kinds = new();   // key: the kind

    // The selected kind has a mesh of its own, with its own triangle budget: the mesh of all kinds shares one budget in catalog
    // order, so on a big map the later kinds (the wildlife ones cover the same forest and fields many times) are cut short there.
    MeshInstance3D? _selMesh;
    string _selSig = "";
    StandardMaterial3D? _selFill, _ghostFill, _ghostEdge;
    readonly TriangleBudget _selBudget = new() { Max = 700000 }; // one kind over every field and forest of the village map is about 250 000

    CheckBox _check = null!;
    Label _note = null!;

    public ZoneOverlay(IEditorHost host, ZonesTool tool)
    {
        _host = host;
        _tool = tool;
    }

    /// <summary>The overlay is on (remembered in editor_ui.cfg).</summary>
    public bool On { get; set; } = true;

    public void Toggle() => Set(!On);

    public void Set(bool on, bool save = true)
    {
        On = on;
        if (_check != null) _check.SetPressedNoSignal(on);
        if (_mesh != null) _mesh.Visible = on && _tool.SelectedKind == null && _host.Tool == EditorTool.Zones;
        _host.MarkPanelDirty();
        _host.Say(on ? "zone overlay on: every zone in its kind's tint (M hides it)"
            : _tool.SelectedKind != null ? $"zone overlay hidden; {_tool.KindLabel(_tool.SelectedKind)} stays shown while it is selected (M shows all again)" : "zone overlay hidden (M shows it)");
        if (save) _host.SaveUiConfig();
    }

    public void BuildToggle(VBoxContainer page)
    {
        _check = new CheckBox
        {
            Text = "Show zones on the map  (M)", FocusMode = Control.FocusModeEnum.All, ButtonPressed = On,
            TooltipText = "Draw every zone on the terrain in its kind's tint (M). A selected kind is shown alone, also when this is off. Hover names the zones, click selects one.",
        };
        _check.Toggled += on => Set(on);
        page.AddChild(_check);
        _note = Ui.Note("");
        page.AddChild(_note);
    }

    /// <summary>The panel is redrawn: the toggle shows the state of the overlay.</summary>
    public void SyncToggle() => _check.SetPressedNoSignal(On);

    Color Tint(string kind) => _tool.KindOf(kind) is { } k && EditSchema.ParseColour(k.Tint) is { } c ? Color.FromHtml(c) : new Color(0.5f, 0.5f, 0.5f);

    // ---- the index: the zones per kind ----

    /// <summary>Index the zones per kind for the overlay. Geometry changes only with a zone, a cut or a new map; tints and selection only touch the materials.</summary>
    void EnsureIndex()
    {
        var map = _host.Map;
        var disk = _host.Edits.Disk();
        var kinds = map.ZoneKinds;
        if (_indexed && ReferenceEquals(disk, _disk) && ReferenceEquals(kinds, _kindsFor)) return;
        _indexed = true; _disk = disk; _kindsFor = kinds;
        var zones = _tool.List();
        var sig = map.OriginEpsg5514 == null ? "" : _tool.Signature;
        if (sig == _sig && _arrayMesh != null) return;
        _sig = sig;

        var prev = _kinds.ToDictionary(p => p.Key, p => (p.Value.Fill, p.Value.Edge));
        _kinds.Clear();
        if (map.OriginEpsg5514 != null)
            foreach (var z in zones)
            {
                var rings = new List<Vector2[]> { z.Points.Select(c => map.FromAbsolute(c.x, c.y)).ToArray() };
                if (z.Holes != null) rings.AddRange(z.Holes.Select(h => h.Select(c => map.FromAbsolute(c.x, c.y)).ToArray()));
                if (!_kinds.TryGetValue(z.Kind, out var t)) _kinds[z.Kind] = t = new AreaSet { Name = z.Kind };
                t.Areas++;
                AreaSurfaces.Split(rings, t, out var area);
                t.AreaM2 += area;
            }
        _step = 0; _budget.Count = 0; _budget.Truncated = false; _arrayMesh = null; if (_mesh != null) _mesh.Mesh = null;
        foreach (var (name, t) in _kinds) if (prev.TryGetValue(name, out var m)) { t.Fill = m.Fill; t.Edge = m.Edge; }
        var order = kinds.Select(k => k.Name).ToList();
        _order = _kinds.Keys.OrderBy(k => order.IndexOf(k)).ToList();
    }

    // ---- per frame ----

    /// <summary>Per frame while the tool is open: index, build one kind of the mesh, and keep tints and selection in the materials.</summary>
    public void Update()
    {
        var show = _host.Tool == EditorTool.Zones && _tool.Drawn && _host.Map.OriginEpsg5514 != null;
        var sel = _tool.SelectedKind;
        if (_mesh != null) _mesh.Visible = show && sel == null;
        if (_selMesh != null) _selMesh.Visible = show && sel != null;
        _tool.Marks.UpdateOutline();
        if (_host.Tool != EditorTool.Zones) return;
        EnsureIndex();
        if (_note != null) _note.Text = Note();
        if (!show || _order.Count == 0) return;
        if (sel != null) { UpdateSelectedKind(sel); return; } // shown alone, in its own mesh; the mesh of all kinds waits
        if (_mesh == null)
        {
            _mesh = new MeshInstance3D { Name = "ZoneOverlay", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _host.Outlines.Root.AddChild(_mesh); // follows the editor's visibility: gone in walk mode
        }
        if (_step < _order.Count)
        {
            _arrayMesh ??= new ArrayMesh();
            if (_step == 0 && _arrayMesh.GetSurfaceCount() > 0) _arrayMesh = new ArrayMesh();
            var kind = _order[_step];
            AreaSurfaces.Build(_host.Map, _arrayMesh, _kinds[kind], Tint(kind), Fill, 6, _budget, StripeTexture());
            _matSig = ""; // new materials: colour them again
            _step++;
            _mesh.Mesh = _arrayMesh;
        }
        ApplyMaterials();
    }

    /// <summary>The line under the toggle: what the overlay shows or why it shows nothing.</summary>
    string Note()
    {
        if (_host.Map.ZoneKinds.Count == 0) return "No zone kinds in this map's parcels.json. Build it again (make map), then reopen the editor.";
        if (!On && _tool.SelectedKind == null) return "The zone overlay is off. Pick a kind to show its zones alone.";
        if (_tool.SelectedKind is { } k)
            return $"Showing only {_tool.KindLabel(k)} ({_tool.Count(k)}); Esc shows all kinds{(On ? "" : " (the overlay is off: M)")}." + (_selBudget.Truncated ? " Too many to draw: the smallest triangles are left out." : "");
        if (_step < _order.Count) return $"Drawing zones {_step + 1}/{_order.Count}...";
        var n = _tool.List().Count;
        return $"{n} zone{(n == 1 ? "" : "s")} on the map" + (_budget.Truncated ? ". Too many to draw: the smallest triangles are left out." : ".");
    }

    // ---- the textures ----

    static ImageTexture? _stripes;

    /// <summary>The stripe tile: diagonal bands, alternating full and faint (alpha 1 and 0.15), seamless. Made once.</summary>
    static ImageTexture StripeTexture()
    {
        if (_stripes != null) return _stripes;
        const int n = 64, period = 32; // 2 periods per tile; the bands run at 45 degrees
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                img.SetPixel(x, y, new Color(1, 1, 1, (x + y) % period < period / 2 ? 1f : 0.15f));
        img.GenerateMipmaps();
        return _stripes = ImageTexture.CreateFromImage(img);
    }

    static readonly Dictionary<Color, ImageTexture> _marchTextures = new();

    /// <summary>The tile of a selected kind: the same bands as <see cref="StripeTexture"/>, the tint alternating with white, so the zones read on ground of their own colour. One per tint.</summary>
    static ImageTexture MarchTexture(Color tint)
    {
        if (_marchTextures.TryGetValue(tint, out var tex)) return tex;
        const int n = 64, period = 32;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        var full = new Color(tint, 1f); var white = new Color(1, 1, 1, MarchWhite);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                img.SetPixel(x, y, (x + y) % period < period / 2 ? full : white);
        img.GenerateMipmaps();
        return _marchTextures[tint] = ImageTexture.CreateFromImage(img);
    }

    // ---- the materials ----

    /// <summary>The mesh of all kinds (no kind selected): every kind in its tint.</summary>
    void ApplyMaterials()
    {
        var sig = _step + "#" + string.Join("|", _host.Map.ZoneKinds.Select(k => k.Name + k.Tint));
        if (sig == _matSig) return;
        _matSig = sig;
        foreach (var (kind, t) in _kinds)
        {
            var colour = Tint(kind);
            if (t.Fill != null) t.Fill.AlbedoColor = new Color(colour, Fill);
            if (t.Edge != null) t.Edge.AlbedoColor = new Color(colour, 0.8f);
        }
    }

    /// <summary>
    /// A kind is selected: only it is drawn, in bands of its tint and white that move (one material offset per frame; still with
    /// reduce motion), and a second, faint pass without a depth test shows it where buildings, trees and hills cover it.
    /// Built when the kind, its tint or the zones change.
    /// </summary>
    void UpdateSelectedKind(string kind)
    {
        var colour = Tint(kind);
        var sig = kind + "#" + colour.ToHtml() + "#" + _sig;
        if (sig != _selSig)
        {
            _selSig = sig;
            _selFill = null;
            _selBudget.Count = 0; _selBudget.Truncated = false;
            if (_selMesh == null)
            {
                _selMesh = new MeshInstance3D { Name = "ZoneOverlaySelected", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                _host.Outlines.Root.AddChild(_selMesh);
            }
            var mesh = new ArrayMesh();
            if (_kinds.GetValueOrDefault(kind) is { } all)
            {
                var t = new AreaSet { Name = kind }; // a copy: the materials of the mesh of all kinds stay its own
                t.Outers.AddRange(all.Outers); t.Holes.AddRange(all.Holes);
                AreaSurfaces.Build(_host.Map, mesh, t, colour, FillSelected, 6, _selBudget, MarchTexture(colour));
                if (t.Fill != null) { t.Fill.AlbedoColor = new Color(1, 1, 1, FillSelected); t.Fill.NextPass = GhostFill(colour); }
                if (t.Edge != null) { t.Edge.AlbedoColor = new Color(colour.Lightened(0.55f), 0.95f); t.Edge.NextPass = GhostEdge(colour); }
                _selFill = t.Fill;
            }
            _selMesh.Mesh = mesh;
            _selMesh.Visible = true;
        }
        if (_selFill != null && !WaterSurfaces.ReduceMotion())
        {
            _selFill.Uv1Offset = new Vector3((float)(Time.GetTicksMsec() / 1000.0 * MarchTilesPerS % 0.5), 0, 0); // 0.5 tile = one period: seamless
            if (_ghostFill != null) _ghostFill.Uv1Offset = _selFill.Uv1Offset;
        }
    }

    /// <summary>The second pass of the selected kind's fill: the same bands, faint and without a depth test, so the part behind an obstacle still shows.</summary>
    StandardMaterial3D GhostFill(Color tint)
    {
        if (_ghostFill == null)
        {
            _ghostFill = AreaSurfaces.Material(5);
            _ghostFill.NoDepthTest = true;
            _ghostFill.Uv1Triplanar = true;
            _ghostFill.Uv1WorldTriplanar = true;
            _ghostFill.Uv1Scale = Vector3.One / AreaSurfaces.StripeTileM;
        }
        _ghostFill.AlbedoTexture = MarchTexture(tint);
        _ghostFill.AlbedoColor = new Color(1, 1, 1, FillGhost);
        return _ghostFill;
    }

    StandardMaterial3D GhostEdge(Color tint)
    {
        _ghostEdge ??= AreaSurfaces.Material(5);
        _ghostEdge.NoDepthTest = true;
        _ghostEdge.AlbedoColor = new Color(tint.Lightened(0.55f), EdgeGhost);
        return _ghostEdge;
    }

    // ---- test hooks ----

    public string NoteText => _note.Text;

    /// <summary>Alpha of a kind's fill in the overlay (0: not drawn, a kind other than the selected one); -1 when it has no surface.</summary>
    public float FillAlpha(string kind) => _tool.SelectedKind is { } sel
        ? (kind == sel && _selMesh is { Visible: true } && _selFill != null ? _selFill.AlbedoColor.A : 0f)
        : _kinds.GetValueOrDefault(kind)?.Fill is { } m && _mesh is { Visible: true } ? m.AlbedoColor.A : -1f;

    /// <summary>Run the overlay now, until every kind is built (smoke tests; normally one kind per frame).</summary>
    public void Now()
    {
        for (var i = 0; i < 128 && (_step < Math.Max(_order.Count, 1) || _arrayMesh == null); i++) { Update(); if (_order.Count == 0 || _tool.SelectedKind != null) break; }
    }
}

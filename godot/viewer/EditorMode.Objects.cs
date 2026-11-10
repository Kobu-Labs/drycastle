using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Suchohrad.MapLoader;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// Placed objects in the editor (Editor 7): the palette, placement with a ghost, selecting, moving and rotating
/// with a gizmo, field edits, duplicate and delete. Every completed gesture is ONE save of edits.json (see Mutate,
/// which also records it for undo); the viewer then shows it at once through the watcher's instant objects path
/// (EditsWatcher.NotifySaved), no pipeline run. Like the rest of the editor, every control and key calls one of the
/// public methods below, so the headless smoke drives the same code without synthetic mouse events.
/// </summary>
public partial class EditorMode
{
    public const double MoveSnap = 0.5, RotateSnap = 15.0, DuplicateOffset = 1.0, NudgeStep = 0.5;
    public static readonly string[] Categories = { "car", "slab", "pot", "flower", "bush", "tree", "wall", "fence", "other" };
    const float PickMargin = 0.15f, RingBand = 0.45f;

    public sealed record ObjectInfo(string Id, string Parcel, string Asset, double X, double Y, double Rotation, double Scale, string? Colour);
    public sealed record PaletteEntry(string Category, string Id, string Name, bool Visible);

    // ---- helpers ----

    (double x, double y) RawAbs(Vector3 ground)
    {
        var o = Map.OriginEpsg5514!.Value;
        return (o.X + ground.X, o.Y - ground.Z);
    }

    static double Snap(double v, double step) => Math.Round(v / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>Absolute S-JTSK position of a ground point (map metres), snapped to 0.5 m unless free, 3 decimals.</summary>
    public (double x, double y) SnapAbs(Vector3 ground, bool free)
    {
        var (x, y) = RawAbs(ground);
        if (!free) { x = Snap(x, MoveSnap); y = Snap(y, MoveSnap); }
        return (Math.Round(x, 3), Math.Round(y, 3));
    }

    ParcelInfo? ParcelAtAbs(double x, double y)
    {
        var l = FromAbsolute(x, y);
        return Map.ParcelAt(l.X, l.Y);
    }

    public string? ParcelAtAbsolute(double x, double y) => Map.OriginEpsg5514 == null ? null : ParcelAtAbs(x, y)?.Id;

    string AssetName(string id) => Map.AssetCatalog.FirstOrDefault(a => a.Id == id)?.Name ?? id;

    static double Mod360(double d) { var m = d % 360.0; return m < 0 ? m + 360.0 : m; }

    static IEnumerable<MeshInstance3D> Meshes(Node n)
    {
        if (n is MeshInstance3D m) yield return m;
        foreach (var c in n.GetChildren())
            foreach (var x in Meshes(c)) yield return x;
    }

    /// <summary>Objects of the file (the editor's view of the disk).</summary>
    public IReadOnlyList<ObjectInfo> AllObjects()
        => ObjectEdits.All(Disk()).Select(t => ToInfo(t.pid, t.obj)).OrderBy(o => o.Id, StringComparer.Ordinal).ToList();

    static ObjectInfo ToInfo(string pid, Obj o)
    {
        var (x, y) = ObjectEdits.Position(o);
        return new ObjectInfo(o.GetValueOrDefault("id") as string ?? "", pid, o.GetValueOrDefault("asset") as string ?? "", x, y,
            ObjectEdits.Rotation(o), ObjectEdits.Scale(o), o.GetValueOrDefault("colour") as string);
    }

    public ObjectInfo? ObjectData(string id)
        => ObjectEdits.Find(Disk(), id) is { } f ? ToInfo(f.pid, f.obj) : null;

    public ObjectInfo? SelectedObject => SelectedObjectId == null ? null : ObjectData(SelectedObjectId);

    // ---- placement mode and the ghost ----

    public string? PlacementAsset { get; private set; }
    public double GhostRotation { get; private set; }
    public Node3D? Ghost => _ghost;
    /// <summary>Absolute S-JTSK position the ghost last snapped to; null before the first update.</summary>
    public (double x, double y)? GhostAbsolute { get; private set; }
    public bool GhostInParcel { get; private set; }
    /// <summary>When set the ghost stays where UpdateGhost put it instead of following the mouse (smoke, screenshots).</summary>
    public bool GhostPinned { get; set; }
    Node3D? _ghost;
    StandardMaterial3D _ghostOk = null!, _ghostBad = null!;
    bool? _ghostTint;

    /// <summary>Choose an asset from the palette: placement mode on (a ghost follows the cursor, a click places).</summary>
    public bool ArmPlacement(string asset)
    {
        var info = Map.AssetCatalog.FirstOrDefault(a => a.Id == asset);
        if (info == null) { Say($"unknown asset {asset}", true); return false; }
        if (Map.OriginEpsg5514 == null) { Say("this map has no origin_epsg5514 (export it again, map 0.4.0+)", true); return false; }
        if (GateArmed) CancelGate();
        DisarmPlacement(false);
        PlacementAsset = asset;
        _ghostTint = null;
        _ghost = new Node3D { Name = "Ghost", Visible = false };
        var model = Map.CreateObjectModel(asset, null, out _);
        _ghost.AddChild(model);
        foreach (var mi in Meshes(model))
        {
            mi.MaterialOverride = _ghostOk;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        MarkerRoot.AddChild(_ghost);
        GhostAbsolute = null;
        Say($"placing {info.Name}: click to place (stays armed), R / Shift+R rotate 15 deg, Ctrl = free position, Esc or right click stops");
        _panelDirty = true;
        return true;
    }

    /// <summary>Leave placement mode (Esc, right click, choosing the same asset again).</summary>
    public void DisarmPlacement(bool say = true)
    {
        var was = PlacementAsset != null;
        PlacementAsset = null;
        if (_ghost != null) { _ghost.GetParent()?.RemoveChild(_ghost); _ghost.QueueFree(); _ghost = null; }
        GhostAbsolute = null;
        if (was && say) Say("placement off");
        _panelDirty = true;
    }

    public void RotateGhost(double deg)
    {
        GhostRotation = Mod360(GhostRotation + deg);
        if (_ghost != null) _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        _panelDirty = true;
    }

    /// <summary>Move the ghost to a ground point (snapped to 0.5 m unless free); red when the point is in no parcel.</summary>
    public void UpdateGhost(Vector3 ground, bool free)
    {
        if (_ghost == null || Map.OriginEpsg5514 == null) return;
        var (x, y) = SnapAbs(ground, free);
        GhostAbsolute = (x, y);
        var l = FromAbsolute(x, y);
        _ghost.Position = new Vector3(l.X, Map.GroundHeightOrMean(l.X, l.Y), l.Y);
        _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        _ghost.Visible = true;
        var inParcel = Map.ParcelAt(l.X, l.Y) != null;
        if (_ghostTint != inParcel)
        {
            _ghostTint = inParcel;
            GhostInParcel = inParcel;
            foreach (var mi in Meshes(_ghost)) mi.MaterialOverride = inParcel ? _ghostOk : _ghostBad;
        }
    }

    /// <summary>Left click while armed: append the asset to the objects of the parcel under the point.</summary>
    public bool PlaceAt(Vector3 ground, bool free = false)
    {
        if (PlacementAsset == null) { Say("choose an asset in the palette first", true); return false; }
        if (Map.OriginEpsg5514 == null) { Say("this map has no origin_epsg5514", true); return false; }
        var (x, y) = SnapAbs(ground, free);
        var parcel = ParcelAtAbs(x, y);
        if (parcel == null) { Say("cannot place here: the point is in no parcel", true); return false; }
        var asset = PlacementAsset;
        var rot = GhostRotation;
        var name = AssetName(asset);
        return Mutate(root =>
        {
            var id = ObjectEdits.NextId(root);
            ObjectEdits.Add(root, parcel.Id, ObjectEdits.Make(id, asset, x, y, rot));
            return (true, $"placed {id} ({name}) on {parcel.Id}");
        }, $"place {name}");
    }

    // ---- selection ----

    public string? SelectedObjectId { get; private set; }

    public bool SelectObject(string? id)
    {
        if (id != null && ObjectData(id) == null) return false;
        if (id == SelectedObjectId) return true;
        SelectedObjectId = id;
        _panelDirty = true;
        return true;
    }

    readonly Dictionary<ulong, Aabb?> _aabbCache = new();

    /// <summary>The object's bounding box in its own (unscaled) frame, from all its meshes; null when it has none.</summary>
    public Aabb? LocalAabb(Node3D node)
    {
        if (_aabbCache.TryGetValue(node.GetInstanceId(), out var cached)) return cached;
        if (_aabbCache.Count > 4000) _aabbCache.Clear();
        Aabb? box = null;
        var inv = node.GlobalTransform.AffineInverse();
        foreach (var mi in Meshes(node))
        {
            if (mi.Mesh == null) continue;
            var b = (inv * mi.GlobalTransform) * mi.GetAabb();
            box = box == null ? b : box.Value.Merge(b);
        }
        _aabbCache[node.GetInstanceId()] = box;
        return box;
    }

    /// <summary>The nearest object whose box (grown by 15 cm) the ray hits; objects take priority over parcels.</summary>
    public string? PickObject(Vector3 origin, Vector3 dir)
    {
        string? best = null;
        var bestD = float.MaxValue;
        foreach (var (id, node) in Map.ObjectNodes)
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) continue;
            if (node.GlobalPosition.DistanceSquaredTo(origin) > 600f * 600f) continue;
            if (LocalAabb(node) is not { } box) continue;
            var t = node.GlobalTransform;
            var inv = t.AffineInverse();
            if (RayBox(box.Grow(PickMargin), inv * origin, inv.Basis * dir) is not { } hit) continue;
            var d = origin.DistanceTo(t * hit);
            if (d < bestD) { bestD = d; best = id; }
        }
        return best;
    }

    /// <summary>Slab test of a ray against a box; the entry point (or the origin when inside), null on a miss.</summary>
    static Vector3? RayBox(Aabb box, Vector3 from, Vector3 dir)
    {
        float tmin = 0f, tmax = float.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var o = from[i]; var d = dir[i]; var lo = box.Position[i]; var hi = lo + box.Size[i];
            if (Mathf.Abs(d) < 1e-9f) { if (o < lo || o > hi) return null; continue; }
            var t1 = (lo - o) / d; var t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Mathf.Max(tmin, t1); tmax = Mathf.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return from + dir * tmin;
    }

    public string? PickObjectScreen(Vector2 screen)
        => PickObject(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen));

    // ---- the gizmo: a box around the object, a rotation ring and an arrow at its feet ----

    Node3D _gizmoRoot = null!, _gizBox = null!, _gizRing = null!;
    MeshInstance3D _gizBoxMesh = null!, _gizRingMesh = null!, _gizArrowMesh = null!, _gizKnob = null!;
    string _gizKey = "";
    public float GizmoRadius { get; private set; }
    public bool GizmoVisible => _gizmoRoot.Visible;

    static StandardMaterial3D Unshaded(Color c, bool noDepth)
        => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = noDepth };

    void BuildGizmo()
    {
        _gizmoRoot = new Node3D { Name = "ObjectGizmo", Visible = false };
        _gizBox = new Node3D { Name = "GizmoBox" };
        _gizBoxMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Unshaded(new Color(1f, 0.92f, 0.1f), false) };
        _gizBox.AddChild(_gizBoxMesh);
        _gizRing = new Node3D { Name = "GizmoRing" };
        var ringColour = new Color(0.1f, 0.95f, 1f);
        _gizRingMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Unshaded(ringColour, true) };
        _gizArrowMesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Unshaded(ringColour, true) };
        _gizKnob = new MeshInstance3D
        {
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = Unshaded(new Color(1f, 0.2f, 0.9f), true),
        };
        _gizRing.AddChild(_gizRingMesh); _gizRing.AddChild(_gizArrowMesh); _gizRing.AddChild(_gizKnob);
        _gizmoRoot.AddChild(_gizBox);
        _gizmoRoot.AddChild(_gizRing);
        MarkerRoot.AddChild(_gizmoRoot);
    }

    static ArrayMesh Strip(List<Vector3> verts, List<int> idx, Mesh.PrimitiveType type = Mesh.PrimitiveType.Triangles)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(type, arrays);
        return mesh;
    }

    void RebuildGizmoShapes(Aabb box, float radius)
    {
        // wireframe box (12 edges) in the object's own frame
        var v = new List<Vector3>();
        for (var i = 0; i < 8; i++) v.Add(box.GetEndpoint(i));
        var edges = new[] { 0, 1, 1, 5, 5, 4, 4, 0, 2, 3, 3, 7, 7, 6, 6, 2, 0, 2, 1, 3, 4, 6, 5, 7 };
        _gizBoxMesh.Mesh = Strip(v, edges.ToList(), Mesh.PrimitiveType.Lines);
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
        _gizRingMesh.Mesh = Strip(rv, ri);
        var av = new List<Vector3> { new(0, 0, -0.06f), new(0, 0, 0.06f), new(radius, 0, 0.06f), new(radius, 0, -0.06f) };
        _gizArrowMesh.Mesh = Strip(av, new List<int> { 0, 1, 2, 0, 2, 3 });
        _gizKnob.Position = new Vector3(radius, 0, 0);
    }

    /// <summary>Follow the selected object: box, ring at its feet (radius from its footprint) and the arrow showing rotation_deg.</summary>
    void UpdateGizmo()
    {
        var node = SelectedObjectId != null && Map.ObjectNodes.TryGetValue(SelectedObjectId, out var n) && GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n : null;
        var box = node == null ? null : LocalAabb(node);
        _gizmoRoot.Visible = Active && node != null && box != null;
        if (node == null || box == null) return;
        var b = box.Value;
        var sc = node.Scale.X;
        var radius = Mathf.Max(0.9f, 0.5f * Mathf.Sqrt(b.Size.X * b.Size.X + b.Size.Z * b.Size.Z) * sc + 0.6f);
        var key = $"{node.GetInstanceId()}|{radius:F2}";
        if (key != _gizKey) { _gizKey = key; GizmoRadius = radius; RebuildGizmoShapes(b, radius); }
        _gizBox.GlobalTransform = node.GlobalTransform;
        _gizRing.GlobalPosition = node.GlobalPosition + Vector3.Up * 0.1f;
        _gizRing.GlobalRotation = new Vector3(0, node.GlobalRotation.Y, 0);
    }

    /// <summary>True when a ground point lies on the rotation ring of the selected object (a press there starts a rotation).</summary>
    public bool OnRotationRing(Vector3 ground)
    {
        if (SelectedObjectId == null || !Map.ObjectNodes.TryGetValue(SelectedObjectId, out var n) || !GizmoVisible) return false;
        var d = new Vector2(ground.X - n.GlobalPosition.X, ground.Z - n.GlobalPosition.Z).Length();
        return Mathf.Abs(d - GizmoRadius) <= RingBand;
    }

    // ---- gestures: move and rotate ----

    enum DragKind { None, Move, Rotate }
    DragKind _drag;
    string? _dragId;
    (double x, double y, double rot) _dragStart;
    (double dx, double dy) _grab;
    (double x, double y, double rot) _dragNow;
    bool _dragMoved;
    public bool Dragging => _drag != DragKind.None;
    public bool DragIsRotation => _drag == DragKind.Rotate;

    /// <summary>Select the object and start moving it; the grab point keeps its offset to the object's position.</summary>
    public bool StartMove(string id, Vector3 grabGround)
    {
        var o = ObjectData(id);
        if (o == null || Map.OriginEpsg5514 == null) return false;
        SelectObject(id);
        var (gx, gy) = RawAbs(grabGround);
        _grab = (o.X - gx, o.Y - gy);
        _drag = DragKind.Move; _dragId = id; _dragStart = _dragNow = (o.X, o.Y, o.Rotation); _dragMoved = false;
        return true;
    }

    public bool StartRotate(string id)
    {
        var o = ObjectData(id);
        if (o == null || Map.OriginEpsg5514 == null) return false;
        SelectObject(id);
        _drag = DragKind.Rotate; _dragId = id; _dragStart = _dragNow = (o.X, o.Y, o.Rotation); _dragMoved = false;
        return true;
    }

    /// <summary>Drag: the object follows the ground point (0.5 m grid unless free), or the arrow turns towards it (15 degrees unless free). Only the view changes until EndDrag.</summary>
    public void DragTo(Vector3 ground, bool free)
    {
        if (_drag == DragKind.None || _dragId == null) return;
        var (gx, gy) = RawAbs(ground);
        if (_drag == DragKind.Move)
        {
            double x = gx + _grab.dx, y = gy + _grab.dy;
            if (!free) { x = Snap(x, MoveSnap); y = Snap(y, MoveSnap); }
            _dragNow = (Math.Round(x, 3), Math.Round(y, 3), _dragStart.rot);
        }
        else
        {
            var a = Math.Atan2(gy - _dragStart.y, gx - _dragStart.x) * 180.0 / Math.PI;
            a = free ? Math.Round(a, 1) : Snap(a, RotateSnap);
            _dragNow = (_dragStart.x, _dragStart.y, Math.Round(Mod360(a), 3));
        }
        if (_dragNow != _dragStart) _dragMoved = true;
        PreviewObject(_dragId, _dragNow.x, _dragNow.y, _dragNow.rot);
    }

    void PreviewObject(string id, double x, double y, double rot)
    {
        if (!Map.ObjectNodes.TryGetValue(id, out var node) || !GodotObject.IsInstanceValid(node)) return;
        var l = FromAbsolute(x, y);
        node.Position = new Vector3(l.X, Map.GroundHeightOrMean(l.X, l.Y), l.Y);
        node.RotationDegrees = new Vector3(0, (float)rot, 0);
    }

    /// <summary>Release: one save with the final pose (nothing when it did not change). False when nothing was saved.</summary>
    public bool EndDrag()
    {
        if (_drag == DragKind.None || _dragId == null) return false;
        var kind = _drag; var id = _dragId; var now = _dragNow; var start = _dragStart; var moved = _dragMoved;
        _drag = DragKind.None; _dragId = null;
        if (!moved) return false;
        var ok = kind == DragKind.Move ? MoveObject(id, now.x, now.y) : SetObjectRotation(now.rot, id);
        if (!ok) PreviewObject(id, start.x, start.y, start.rot);
        return ok;
    }

    public void CancelDrag()
    {
        if (_drag == DragKind.None || _dragId == null) return;
        PreviewObject(_dragId, _dragStart.x, _dragStart.y, _dragStart.rot);
        _drag = DragKind.None; _dragId = null;
    }

    // ---- saved edits of one object ----

    /// <summary>Set the position (absolute S-JTSK metres). The object goes to the parcel now under it; refused when there is none.</summary>
    public bool MoveObject(string id, double x, double y)
    {
        if (Map.OriginEpsg5514 == null) { Say("this map has no origin_epsg5514", true); return false; }
        var parcel = ParcelAtAbs(x, y);
        if (parcel == null) { Say("cannot move there: the point is in no parcel", true); return false; }
        return Mutate(root =>
        {
            if (ObjectEdits.Find(root, id) is not { } f) return (false, $"object {id} no longer exists");
            ObjectEdits.SetPosition(f.obj, x, y);
            var home = f.pid != parcel.Id;
            if (home) { ObjectEdits.Remove(root, id); ObjectEdits.Add(root, parcel.Id, f.obj); }
            return (true, $"moved {id}" + (home ? $" to {parcel.Id}" : ""));
        }, $"move {id}");
    }

    bool EditObject(string id, string what, Func<Obj, bool> change)
        => Mutate(root =>
        {
            if (ObjectEdits.Find(root, id) is not { } f) return (false, $"object {id} no longer exists");
            return change(f.obj) ? (true, $"{what} {id}") : (false, $"cannot {what} {id}");
        }, $"{what} {id}");

    public bool SetObjectPosition(double x, double y) => SelectedObjectId != null && MoveObject(SelectedObjectId, x, y);

    public bool SetObjectRotation(double deg, string? id = null)
    {
        id ??= SelectedObjectId;
        return id != null && EditObject(id, "rotate", o => { ObjectEdits.SetRotation(o, deg); return true; });
    }

    public bool SetObjectScale(double scale)
    {
        if (SelectedObjectId == null) return false;
        if (!(scale > 0) || !double.IsFinite(scale)) { Say("scale must be a number > 0", true); return false; }
        return EditObject(SelectedObjectId, "scale", o => { ObjectEdits.SetScale(o, scale); return true; });
    }

    /// <summary>Set or clear (null) the colour override.</summary>
    public bool SetObjectColour(string? colour)
    {
        if (SelectedObjectId == null) return false;
        if (colour != null && !EditSchema.ColourRx.IsMatch(colour)) { Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        return EditObject(SelectedObjectId, colour == null ? "clear colour of" : "colour", o => { ObjectEdits.SetColour(o, colour); return true; });
    }

    public bool NudgeObject(double dx, double dy)
    {
        var o = SelectedObject;
        return o != null && MoveObject(o.Id, o.X + dx, o.Y + dy);
    }

    /// <summary>Rotate the selected object by a number of degrees (R and Shift+R with nothing armed).</summary>
    public bool RotateSelected(double deg)
    {
        var o = SelectedObject;
        return o != null && SetObjectRotation(Mod360(o.Rotation + deg), o.Id);
    }

    public bool DeleteSelected()
    {
        if (SelectedObjectId == null) { Say("no object selected", true); return false; }
        var id = SelectedObjectId;
        var ok = Mutate(root => ObjectEdits.Remove(root, id) ? (true, $"deleted {id}") : (false, $"object {id} no longer exists"), $"delete {id}");
        if (ok) { SelectedObjectId = null; _panelDirty = true; }
        return ok;
    }

    /// <summary>Copy the selected object 1 m east (into the parcel under the copy) and select the copy.</summary>
    public bool DuplicateSelected()
    {
        var o = SelectedObject;
        if (o == null) { Say("no object selected", true); return false; }
        var x = o.X + DuplicateOffset;
        var parcel = ParcelAtAbs(x, o.Y);
        if (parcel == null) { Say("cannot duplicate there: the copy would be in no parcel", true); return false; }
        string? newId = null;
        var ok = Mutate(root =>
        {
            if (ObjectEdits.Find(root, o.Id) is not { } f) return (false, $"object {o.Id} no longer exists");
            var copy = ObjectEdits.CloneObj(f.obj);
            newId = ObjectEdits.NextId(root);
            copy["id"] = newId;
            ObjectEdits.SetPosition(copy, x, o.Y);
            ObjectEdits.Add(root, parcel.Id, copy);
            return (true, $"duplicated {o.Id} as {newId}");
        }, $"duplicate {o.Id}");
        if (ok && newId != null) { SelectedObjectId = null; SelectObject(newId); }
        return ok;
    }

    /// <summary>Arrow keys: one step in the camera's screen directions snapped to the map axes (right, forward as -1, 0, 1).</summary>
    public bool NudgeScreen(int right, int forward)
    {
        var f = -FlyCam.GlobalBasis.Z; var r = FlyCam.GlobalBasis.X;
        if (Mathf.Abs(f.Y) > 0.98f) f = FlyCam.GlobalBasis.Y;
        // map axes: east = +X, north = -Z
        var dir = new Vector2(r.X * right + f.X * forward, -(r.Z * right + f.Z * forward));
        if (dir.LengthSquared() < 1e-6f) return false;
        var step = Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y) ? new Vector2(Mathf.Sign(dir.X), 0) : new Vector2(0, Mathf.Sign(dir.Y));
        return NudgeObject(step.X * NudgeStep, step.Y * NudgeStep);
    }

    // ---- mouse flow ----

    /// <summary>Left press with nothing armed: rotation ring, then an object, then the parcel under the cursor.</summary>
    void LeftPress(Vector2 screen, bool shift)
    {
        var o = FlyCam.ProjectRayOrigin(screen); var d = FlyCam.ProjectRayNormal(screen);
        var hasGround = RayToGround(o, d, out var gp);
        if (SelectedObjectId != null && hasGround && OnRotationRing(gp) && StartRotate(SelectedObjectId)) return;
        if (PickObject(o, d) is { } id)
        {
            if (!hasGround && Map.ObjectNodes.TryGetValue(id, out var node)) gp = node.GlobalPosition;
            StartMove(id, gp);
            return;
        }
        SelectObject(null);
        var hit = PickScreen(screen, out _)?.Id;
        if (Tool == EditorTool.Group) { if (hit != null) Click(hit, true); return; } // marking: every click toggles a parcel
        Click(hit, shift);
    }

    void UpdateObjectTools()
    {
        var mouse = GetViewport().GetMousePosition();
        var free = Input.IsKeyPressed(Key.Ctrl);
        if (_ghost != null && !GhostPinned)
        {
            if (!_looking && !OverUi(mouse) && RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var g)) UpdateGhost(g, free);
            else _ghost.Visible = false;
        }
        if (_drag != DragKind.None && RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var dg)) DragTo(dg, free);
        UpdateGizmo();
    }

    // ---- the palette and the object fields (UI) ----

    LineEdit _filterEdit = null!;
    Label _objStatus = null!, _objCount = null!, _objAsset = null!, _undoInfo = null!;
    VBoxContainer _objBox = null!;
    SpinBox _objX = null!, _objY = null!, _objRot = null!, _objScale = null!;
    Button _objColourReset = null!;
    ColorPickerButton _objColour = null!;
    Button _btnUndo = null!, _btnRedo = null!; // the rail buttons (EditorUi.cs)
    readonly List<(AssetInfo info, Button btn)> _palButtons = new();
    string _filter = "";

    void BuildObjectsSection(VBoxContainer body)
    {
        _ghostOk = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.95f, 1f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Emission = new Color(0.05f, 0.25f, 0.3f), EmissionEnabled = true,
        };
        _ghostBad = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.25f, 0.2f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Emission = new Color(0.3f, 0.04f, 0.03f), EmissionEnabled = true,
        };
        BuildGizmo();

        _undoInfo = Note();
        body.AddChild(_undoInfo);
        _objCount = Note();
        body.AddChild(_objCount);

        // selected object
        _objBox = new VBoxContainer { Visible = false };
        var head = new Label { Text = "Selected object" };
        head.ThemeTypeVariation = "Header";
        _objBox.AddChild(head);
        _objAsset = Note("");
        _objBox.AddChild(_objAsset);
        _objX = Spin(-3e6, 3e6, 0.001, " m");
        _objY = Spin(-3e6, 3e6, 0.001, " m");
        _objX.ValueChanged += _ => { if (!_syncing) SetObjectPosition(_objX.Value, _objY.Value); };
        _objY.ValueChanged += _ => { if (!_syncing) SetObjectPosition(_objX.Value, _objY.Value); };
        _objBox.AddChild(Row("x", _objX, 60));
        _objBox.AddChild(Row("y", _objY, 60));
        _objRot = Spin(-100000, 100000, 0.1, " deg");
        _objRot.ValueChanged += v => { if (!_syncing) SetObjectRotation(v); };
        _objBox.AddChild(Row("rotation", _objRot, 60));
        _objScale = Spin(0.01, 1000, 0.01);
        _objScale.ValueChanged += v => { if (!_syncing) SetObjectScale(v); };
        _objBox.AddChild(Row("scale", _objScale, 60));
        _objColour = new ColorPickerButton { CustomMinimumSize = new Vector2(80, 28), FocusMode = Control.FocusModeEnum.None, EditAlpha = false };
        _objColourReset = ResetBtn(() => SetObjectColour(null));
        // one undo step per picked colour: commit when the picker closes
        _objColour.PopupClosed += () =>
        {
            var hex = "#" + _objColour.Color.ToHtml(false);
            if (!_syncing && !string.Equals(SelectedObject?.Colour, hex, StringComparison.OrdinalIgnoreCase)) SetObjectColour(hex);
        };
        var cr = Row("colour", _objColour, 60); cr.AddChild(_objColourReset);
        _objBox.AddChild(cr);
        var br = new HBoxContainer();
        var dup = Btn("Duplicate (Ctrl+D)", () => DuplicateSelected());
        var del = Btn("Delete (Del)", () => DeleteSelected());
        foreach (var b in new[] { dup, del }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; br.AddChild(b); }
        _objBox.AddChild(br);
        _objBox.AddChild(Note("drag the object to move it (0.5 m grid), drag the ring to rotate (15 degrees); Ctrl = free; arrows nudge 0.5 m"));
        body.AddChild(_objBox);

        // palette: category tabs (a render of one item each), then a grid of item renders
        var ph = new Label { Text = "Place an object", ThemeTypeVariation = "Header" };
        body.AddChild(ph);
        _objStatus = Note("");
        body.AddChild(_objStatus);
        _filterEdit = new LineEdit { PlaceholderText = "search all objects", ClearButtonEnabled = true };
        _filterEdit.TextChanged += t => SetPaletteFilter(t);
        body.AddChild(_filterEdit);

        var catalog = Map.AssetCatalog;
        var cats = Categories.Concat(catalog.Select(a => a.Category).Distinct().Except(Categories).OrderBy(x => x, StringComparer.Ordinal)).ToList();
        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        tabs.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        body.AddChild(tabs);
        var tabGroup = new ButtonGroup();
        foreach (var cat in cats)
        {
            var items = catalog.Where(a => a.Category == cat).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0) continue;
            var capture = cat;
            var tab = new Button
            {
                ToggleMode = true, ButtonGroup = tabGroup, FocusMode = Control.FocusModeEnum.None, ThemeTypeVariation = "TabButton",
                CustomMinimumSize = new Vector2(EditorTheme.RailButton, EditorTheme.RailButton), ExpandIcon = true,
                Text = items.Count == 0 ? "" : cat[..Math.Min(cat.Length, 3)], // replaced by the render when it arrives
                TooltipText = $"{Cap(cat)} ({items.Count})",
            };
            tab.Pressed += () => SetPaletteGroupOpen(capture, true);
            tabs.AddChild(tab);
            _palTabs[cat] = (tab, items[0].Id, items.Count);
        }
        _palTitle = new Label { ThemeTypeVariation = "Header" };
        body.AddChild(_palTitle);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        grid.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        body.AddChild(grid);
        foreach (var a in catalog.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var info = a;
            var b = new Button
            {
                Text = a.Name, ToggleMode = true, FocusMode = Control.FocusModeEnum.None, ClipText = true, ExpandIcon = true, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TooltipText = $"{a.Name}\n{a.Id}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "PaletteButton",
                CustomMinimumSize = new Vector2(0, 128), IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
            };
            b.Pressed += () => { if (PlacementAsset == info.Id) DisarmPlacement(); else ArmPlacement(info.Id); };
            grid.AddChild(b);
            _palButtons.Add((a, b));
        }
        _palActive = cats.FirstOrDefault(c => _palTabs.ContainsKey(c)) ?? "";
        ApplyPaletteLayout();
        if (DisplayServer.GetName() != "headless") StartThumbnails(catalog);
    }

    AssetThumbs? _thumbs;
    Label _palTitle = null!;
    string _palActive = "";
    readonly Dictionary<string, (Button btn, string sample, int count)> _palTabs = new();

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void StartThumbnails(IReadOnlyList<AssetInfo> catalog)
    {
        _thumbs = new AssetThumbs { Source = id => Map.InstantiateAsset(id) };
        AddChild(_thumbs);
        _thumbs.Ready += (id, tex) =>
        {
            foreach (var (a, b) in _palButtons) if (a.Id == id) b.Icon = tex;
            foreach (var (_, t) in _palTabs) if (t.sample == id) { t.btn.Icon = tex; t.btn.Text = ""; }
        };
        // the visible category first, then the rest
        _thumbs.Request(catalog.Where(a => a.Category == _palActive).Select(a => a.Id).Concat(catalog.Select(a => a.Id)));
    }

    /// <summary>Show one category of the palette (open = true); false does nothing, a category is always shown.</summary>
    public void SetPaletteGroupOpen(string category, bool open)
    {
        if (!open || !_palTabs.ContainsKey(category)) return;
        _palActive = category;
        ApplyPaletteLayout();
    }

    public void SetPaletteFilter(string text)
    {
        _filter = text.Trim();
        if (_filterEdit.Text != text) _filterEdit.Text = text;
        ApplyPaletteLayout();
    }

    static bool Matches(AssetInfo a, string f)
        => f == "" || a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(f, StringComparison.OrdinalIgnoreCase)
           || a.Category.Contains(f, StringComparison.OrdinalIgnoreCase);

    void ApplyPaletteLayout()
    {
        foreach (var (a, b) in _palButtons) b.Visible = _filter == "" ? a.Category == _palActive : Matches(a, _filter);
        foreach (var (cat, t) in _palTabs) t.btn.SetPressedNoSignal(_filter == "" && cat == _palActive);
        var n = _palButtons.Count(p => p.btn.Visible);
        _palTitle.Text = _filter == "" ? $"{Cap(_palActive)} ({n})" : n == 0 ? $"Nothing matches \"{_filter}\"" : $"{n} match \"{_filter}\"";
    }

    /// <summary>The palette as listed: every manifest asset with its category and name; Visible is false when the filter or category hides it.</summary>
    public IReadOnlyList<PaletteEntry> Palette => _palButtons.Select(p => new PaletteEntry(p.info.Category, p.info.Id, p.info.Name, p.btn.Visible)).ToList();
    public string PaletteGroupTitle(string category) => _palTabs.TryGetValue(category, out var t) ? $"{category} ({t.count})" : "";
    public bool PaletteGroupShown(string category) => _palTabs.ContainsKey(category) && (_filter == "" || _palButtons.Any(p => p.info.Category == category && p.btn.Visible));
    public bool PaletteGroupBodyShown(string category) => _filter == "" ? category == _palActive : _palButtons.Any(p => p.info.Category == category && p.btn.Visible);
    public string PaletteFilter => _filter;
    public string ObjectsStatusText => _objStatus.Text;
    public bool ObjectFieldsShown => _objBox.Visible;
    /// <summary>The panel's object fields (the real controls, so the smoke can set Value and fire the signals the mouse would).</summary>
    public SpinBox ObjectXSpin => _objX;
    public SpinBox ObjectYSpin => _objY;
    public SpinBox ObjectRotationSpin => _objRot;
    public SpinBox ObjectScaleSpin => _objScale;
    public bool ObjectColourSet => _objColourReset.Visible;
    public string UndoInfoText => _undoInfo.Text;

    void RefreshObjectsPanel()
    {
        if (SelectedObjectId != null && ObjectData(SelectedObjectId) == null) SelectedObjectId = null;
        var o = SelectedObject;
        _objBox.Visible = o != null;
        if (o != null)
        {
            _syncing = true;
            try
            {
                _objAsset.Text = $"{o.Id}  {AssetName(o.Asset)}\n{o.Asset}\nparcel entry {o.Parcel}";
                _objX.SetValueNoSignal(o.X); _objY.SetValueNoSignal(o.Y);
                _objRot.SetValueNoSignal(o.Rotation); _objScale.SetValueNoSignal(o.Scale);
                _objColourReset.Visible = o.Colour != null;
                _objColour.Color = Color.FromHtml(o.Colour ?? "#cccccc");
                _objColour.Modulate = o.Colour == null ? Grey : Colors.White;
            }
            finally { _syncing = false; }
        }
        _objStatus.Text = PlacementAsset != null
            ? $"placing {AssetName(PlacementAsset)}, rotation {GhostRotation:0} deg. Click to place, R / Shift+R rotate, Esc stops."
            : "Choose an asset to place it; click an object to select it.";
        foreach (var (a, b) in _palButtons) b.SetPressedNoSignal(a.Id == PlacementAsset);
        _btnUndo.Disabled = _undo.Count == 0;
        _btnRedo.Disabled = _redo.Count == 0;
        _undoInfo.Text = (_undo.Count == 0 ? "nothing to undo" : $"undo: {NextUndoDescription} ({_undo.Count})") + (_redo.Count == 0 ? "" : $"; redo: {NextRedoDescription} ({_redo.Count})");
        var parcels = Selection.ToList();
        _objCount.Text = parcels.Count == 0
            ? $"{ObjectEdits.All(Disk()).Count()} placed objects in the file"
            : $"{ObjectEdits.All(Disk()).Count(t => parcels.Contains(t.pid))} placed objects on {(parcels.Count == 1 ? parcels[0] : "the selection")}, {ObjectEdits.All(Disk()).Count()} in the file";
        _hintLabel.Text = Tool == EditorTool.Group && PlacementAsset == null
            ? "marking parcels: click a parcel to mark or unmark it, Enter groups the marked ones, Esc clears the marks"
            : PlacementAsset != null
            ? "placing: LMB place, R / Shift+R rotate, Ctrl free position, Esc or RMB stop, Ctrl+Z undo"
            : SelectedObjectId != null
                ? "object: drag moves, drag the ring rotates, arrows nudge, Del delete, Ctrl+D duplicate, R rotate, Ctrl+Z undo, Esc deselect"
                : "WASD fly, RMB look, LMB select, Shift+LMB add, G group, T top-down, Enter apply, Ctrl+Z undo, Esc back, F1 controls, F2 walk";
    }
}

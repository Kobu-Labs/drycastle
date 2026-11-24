using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// Placed objects in the editor (Editor 7): the palette, placement with a ghost, selecting, moving and rotating
/// with a gizmo, field edits, duplicate and delete. Every completed gesture is ONE save of edits.json (see Mutate,
/// which also records it for undo); the viewer then shows it at once through the watcher's instant objects path
/// (EditsWatcher.NotifySaved), no pipeline run. Like the rest of the editor, every control and key calls one of the
/// public methods below, so the headless smoke drives the same code without synthetic mouse events.
/// The handles are <see cref="ObjectGizmo"/>, the sidebar page is <see cref="ObjectsPanel"/>.
/// </summary>
public sealed class ObjectsTool : EditorPart
{
    public const double RotateSnap = 15.0, DuplicateOffset = 1.0, NudgeStep = 0.5;
    public static readonly string[] Categories = { "car", "slab", "pot", "flower", "bush", "tree", "wall", "fence", "other" };
    /// <summary>Categories whose palette items show their render alone once it has arrived (the name stays in the tooltip).</summary>
    public static readonly string[] IconOnlyCategories = { "pole", "connector" };
    const float PickMargin = 0.15f;

    /// <summary>A placed object. <c>Parcel</c> is the parcel the object stands in ("" on ground that is no parcel): derived from its position, an object belongs to none.</summary>
    public sealed record ObjectInfo(string Id, string Parcel, string Asset, double X, double Y, double Rotation, double Scale, string? Colour);
    public sealed record PaletteEntry(string Category, string Id, string Name, bool Visible);

    public ObjectGizmo Gizmo { get; }
    public ObjectsPanel Panel { get; }

    public ObjectsTool(IEditorHost host) : base(host)
    {
        Gizmo = new ObjectGizmo(host, this);
        Panel = new ObjectsPanel(host, this);
    }

    // ---- the parts' hooks ----

    /// <summary>The ghost follows the mouse, a drag follows the ground point, the gizmo follows the selection.</summary>
    public override void Update(double delta)
    {
        var mouse = Host.GetViewport().GetMousePosition();
        var free = Input.IsKeyPressed(Key.Ctrl);
        if (_ghost != null && !GhostPinned)
        {
            if (!Host.Looking && !Host.Chrome.OverUi(mouse) && Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(mouse), Host.FlyCam.ProjectRayNormal(mouse), out var g)) UpdateGhost(g, free);
            else _ghost.Visible = false;
        }
        if (_drag != DragKind.None && Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(mouse), Host.FlyCam.ProjectRayNormal(mouse), out var dg)) DragTo(dg, free);
        Gizmo.Update();
    }

    /// <summary>Leaving the editor drops the drag and the placement mode.</summary>
    public override void ActiveChanged(bool on)
    {
        if (on) return;
        CancelDrag();
        DisarmPlacement(false);
    }

    public void BuildPage(VBoxContainer body)
    {
        Gizmo.Build();
        Panel.Build(body);
    }

    /// <summary>The page is redrawn (the undo line and the hint bar are the editor's).</summary>
    public void RefreshPanel()
    {
        if (SelectedObjectId != null && ObjectData(SelectedObjectId) == null) SelectedObjectId = null;
        Panel.Refresh();
    }

    /// <summary>Editor shortcuts for objects; true when the key was used. Not reached while a text field has the focus.</summary>
    public bool HandleKey(InputEventKey k)
    {
        var overViewport = !Host.Chrome.OverUi(Host.GetViewport().GetMousePosition());
        if (k.CtrlPressed)
        {
            if (k.Keycode != Key.D) return false;
            DuplicateSelected();
            return true;
        }
        switch (k.Keycode)
        {
            case Key.R when PlacementAsset != null: RotateGhost(k.ShiftPressed ? -RotateSnap : RotateSnap); return true;
            case Key.R when SelectedObjectId != null: RotateSelected(k.ShiftPressed ? -RotateSnap : RotateSnap); return true;
            case Key.Delete when SelectedObjectId != null: DeleteSelected(); return true;
            case Key.Left when SelectedObjectId != null && overViewport: NudgeScreen(-1, 0); return true;
            case Key.Right when SelectedObjectId != null && overViewport: NudgeScreen(1, 0); return true;
            case Key.Up when SelectedObjectId != null && overViewport: NudgeScreen(0, 1); return true;
            case Key.Down when SelectedObjectId != null && overViewport: NudgeScreen(0, -1); return true;
        }
        return false;
    }

    // ---- helpers ----

    internal string AssetName(string id) => Host.Map.AssetCatalog.FirstOrDefault(a => a.Id == id)?.Name ?? id;

    /// <summary>Objects of the file (the editor's view of the disk).</summary>
    public IReadOnlyList<ObjectInfo> AllObjects()
        => ObjectEdits.All(Host.Edits.Disk()).Select(t => ToInfo(t.obj)).OrderBy(o => o.Id, StringComparer.Ordinal).ToList();

    internal int ObjectCount => ObjectEdits.All(Host.Edits.Disk()).Count();

    ObjectInfo ToInfo(Obj o)
    {
        var (x, y) = ObjectEdits.Position(o);
        return new ObjectInfo(o.GetValueOrDefault("id") as string ?? "", Host.Map.OriginEpsg5514 == null ? "" : Host.Map.ParcelAtAbs(x, y)?.Id ?? "", o.GetValueOrDefault("asset") as string ?? "", x, y,
            ObjectEdits.Rotation(o), ObjectEdits.Scale(o), o.GetValueOrDefault("colour") as string);
    }

    public ObjectInfo? ObjectData(string id)
        => ObjectEdits.Find(Host.Edits.Disk(), id) is { } f ? ToInfo(f.obj) : null;

    public ObjectInfo? SelectedObject => SelectedObjectId == null ? null : ObjectData(SelectedObjectId);

    // ---- placement mode and the ghost ----

    public string? PlacementAsset { get; private set; }
    public double GhostRotation { get; private set; }
    public Node3D? Ghost => _ghost;
    /// <summary>Absolute S-JTSK position the ghost last snapped to; null before the first update.</summary>
    public (double x, double y)? GhostAbsolute { get; private set; }
    /// <summary>When set the ghost stays where UpdateGhost put it instead of following the mouse (smoke, screenshots).</summary>
    public bool GhostPinned { get; set; }
    Node3D? _ghost;
    readonly StandardMaterial3D _ghostOk = new()
    {
        AlbedoColor = new Color(0.35f, 0.95f, 1f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Emission = new Color(0.05f, 0.25f, 0.3f), EmissionEnabled = true,
    };

    /// <summary>Choose an asset from the palette: placement mode on (a ghost follows the cursor, a click places).</summary>
    public bool ArmPlacement(string asset)
    {
        var info = Host.Map.AssetCatalog.FirstOrDefault(a => a.Id == asset);
        if (info == null) { Host.Say($"unknown asset {asset}", true); return false; }
        if (Host.Map.OriginEpsg5514 == null) { Host.Say("this map has no origin_epsg5514 (export it again, map 0.4.0+)", true); return false; }
        if (Host.Fence.Gates.Armed) Host.Fence.Gates.Cancel();
        DisarmPlacement(false);
        Host.Poles.StopConnecting(false);
        Host.Poles.Plan.Stop(false);
        PlacementAsset = asset;
        _ghost = new Node3D { Name = "Ghost", Visible = false };
        var model = Host.Map.CreateObjectModel(asset, null, out _);
        _ghost.AddChild(model);
        foreach (var mi in Gfx.Meshes(model))
        {
            mi.MaterialOverride = _ghostOk;
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        Host.MarkerRoot.AddChild(_ghost);
        GhostAbsolute = null;
        Host.Say($"placing {info.Name}: click to place (stays armed), R / Shift+R rotate 15 deg, Ctrl = free position, Esc or right click stops");
        Host.MarkPanelDirty();
        return true;
    }

    /// <summary>Leave placement mode (Esc, right click, choosing the same asset again).</summary>
    public void DisarmPlacement(bool say = true)
    {
        var was = PlacementAsset != null;
        PlacementAsset = null;
        if (_ghost != null) { _ghost.GetParent()?.RemoveChild(_ghost); _ghost.QueueFree(); _ghost = null; }
        GhostAbsolute = null;
        if (was && say) Host.Say("placement off");
        Host.MarkPanelDirty();
    }

    public void RotateGhost(double deg)
    {
        GhostRotation = MapFrame.Mod360(GhostRotation + deg);
        if (_ghost != null) _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        Host.MarkPanelDirty();
    }

    /// <summary>Move the ghost to a ground point (snapped to 0.5 m unless free). Any ground will do: an object belongs to no parcel.</summary>
    public void UpdateGhost(Vector3 ground, bool free)
    {
        if (_ghost == null || Host.Map.OriginEpsg5514 == null) return;
        var (x, y) = Host.Map.SnapAbs(ground, free);
        GhostAbsolute = (x, y);
        var l = Host.Map.FromAbsolute(x, y);
        _ghost.Position = new Vector3(l.X, Host.Map.GroundHeightOrMean(l.X, l.Y), l.Y);
        _ghost.RotationDegrees = new Vector3(0, (float)GhostRotation, 0);
        _ghost.Visible = true;
    }

    /// <summary>Left click while armed: place at the ground point under the cursor (stays armed).</summary>
    public void PlaceClick(Vector2 screen, bool free)
    {
        Host.GetViewport().GuiReleaseFocus();
        if (Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(screen), Host.FlyCam.ProjectRayNormal(screen), out var pp)) PlaceAt(pp, free);
        else Host.Say("click on the ground", true);
    }

    /// <summary>Append the asset to the objects of the file, at the point (a parcel is not needed).</summary>
    public bool PlaceAt(Vector3 ground, bool free = false)
    {
        if (PlacementAsset == null) { Host.Say("choose an asset in the palette first", true); return false; }
        if (Host.Map.OriginEpsg5514 == null) { Host.Say("this map has no origin_epsg5514", true); return false; }
        var (x, y) = Host.Map.SnapAbs(ground, free);
        var asset = PlacementAsset;
        var rot = GhostRotation;
        var name = AssetName(asset);
        return Host.Edits.Mutate(root =>
        {
            var id = ObjectEdits.NextId(root);
            ObjectEdits.Add(root, ObjectEdits.Make(id, asset, x, y, rot));
            return (true, $"placed {id} ({name})");
        }, $"place {name}");
    }

    // ---- selection ----

    public string? SelectedObjectId { get; private set; }

    public bool SelectObject(string? id)
    {
        if (id != null && ObjectData(id) == null) return false;
        if (id == SelectedObjectId) return true;
        SelectedObjectId = id;
        Host.MarkPanelDirty();
        return true;
    }

    readonly Dictionary<ulong, Aabb?> _aabbCache = new();

    /// <summary>The object's bounding box in its own (unscaled) frame, from all its meshes; null when it has none.</summary>
    internal Aabb? LocalAabb(Node3D node)
    {
        if (_aabbCache.TryGetValue(node.GetInstanceId(), out var cached)) return cached;
        if (_aabbCache.Count > 4000) _aabbCache.Clear();
        Aabb? box = null;
        var inv = node.GlobalTransform.AffineInverse();
        foreach (var mi in Gfx.Meshes(node))
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
        foreach (var (id, node) in Host.Map.ObjectNodes)
        {
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) continue;
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

    // ---- gestures: move and rotate ----

    enum DragKind { None, Move, Rotate }
    DragKind _drag;
    string? _dragId;
    (double x, double y, double rot) _dragStart;
    (double dx, double dy) _grab;
    (double x, double y, double rot) _dragNow;
    bool _dragMoved;
    public bool Dragging => _drag != DragKind.None;
    /// <summary>The object being dragged (null: no drag).</summary>
    public string? DraggedId => _dragId;
    public bool DragIsRotation => _drag == DragKind.Rotate;

    /// <summary>Select the object and start moving it; the grab point keeps its offset to the object's position.</summary>
    public bool StartMove(string id, Vector3 grabGround)
    {
        var o = ObjectData(id);
        if (o == null || Host.Map.OriginEpsg5514 == null) return false;
        SelectObject(id);
        var (gx, gy) = Host.Map.RawAbs(grabGround);
        _grab = (o.X - gx, o.Y - gy);
        _drag = DragKind.Move; _dragId = id; _dragStart = _dragNow = (o.X, o.Y, o.Rotation); _dragMoved = false;
        return true;
    }

    public bool StartRotate(string id)
    {
        var o = ObjectData(id);
        if (o == null || Host.Map.OriginEpsg5514 == null) return false;
        SelectObject(id);
        _drag = DragKind.Rotate; _dragId = id; _dragStart = _dragNow = (o.X, o.Y, o.Rotation); _dragMoved = false;
        return true;
    }

    /// <summary>Drag: the object follows the ground point (0.5 m grid unless free), or the arrow turns towards it (15 degrees unless free). Only the view changes until EndDrag.</summary>
    public void DragTo(Vector3 ground, bool free)
    {
        if (_drag == DragKind.None || _dragId == null) return;
        var (gx, gy) = Host.Map.RawAbs(ground);
        if (_drag == DragKind.Move)
        {
            double x = gx + _grab.dx, y = gy + _grab.dy;
            if (!free) { x = MapFrame.Snap(x, MapFrame.MoveSnap); y = MapFrame.Snap(y, MapFrame.MoveSnap); }
            _dragNow = (Math.Round(x, 3), Math.Round(y, 3), _dragStart.rot);
        }
        else
        {
            var a = Math.Atan2(gy - _dragStart.y, gx - _dragStart.x) * 180.0 / Math.PI;
            a = free ? Math.Round(a, 1) : MapFrame.Snap(a, RotateSnap);
            _dragNow = (_dragStart.x, _dragStart.y, Math.Round(MapFrame.Mod360(a), 3));
        }
        if (_dragNow != _dragStart) _dragMoved = true;
        PreviewObject(_dragId, _dragNow.x, _dragNow.y, _dragNow.rot);
    }

    void PreviewObject(string id, double x, double y, double rot)
    {
        if (!Host.Map.ObjectNodes.TryGetValue(id, out var node) || !GodotObject.IsInstanceValid(node)) return;
        var l = Host.Map.FromAbsolute(x, y);
        // an object fixed at an elevation (a house connector on a wall or roof) keeps it; the others stand on the ground
        var fixedZ = ObjectEdits.Find(Host.Edits.Disk(), id) is { } f ? ObjectEdits.Number(f.obj.GetValueOrDefault("z")) : null;
        node.Position = new Vector3(l.X, fixedZ is double z ? (float)z : Host.Map.GroundHeightOrMean(l.X, l.Y), l.Y);
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

    /// <summary>Set the position (absolute S-JTSK metres). Anywhere will do: an object belongs to no parcel.</summary>
    public bool MoveObject(string id, double x, double y)
    {
        if (Host.Map.OriginEpsg5514 == null) { Host.Say("this map has no origin_epsg5514", true); return false; }
        return Host.Edits.Mutate(root =>
        {
            if (ObjectEdits.Find(root, id) is not { } f) return (false, $"object {id} no longer exists");
            ObjectEdits.SetPosition(f.obj, x, y);
            return (true, $"moved {id}");
        }, $"move {id}");
    }

    bool EditObject(string id, string what, Func<Obj, bool> change)
        => Host.Edits.Mutate(root =>
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
        if (!(scale > 0) || !double.IsFinite(scale)) { Host.Say("scale must be a number > 0", true); return false; }
        return EditObject(SelectedObjectId, "scale", o => { ObjectEdits.SetScale(o, scale); return true; });
    }

    /// <summary>Set or clear (null) the colour override.</summary>
    public bool SetObjectColour(string? colour)
    {
        if (SelectedObjectId == null) return false;
        if (colour != null && !EditSchema.ColourRx.IsMatch(colour)) { Host.Say($"colour must look like #rrggbb, got {colour}", true); return false; }
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
        return o != null && SetObjectRotation(MapFrame.Mod360(o.Rotation + deg), o.Id);
    }

    public bool DeleteSelected()
    {
        if (SelectedObjectId == null) { Host.Say("no object selected", true); return false; }
        var id = SelectedObjectId;
        var ok = Host.Edits.Mutate(root =>
        {
            if (!ObjectEdits.Remove(root, id)) return (false, $"object {id} no longer exists");
            var gone = EditsFile.RemoveCablesOf(root, id); // a pole takes its cables with it
            return (true, $"deleted {id}" + (gone.Count > 0 ? $" and its cable{(gone.Count == 1 ? "" : "s")} {string.Join(", ", gone)}" : ""));
        }, $"delete {id}");
        if (ok) { SelectedObjectId = null; Host.MarkPanelDirty(); }
        return ok;
    }

    /// <summary>Copy the selected object 1 m east and select the copy.</summary>
    public bool DuplicateSelected()
    {
        var o = SelectedObject;
        if (o == null) { Host.Say("no object selected", true); return false; }
        var x = o.X + DuplicateOffset;
        string? newId = null;
        var ok = Host.Edits.Mutate(root =>
        {
            if (ObjectEdits.Find(root, o.Id) is not { } f) return (false, $"object {o.Id} no longer exists");
            var copy = ObjectEdits.CloneObj(f.obj);
            newId = ObjectEdits.NextId(root);
            copy["id"] = newId;
            ObjectEdits.SetPosition(copy, x, o.Y);
            ObjectEdits.Add(root, copy);
            return (true, $"duplicated {o.Id} as {newId}");
        }, $"duplicate {o.Id}");
        if (ok && newId != null) { SelectedObjectId = null; SelectObject(newId); }
        return ok;
    }

    /// <summary>Arrow keys: one step in the camera's screen directions snapped to the map axes (right, forward as -1, 0, 1).</summary>
    public bool NudgeScreen(int right, int forward)
    {
        var cam = Host.FlyCam;
        var f = -cam.GlobalBasis.Z; var r = cam.GlobalBasis.X;
        if (Mathf.Abs(f.Y) > 0.98f) f = cam.GlobalBasis.Y;
        // map axes: east = +X, north = -Z
        var dir = new Vector2(r.X * right + f.X * forward, -(r.Z * right + f.Z * forward));
        if (dir.LengthSquared() < 1e-6f) return false;
        var step = Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y) ? new Vector2(Mathf.Sign(dir.X), 0) : new Vector2(0, Mathf.Sign(dir.Y));
        return NudgeObject(step.X * NudgeStep, step.Y * NudgeStep);
    }

    // ---- mouse flow ----

    /// <summary>Left press with nothing armed: rotation ring, then an object, then the parcel under the cursor.</summary>
    public void LeftPress(Vector2 screen, bool shift)
    {
        var o = Host.FlyCam.ProjectRayOrigin(screen); var d = Host.FlyCam.ProjectRayNormal(screen);
        var hasGround = Host.Picker.RayToGround(o, d, out var gp);
        if (SelectedObjectId != null && hasGround && Gizmo.OnRing(gp) && StartRotate(SelectedObjectId)) return;
        if (PickObject(o, d) is { } id)
        {
            if (!hasGround && Host.Map.ObjectNodes.TryGetValue(id, out var node)) gp = node.GlobalPosition;
            StartMove(id, gp);
            return;
        }
        SelectObject(null);
        var hit = Host.Picker.PickScreen(screen, out _)?.Id;
        if (Host.Tool == EditorTool.Group) { if (hit != null) Host.Click(hit, true); return; } // marking: every click toggles a parcel
        Host.Click(hit, shift);
    }
}

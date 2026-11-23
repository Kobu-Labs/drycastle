using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Poles tool: utility poles and the cables between them. Poles are ordinary placed objects (the `prim:pole_*`
/// assets), so placing, selecting, moving, turning and deleting them is the Objects code; this page adds a palette of
/// the poles alone and the cables. A cable is an entry of `cables` in edits.json (`{"from": pole id, "to": pole id}`,
/// see EditsFile.Connect); the game strings its wires between the poles as they stand, so nothing is regenerated.
///
/// Connect mode (C): the first click picks the pole to start from (the selected pole when there is one), every further
/// click on another pole strings a cable from the last one and carries on from there, so a street is a row of clicks.
/// A click on a house (a wall or its roof) fixes a house connector there and strings the cable to it; the start stays
/// on the pole, so one pole can feed several houses. The connector is a placed object too (`prim:connector_*`, with a
/// `z`): a wall kind or a roof kind after the surface clicked, which one of them picked from the spot.
/// Esc or a right click stops. A click on a cable selects it (Del removes it). Poles are never turned for the user:
/// the wires are matched left to right across each span, whichever way a pole faces.
/// Plan mode (P) is <see cref="PolePlan"/>, the sidebar page <see cref="PolesPanel"/>.
/// Like the rest of the editor, every control and key calls one of the public methods below.
/// </summary>
public sealed class PolesTool : EditorPart
{
    public sealed record CableInfo(string Id, string From, string To, double LengthM);

    const float CablePickPixels = 9f;

    public PolePlan Plan { get; }
    readonly PolesPanel _panel;

    /// <summary>Connect mode is on: clicks on poles string cables.</summary>
    public bool Connecting { get; private set; }
    /// <summary>The pole the next cable starts from; null until the first pole is clicked.</summary>
    public string? ConnectFrom { get; private set; }
    public string? SelectedCableId { get; private set; }

    public PolesTool(IEditorHost host) : base(host)
    {
        Plan = new PolePlan(host, this);
        _panel = new PolesPanel(host, this);
    }

    // ---- the parts' hooks ----

    /// <summary>Leaving the editor ends connect and plan mode.</summary>
    public override void ActiveChanged(bool on)
    {
        if (on) return;
        StopConnecting(false);
        Plan.Stop(false);
    }

    public void BuildPage(VBoxContainer page)
    {
        _panel.Build(page);
        BuildWorld();
        Plan.BuildWorld();
    }

    public void RefreshPanel() => _panel.Refresh();

    /// <summary>What the page refresh drops: a cable the file no longer has stays no selection, a missing pole is no start for a cable.</summary>
    internal void DropMissing()
    {
        if (SelectedCableId != null && AllCables().All(c => c.Id != SelectedCableId)) SelectedCableId = null;
        if (ConnectFrom != null && Host.Objects.ObjectData(ConnectFrom) == null) ConnectFrom = null;
    }

    // ---- data ----

    ObjectsTool Objects => Host.Objects;

    internal bool IsPole(string objectId) => Objects.ObjectData(objectId) is { } o && EditsFile.IsPoleAsset(o.Asset);
    /// <summary>A house connector: where a cable ends on a wall or a roof.</summary>
    internal bool IsConnector(string objectId) => Objects.ObjectData(objectId) is { } o && EditsFile.IsConnectorAsset(o.Asset);

    internal IReadOnlyList<ObjectsTool.ObjectInfo> AllPoles()
        => Objects.AllObjects().Where(o => o.Asset.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal)).ToList();

    /// <summary>Cables of the file with their plan length; a cable with a missing pole has length -1.</summary>
    internal IReadOnlyList<CableInfo> AllCables()
    {
        var pos = Objects.AllObjects().ToDictionary(o => o.Id, o => (o.X, o.Y));
        return EditsFile.Cables(Host.Edits.Disk()).Select(c =>
        {
            var len = pos.TryGetValue(c.from, out var a) && pos.TryGetValue(c.to, out var b) ? Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) : -1;
            return new CableInfo(c.id, c.from, c.to, len);
        }).ToList();
    }

    // ---- connect mode ----

    /// <summary>C or the button: start stringing cables, from the selected pole when one is selected.</summary>
    public void StartConnecting()
    {
        if (AllPoles().Count == 0) { Host.Say("place a pole first: cables run from pole to pole, or from a pole to a house", true); return; }
        Objects.DisarmPlacement(false);
        Plan.Stop(false);
        if (Host.GateArmed) Host.CancelGate();
        SelectCable(null);
        Connecting = true;
        ConnectFrom = Objects.SelectedObjectId != null && IsPole(Objects.SelectedObjectId) ? Objects.SelectedObjectId : null;
        Host.Say(ConnectFrom != null ? $"connecting from {ConnectFrom}: click the next pole, or a house (Esc stops)" : "connecting: click the pole to start from (Esc stops)");
        Host.MarkPanelDirty();
    }

    public void StopConnecting(bool say = true)
    {
        if (!Connecting) return;
        Connecting = false;
        ConnectFrom = null;
        if (say) Host.Say("connecting off");
        Host.MarkPanelDirty();
    }

    internal void ToggleConnecting() { if (Connecting) StopConnecting(); else StartConnecting(); }

    /// <summary>
    /// A click in connect mode on an object (null: on nothing). The first pole becomes the start; another pole gets a
    /// cable from the start and becomes the new start; a house connector gets a cable too, and the start stays on the
    /// pole. Returns true when a cable was strung.
    /// </summary>
    bool ConnectClick(string? objectId)
    {
        if (!Connecting) return false;
        if (objectId == null) { Host.Say("click a pole or a house (Esc stops connecting)", true); return false; }
        var connector = IsConnector(objectId);
        if (!connector && !IsPole(objectId)) { Host.Say($"{objectId} is not a utility pole or a house connector", true); return false; }
        if (ConnectFrom == null || Objects.ObjectData(ConnectFrom) == null)
        {
            if (connector) { Host.Say("start from a pole, then click the house connector", true); return false; }
            ConnectFrom = objectId;
            Objects.SelectObject(objectId);
            Host.Say($"from {objectId}: click the next pole");
            Host.MarkPanelDirty();
            return false;
        }
        if (objectId == ConnectFrom) { Host.Say("click another pole: a cable joins two different poles", true); return false; }
        var from = ConnectFrom;
        var joined = AllCables().Any(c => (c.From == from && c.To == objectId) || (c.From == objectId && c.To == from));
        var ok = !joined && ConnectPoles(from, objectId);
        if (joined) Host.Say(connector ? $"{from} and {objectId} are already joined" : $"{from} and {objectId} are already joined; carrying on from {objectId}");
        // the line carries on from the pole just clicked, also along a stretch that is strung already;
        // after a house connector it stays on the pole, which may feed more houses
        if ((ok || joined) && !connector) { ConnectFrom = objectId; Objects.SelectObject(objectId); }
        Host.MarkPanelDirty();
        return ok;
    }

    // ---- houses ----

    /// <summary>"wall" or "roof" for a surface a connector can sit on, by its normal; null for the underside of something.</summary>
    internal static string? MountOf(Vector3 normal) => normal.Y >= 0.5f ? "roof" : normal.Y > -0.5f ? "wall" : null;

    /// <summary>The connector assets made for a wall or a roof, by id.</summary>
    IReadOnlyList<string> ConnectorAssets(string mount)
        => Host.Map.AssetCatalog.Select(a => a.Id).Where(id => id.StartsWith($"{EditsFile.ConnectorAssetPrefix}{mount}_", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>One of the connectors for that surface, picked from the spot (the same spot gives the same one, the next house another).</summary>
    internal string? PickConnector(string mount, Vector3 at)
    {
        var all = ConnectorAssets(mount);
        if (all.Count == 0) return null;
        // mixed by hand (System.HashCode is seeded per process): the choice is the same every run
        var h = (uint)(Mathf.RoundToInt(at.X * 4f) * 73856093) ^ (uint)(Mathf.RoundToInt(at.Y * 4f) * 19349663) ^ (uint)(Mathf.RoundToInt(at.Z * 4f) * 83492791);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return all[(int)(h % (uint)all.Count)];
    }

    /// <summary>The building surface under a ray, unless the ground is in front of it.</summary>
    internal BuildingHit? BuildingUnder(Vector3 origin, Vector3 dir)
    {
        if (Host.Map.RayBuildings(origin, dir) is not { } hit) return null;
        if (Host.RayToGround(origin, dir, out var ground) && origin.DistanceTo(ground) < hit.Distance - 0.05f) return null;
        return hit;
    }

    /// <summary>
    /// A click in connect mode on a house: fix a connector at that point of the wall or roof and string the cable from
    /// the start pole to it, in one save (one undo step). A wall connector faces out of the wall, a roof mast is turned
    /// towards the pole. <paramref name="asset"/> overrides the connector that would be picked.
    /// </summary>
    bool ConnectHouse(BuildingHit hit, string? asset = null)
    {
        if (!Connecting) return false;
        if (ConnectFrom == null || !IsPole(ConnectFrom)) { Host.Say("click the pole to start from first, then the house", true); return false; }
        if (Host.Map.OriginEpsg5514 == null) { Host.Say("this map has no origin_epsg5514", true); return false; }
        if (MountOf(hit.Normal) is not { } mount) { Host.Say("click a wall or the roof, not the underside of something", true); return false; }
        asset ??= PickConnector(mount, hit.Point);
        if (asset == null) { Host.Say($"no {mount} connectors in the asset manifest (make poles-manifest)", true); return false; }
        var (ax, ay) = Host.Map.RawAbs(hit.Point);
        double x = Math.Round(ax, 3), y = Math.Round(ay, 3), z = Math.Round(hit.Point.Y, 3);
        var parcel = Host.Map.ParcelAtAbs(x, y)?.Id ?? hit.ParcelId;
        var from = ConnectFrom;
        double rot;
        if (mount == "wall") rot = Math.Atan2(-hit.Normal.Z, hit.Normal.X) * 180.0 / Math.PI;
        else
        {
            var pole = Host.Map.ObjectNodes.TryGetValue(from, out var pn) ? pn.GlobalPosition : hit.Point + Vector3.Right;
            rot = Math.Atan2(-(pole.Z - hit.Point.Z), pole.X - hit.Point.X) * 180.0 / Math.PI;
        }
        rot = Math.Round(MapFrame.Mod360(rot), 1);
        var name = Objects.AssetName(asset);
        string? newId = null;
        var ok = Host.Edits.Mutate(root =>
        {
            var id = ObjectEdits.NextId(root);
            var o = ObjectEdits.Make(id, asset, x, y, rot);
            ObjectEdits.SetZ(o, z);
            ObjectEdits.Add(root, o);
            var (strung, msg, cable) = EditsFile.Connect(root, from, id);
            if (!strung) return (false, msg);
            newId = id;
            return (true, $"{cable}: {from} to the house on {parcel}, {id} on the {mount}");
        }, $"connect {from} to a house");
        if (ok && newId != null) Host.Say($"connected {from} to the house on {parcel} ({name}); click the next house or pole, Esc stops");
        Host.MarkPanelDirty();
        return ok;
    }

    /// <summary>Swap the selected house connector for the next kind made for the same surface (wall or roof).</summary>
    internal bool CycleConnector()
    {
        if (Objects.SelectedObject is not { } o || !EditsFile.IsConnectorAsset(o.Asset)) { Host.Say("select a house connector first", true); return false; }
        var mount = o.Asset.StartsWith(EditsFile.ConnectorAssetPrefix + "roof_", StringComparison.Ordinal) ? "roof" : "wall";
        var all = ConnectorAssets(mount);
        if (all.Count < 2) { Host.Say($"there is no other {mount} connector", true); return false; }
        var next = all[(all.ToList().IndexOf(o.Asset) + 1) % all.Count]; // an unknown asset (index -1) starts at the first
        return Host.Edits.Mutate(root =>
        {
            if (ObjectEdits.Find(root, o.Id) is not { } f) return (false, $"object {o.Id} no longer exists");
            f.obj["asset"] = next;
            return (true, $"{o.Id} is now {Objects.AssetName(next)}");
        }, $"change the connector {o.Id}");
    }

    // ---- cables ----

    /// <summary>String a cable between two placed poles (one save, one undo step).</summary>
    bool ConnectPoles(string from, string to)
        => Host.Edits.Mutate(root =>
        {
            var (ok, msg, _) = EditsFile.Connect(root, from, to);
            return (ok, msg);
        }, $"connect {from} and {to}");

    internal bool DisconnectCable(string id)
    {
        var ok = Host.Edits.Mutate(root => EditsFile.Disconnect(root, id), $"remove cable {id}");
        if (ok && SelectedCableId == id) SelectCable(null);
        return ok;
    }

    public bool SelectCable(string? id)
    {
        if (id != null && AllCables().All(c => c.Id != id)) return false;
        if (id == SelectedCableId) return true;
        SelectedCableId = id;
        if (id != null) Objects.SelectObject(null);
        Host.MarkPanelDirty();
        return true;
    }

    internal bool DeleteSelectedCable()
    {
        if (SelectedCableId == null) { Host.Say("no cable selected", true); return false; }
        return DisconnectCable(SelectedCableId);
    }

    /// <summary>The drawn cable nearest to a screen point, within a few pixels of one of its wires; null when none is.</summary>
    string? PickCable(Vector2 screen)
    {
        string? best = null;
        var bestD = CablePickPixels;
        foreach (var id in Host.Map.CableNodes.Keys)
            foreach (var wire in Host.Map.CableWiresOf(id))
            {
                Vector2? prev = null;
                foreach (var p in wire.Points)
                {
                    var world = new Vector3(p.X, p.Y, p.Z);
                    Vector2? now = Host.FlyCam.IsPositionBehind(world) ? null : Host.FlyCam.UnprojectPosition(world);
                    if (prev is { } a && now is { } b)
                    {
                        var d = DistanceToSegment(screen, a, b);
                        if (d < bestD) { bestD = d; best = id; }
                    }
                    prev = now;
                }
            }
        return best;
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    // ---- input ----

    /// <summary>Left press with the Poles tool and nothing armed: connect mode takes the click, else an object, then a cable, then the usual.</summary>
    public void LeftPress(Vector2 screen, bool shift)
    {
        if (Plan.Planning) { Plan.LeftPress(screen); return; }
        var o = Host.FlyCam.ProjectRayOrigin(screen); var d = Host.FlyCam.ProjectRayNormal(screen);
        if (Connecting)
        {
            // an object under the cursor first (a pole, a connector), then a house, then nothing
            if (Objects.PickObject(o, d) is { } under) ConnectClick(under);
            else if (ConnectFrom != null && BuildingUnder(o, d) is { } hit) ConnectHouse(hit);
            else ConnectClick(null);
            return;
        }
        var onRing = Objects.SelectedObjectId != null && Host.RayToGround(o, d, out var gp) && Objects.Gizmo.OnRing(gp);
        if (!onRing && Objects.PickObject(o, d) == null && PickCable(screen) is { } cable) { SelectCable(cable); return; }
        SelectCable(null);
        Objects.LeftPress(screen, shift);
    }

    /// <summary>Keys of the Poles tool; true when the key was used.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (Host.Tool != EditorTool.Poles || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.P: Plan.Toggle(); return true;
            case Key.Enter or Key.KpEnter when Plan.Planning: Plan.Build(); return true;
            case Key.Backspace when Plan.Planning: Plan.UndoLast(); return true;
            case Key.C: ToggleConnecting(); return true;
            case Key.Delete when SelectedCableId != null: DeleteSelectedCable(); return true;
        }
        return false;
    }

    public string HintText()
        => Plan.Planning ? $"planning ({Plan.PoleCount} poles, {Plan.HouseCount} houses): LMB a pole point, RMB a house wall or roof, click a point again to remove it, Backspace removes the last, Enter builds, Esc stops"
            : Connecting
            ? (ConnectFrom == null ? "connecting: LMB on the pole to start from, Esc or RMB stops"
                : $"connecting from {ConnectFrom}: LMB on the next pole strings a cable and carries on from it, LMB on a house wall or roof connects the house, Esc or RMB stops, Ctrl+Z undo")
            : SelectedCableId != null ? $"{SelectedCableId}: Del removes the cable, Esc deselects, C connects poles"
            : Objects.SelectedObjectId != null && IsConnector(Objects.SelectedObjectId) ? "house connector: drag moves, the ring or R / Shift+R turns, Del deletes it and its cable, Esc deselects"
            : Objects.SelectedObjectId != null ? "pole: drag moves, drag the ring or R / Shift+R turns, C strings a cable from it, Del deletes it and its cables, Esc deselects"
            : "poles: P plans a line, C connects by hand, choose a pole to place it, LMB selects a pole or a cable, Esc leaves the tool";

    // ---- per frame: the preview line, the cable highlight, cables following a dragged pole ----

    MeshInstance3D _line = null!;
    StandardMaterial3D _lineIdle = null!, _lineOk = null!, _cableHi = null!;
    (Vector3, Vector3)? _lineEnds;
    MeshInstance3D? _hiCable;
    Material? _hiCablePrev;
    string? _cableDragId;

    void BuildWorld()
    {
        _lineIdle = Gfx.Unshaded(new Color(1f, 1f, 1f, 0.8f), true);
        _lineOk = Gfx.Unshaded(new Color(0.1f, 0.95f, 1f), true);
        _cableHi = Gfx.Unshaded(new Color(1f, 0.92f, 0.1f), false);
        _line = new MeshInstance3D { Name = "ConnectLine", Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Host.MarkerRoot.AddChild(_line);
    }

    /// <summary>Where the wires leave a pole or a house connector: the middle of its line seats, else of all its seats (8 m above its foot when it has none).</summary>
    Vector3 PoleTop(string id)
    {
        var all = Host.Map.SeatsOf(id);
        var seats = all.Where(s => s.Kind == "line").ToList();
        if (seats.Count == 0) seats = all;
        if (seats.Count > 0)
        {
            var c = System.Numerics.Vector3.Zero;
            foreach (var s in seats) c += s.Pos;
            c /= seats.Count;
            return new Vector3(c.X, c.Y, c.Z);
        }
        return Host.Map.ObjectNodes.TryGetValue(id, out var n) ? n.GlobalPosition + Vector3.Up * 8f : Vector3.Zero;
    }

    public override void Update(double delta)
    {
        Plan.UpdateWorld();
        FollowDraggedPole();
        HighlightSelectedCable();
        UpdateConnectLine();
    }

    /// <summary>Cables follow a pole while it is dragged, and go back when the drag is cancelled or refused.</summary>
    void FollowDraggedPole()
    {
        var dragged = Objects.DraggedId;
        if (Objects.Dragging && dragged != null && Host.Map.Cables.Any(c => c.From == dragged || c.To == dragged))
        {
            _cableDragId = dragged;
            Host.Map.RebuildCables(dragged);
        }
        else if (!Objects.Dragging && _cableDragId != null)
        {
            Host.Map.RebuildCables(_cableDragId);
            _cableDragId = null;
        }
    }

    /// <summary>The selected cable is drawn yellow; the node is replaced whenever the cables are drawn again.</summary>
    void HighlightSelectedCable()
    {
        var want = Host.Tool == EditorTool.Poles && SelectedCableId != null && Host.Map.CableNodes.TryGetValue(SelectedCableId, out var cn) ? cn : null;
        if (want == _hiCable) return;
        if (_hiCable != null && GodotObject.IsInstanceValid(_hiCable)) _hiCable.MaterialOverride = _hiCablePrev;
        _hiCable = want;
        if (want != null) { _hiCablePrev = want.MaterialOverride; want.MaterialOverride = _cableHi; }
    }

    /// <summary>Connect mode with a start chosen: a line from the start to what a click would string a cable to, and a hover text on it.</summary>
    void UpdateConnectLine()
    {
        var show = false;
        if (Connecting && Host.Tool == EditorTool.Poles && ConnectFrom != null && Host.Map.ObjectNodes.ContainsKey(ConnectFrom))
        {
            var mouse = Host.GetViewport().GetMousePosition();
            if (!Host.Looking && !Host.OverUi(mouse) && !Host.Modal)
            {
                var o = Host.FlyCam.ProjectRayOrigin(mouse); var d = Host.FlyCam.ProjectRayNormal(mouse);
                var under = Objects.PickObject(o, d);
                var valid = under != null && under != ConnectFrom && (IsPole(under) || IsConnector(under));
                var house = under == null ? BuildingUnder(o, d) : null;
                var mount = house is { } hh ? MountOf(hh.Normal) : null;
                if (mount != null) valid = true;
                Vector3? end = under != null && valid ? PoleTop(under) : mount != null ? house!.Value.Point
                    : Host.RayToGround(o, d, out var g) ? g + Vector3.Up * 0.3f : null;
                if (end is { } e)
                {
                    show = true;
                    var ends = (PoleTop(ConnectFrom), e);
                    if (ends != _lineEnds)
                    {
                        _lineEnds = ends;
                        _line.Mesh = Gfx.Strip(new List<Vector3> { ends.Item1, ends.Item2 }, new List<int> { 0, 1 }, Mesh.PrimitiveType.Lines);
                    }
                    _line.MaterialOverride = valid ? _lineOk : _lineIdle;
                    if (under != null || mount != null)
                        Host.ShowHoverText(mount != null ? $"house on {house!.Value.ParcelId}: click to fix a {mount} connector here and string a cable from {ConnectFrom}"
                            : valid ? $"{under}: click to string a cable from {ConnectFrom}"
                            : under == ConnectFrom ? $"{under}: the cable starts here" : $"{under} is not a pole or a house connector", mouse);
                }
            }
        }
        _line.Visible = show;
    }
}

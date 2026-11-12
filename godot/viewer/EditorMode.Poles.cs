using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Suchohrad.MapLoader;

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
/// Like the rest of the editor, every control and key calls one of the public methods below.
/// </summary>
public partial class EditorMode
{
    public sealed record CableInfo(string Id, string From, string To, double LengthM);

    /// <summary>Connect mode is on: clicks on poles string cables.</summary>
    public bool Connecting { get; private set; }
    /// <summary>The pole the next cable starts from; null until the first pole is clicked.</summary>
    public string? ConnectFrom { get; private set; }
    public string? SelectedCableId { get; private set; }
    const float CablePickPixels = 9f;

    // ---- data ----

    public bool IsPole(string objectId) => ObjectData(objectId) is { } o && EditsFile.IsPoleAsset(o.Asset);
    /// <summary>A house connector: where a cable ends on a wall or a roof.</summary>
    public bool IsConnector(string objectId) => ObjectData(objectId) is { } o && EditsFile.IsConnectorAsset(o.Asset);

    public IReadOnlyList<ObjectInfo> AllPoles()
        => AllObjects().Where(o => o.Asset.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal)).ToList();

    /// <summary>Cables of the file with their plan length; a cable with a missing pole has length -1.</summary>
    public IReadOnlyList<CableInfo> AllCables()
    {
        var pos = AllObjects().ToDictionary(o => o.Id, o => (o.X, o.Y));
        return EditsFile.Cables(Disk()).Select(c =>
        {
            var len = pos.TryGetValue(c.from, out var a) && pos.TryGetValue(c.to, out var b) ? Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) : -1;
            return new CableInfo(c.id, c.from, c.to, len);
        }).ToList();
    }

    public IReadOnlyList<CableInfo> CablesOf(string poleId) => AllCables().Where(c => c.From == poleId || c.To == poleId).ToList();

    // ---- connect mode ----

    /// <summary>C or the button: start stringing cables, from the selected pole when one is selected.</summary>
    public void StartConnecting()
    {
        if (AllPoles().Count == 0) { Say("place a pole first: cables run from pole to pole, or from a pole to a house", true); return; }
        DisarmPlacement(false);
        StopPlanning(false);
        if (GateArmed) CancelGate();
        SelectCable(null);
        Connecting = true;
        ConnectFrom = SelectedObjectId != null && IsPole(SelectedObjectId) ? SelectedObjectId : null;
        Say(ConnectFrom != null ? $"connecting from {ConnectFrom}: click the next pole, or a house (Esc stops)" : "connecting: click the pole to start from (Esc stops)");
        _panelDirty = true;
    }

    public void StopConnecting(bool say = true)
    {
        if (!Connecting) return;
        Connecting = false;
        ConnectFrom = null;
        if (say) Say("connecting off");
        _panelDirty = true;
    }

    public void ToggleConnecting() { if (Connecting) StopConnecting(); else StartConnecting(); }

    /// <summary>
    /// A click in connect mode on an object (null: on nothing). The first pole becomes the start; another pole gets a
    /// cable from the start and becomes the new start; a house connector gets a cable too, and the start stays on the
    /// pole. Returns true when a cable was strung.
    /// </summary>
    public bool ConnectClick(string? objectId)
    {
        if (!Connecting) return false;
        if (objectId == null) { Say("click a pole or a house (Esc stops connecting)", true); return false; }
        var connector = IsConnector(objectId);
        if (!connector && !IsPole(objectId)) { Say($"{objectId} is not a utility pole or a house connector", true); return false; }
        if (ConnectFrom == null || ObjectData(ConnectFrom) == null)
        {
            if (connector) { Say("start from a pole, then click the house connector", true); return false; }
            ConnectFrom = objectId;
            SelectObject(objectId);
            Say($"from {objectId}: click the next pole");
            _panelDirty = true;
            return false;
        }
        if (objectId == ConnectFrom) { Say("click another pole: a cable joins two different poles", true); return false; }
        var from = ConnectFrom;
        var joined = AllCables().Any(c => (c.From == from && c.To == objectId) || (c.From == objectId && c.To == from));
        var ok = !joined && ConnectPoles(from, objectId);
        if (joined) Say(connector ? $"{from} and {objectId} are already joined" : $"{from} and {objectId} are already joined; carrying on from {objectId}");
        // the line carries on from the pole just clicked, also along a stretch that is strung already;
        // after a house connector it stays on the pole, which may feed more houses
        if ((ok || joined) && !connector) { ConnectFrom = objectId; SelectObject(objectId); }
        _panelDirty = true;
        return ok;
    }

    // ---- houses ----

    /// <summary>"wall" or "roof" for a surface a connector can sit on, by its normal; null for the underside of something.</summary>
    public static string? MountOf(Vector3 normal) => normal.Y >= 0.5f ? "roof" : normal.Y > -0.5f ? "wall" : null;

    /// <summary>The connector assets made for a wall or a roof, by id.</summary>
    public IReadOnlyList<string> ConnectorAssets(string mount)
        => Map.AssetCatalog.Select(a => a.Id).Where(id => id.StartsWith($"{EditsFile.ConnectorAssetPrefix}{mount}_", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>One of the connectors for that surface, picked from the spot (the same spot gives the same one, the next house another).</summary>
    public string? PickConnector(string mount, Vector3 at)
    {
        var all = ConnectorAssets(mount);
        if (all.Count == 0) return null;
        // mixed by hand (System.HashCode is seeded per process): the choice is the same every run
        var h = (uint)(Mathf.RoundToInt(at.X * 4f) * 73856093) ^ (uint)(Mathf.RoundToInt(at.Y * 4f) * 19349663) ^ (uint)(Mathf.RoundToInt(at.Z * 4f) * 83492791);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return all[(int)(h % (uint)all.Count)];
    }

    /// <summary>The building surface under a ray, unless the ground is in front of it.</summary>
    public BuildingHit? BuildingUnder(Vector3 origin, Vector3 dir)
    {
        if (Map.RayBuildings(origin, dir) is not { } hit) return null;
        if (RayToGround(origin, dir, out var ground) && origin.DistanceTo(ground) < hit.Distance - 0.05f) return null;
        return hit;
    }

    /// <summary>
    /// A click in connect mode on a house: fix a connector at that point of the wall or roof and string the cable from
    /// the start pole to it, in one save (one undo step). A wall connector faces out of the wall, a roof mast is turned
    /// towards the pole. <paramref name="asset"/> overrides the connector that would be picked.
    /// </summary>
    public bool ConnectHouse(BuildingHit hit, string? asset = null)
    {
        if (!Connecting) return false;
        if (ConnectFrom == null || !IsPole(ConnectFrom)) { Say("click the pole to start from first, then the house", true); return false; }
        if (Map.OriginEpsg5514 == null) { Say("this map has no origin_epsg5514", true); return false; }
        if (MountOf(hit.Normal) is not { } mount) { Say("click a wall or the roof, not the underside of something", true); return false; }
        asset ??= PickConnector(mount, hit.Point);
        if (asset == null) { Say($"no {mount} connectors in the asset manifest (make poles-manifest)", true); return false; }
        var (ax, ay) = RawAbs(hit.Point);
        double x = Math.Round(ax, 3), y = Math.Round(ay, 3), z = Math.Round(hit.Point.Y, 3);
        var parcel = ParcelAtAbs(x, y)?.Id ?? hit.ParcelId;
        var from = ConnectFrom;
        double rot;
        if (mount == "wall") rot = Math.Atan2(-hit.Normal.Z, hit.Normal.X) * 180.0 / Math.PI;
        else
        {
            var pole = Map.ObjectNodes.TryGetValue(from, out var pn) ? pn.GlobalPosition : hit.Point + Vector3.Right;
            rot = Math.Atan2(-(pole.Z - hit.Point.Z), pole.X - hit.Point.X) * 180.0 / Math.PI;
        }
        rot = Math.Round(Mod360(rot), 1);
        var name = AssetName(asset);
        string? newId = null;
        var ok = Mutate(root =>
        {
            var id = ObjectEdits.NextId(root);
            var o = ObjectEdits.Make(id, asset, x, y, rot);
            ObjectEdits.SetZ(o, z);
            ObjectEdits.Add(root, parcel, o);
            var (strung, msg, cable) = EditsFile.Connect(root, from, id);
            if (!strung) return (false, msg);
            newId = id;
            return (true, $"{cable}: {from} to the house on {parcel}, {id} on the {mount}");
        }, $"connect {from} to a house");
        if (ok && newId != null) Say($"connected {from} to the house on {parcel} ({name}); click the next house or pole, Esc stops");
        _panelDirty = true;
        return ok;
    }

    /// <summary>Swap the selected house connector for the next kind made for the same surface (wall or roof).</summary>
    public bool CycleConnector()
    {
        if (SelectedObject is not { } o || !EditsFile.IsConnectorAsset(o.Asset)) { Say("select a house connector first", true); return false; }
        var mount = o.Asset.StartsWith(EditsFile.ConnectorAssetPrefix + "roof_", StringComparison.Ordinal) ? "roof" : "wall";
        var all = ConnectorAssets(mount);
        if (all.Count < 2) { Say($"there is no other {mount} connector", true); return false; }
        var next = all[(all.ToList().IndexOf(o.Asset) + 1) % all.Count]; // an unknown asset (index -1) starts at the first
        return Mutate(root =>
        {
            if (ObjectEdits.Find(root, o.Id) is not { } f) return (false, $"object {o.Id} no longer exists");
            f.obj["asset"] = next;
            return (true, $"{o.Id} is now {AssetName(next)}");
        }, $"change the connector {o.Id}");
    }

    /// <summary>String a cable between two placed poles (one save, one undo step).</summary>
    public bool ConnectPoles(string from, string to)
        => Mutate(root =>
        {
            var (ok, msg, _) = EditsFile.Connect(root, from, to);
            return (ok, msg);
        }, $"connect {from} and {to}");

    public bool DisconnectCable(string id)
    {
        var ok = Mutate(root => EditsFile.Disconnect(root, id), $"remove cable {id}");
        if (ok && SelectedCableId == id) SelectCable(null);
        return ok;
    }

    public bool SelectCable(string? id)
    {
        if (id != null && AllCables().All(c => c.Id != id)) return false;
        if (id == SelectedCableId) return true;
        SelectedCableId = id;
        if (id != null) SelectObject(null);
        _panelDirty = true;
        return true;
    }

    public bool DeleteSelectedCable()
    {
        if (SelectedCableId == null) { Say("no cable selected", true); return false; }
        return DisconnectCable(SelectedCableId);
    }

    /// <summary>The drawn cable nearest to a screen point, within a few pixels of one of its wires; null when none is.</summary>
    public string? PickCable(Vector2 screen)
    {
        string? best = null;
        var bestD = CablePickPixels;
        foreach (var id in Map.CableNodes.Keys)
            foreach (var wire in Map.CableWiresOf(id))
            {
                Vector2? prev = null;
                foreach (var p in wire.Points)
                {
                    var world = new Vector3(p.X, p.Y, p.Z);
                    Vector2? now = FlyCam.IsPositionBehind(world) ? null : FlyCam.UnprojectPosition(world);
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
    void PolesLeftPress(Vector2 screen, bool shift)
    {
        if (Planning) { PlanLeftPress(screen); return; }
        var o = FlyCam.ProjectRayOrigin(screen); var d = FlyCam.ProjectRayNormal(screen);
        if (Connecting)
        {
            // an object under the cursor first (a pole, a connector), then a house, then nothing
            if (PickObject(o, d) is { } under) ConnectClick(under);
            else if (ConnectFrom != null && BuildingUnder(o, d) is { } hit) ConnectHouse(hit);
            else ConnectClick(null);
            return;
        }
        var onRing = SelectedObjectId != null && RayToGround(o, d, out var gp) && OnRotationRing(gp);
        if (!onRing && PickObject(o, d) == null && PickCable(screen) is { } cable) { SelectCable(cable); return; }
        SelectCable(null);
        LeftPress(screen, shift);
    }

    /// <summary>Keys of the Poles tool; true when the key was used.</summary>
    bool HandlePolesKey(InputEventKey k)
    {
        if (Tool != EditorTool.Poles || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.P: TogglePlanning(); return true;
            case Key.Enter or Key.KpEnter when Planning: BuildPlan(); return true;
            case Key.Backspace when Planning: PlanUndoLast(); return true;
            case Key.C: ToggleConnecting(); return true;
            case Key.Delete when SelectedCableId != null: DeleteSelectedCable(); return true;
        }
        return false;
    }

    // ---- per frame: the preview line, the cable highlight, cables following a dragged pole ----

    MeshInstance3D _poleLine = null!;
    StandardMaterial3D _poleLineIdle = null!, _poleLineOk = null!, _cableHi = null!;
    (Vector3, Vector3)? _poleLineEnds;
    MeshInstance3D? _hiCable;
    Material? _hiCablePrev;
    string? _cableDragId;

    void BuildPolesWorld()
    {
        _poleLineIdle = Unshaded(new Color(1f, 1f, 1f, 0.8f), true);
        _poleLineOk = Unshaded(new Color(0.1f, 0.95f, 1f), true);
        _cableHi = Unshaded(new Color(1f, 0.92f, 0.1f), false);
        _poleLine = new MeshInstance3D { Name = "ConnectLine", Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        MarkerRoot.AddChild(_poleLine);
    }

    /// <summary>Where the wires leave a pole or a house connector: the middle of its line seats, else of all its seats (8 m above its foot when it has none).</summary>
    Vector3 PoleTop(string id)
    {
        var all = Map.SeatsOf(id);
        var seats = all.Where(s => s.Kind == "line").ToList();
        if (seats.Count == 0) seats = all;
        if (seats.Count > 0)
        {
            var c = System.Numerics.Vector3.Zero;
            foreach (var s in seats) c += s.Pos;
            c /= seats.Count;
            return new Vector3(c.X, c.Y, c.Z);
        }
        return Map.ObjectNodes.TryGetValue(id, out var n) ? n.GlobalPosition + Vector3.Up * 8f : Vector3.Zero;
    }

    /// <summary>The pole or connector under the mouse that a click would string a cable to (connect mode, a start chosen); null otherwise.</summary>
    public string? ConnectTarget { get; private set; }
    /// <summary>The spot on a house under the mouse where a click would fix a connector; null otherwise.</summary>
    public BuildingHit? ConnectHouseTarget { get; private set; }

    void UpdatePolesTool()
    {
        UpdatePlanWorld();
        // cables follow a pole while it is dragged, and go back when the drag is cancelled or refused
        if (Dragging && _dragId != null && Map.Cables.Any(c => c.From == _dragId || c.To == _dragId))
        {
            _cableDragId = _dragId;
            Map.RebuildCables(_dragId);
        }
        else if (!Dragging && _cableDragId != null)
        {
            Map.RebuildCables(_cableDragId);
            _cableDragId = null;
        }

        // the selected cable is drawn yellow; the node is replaced whenever the cables are drawn again
        var want = Tool == EditorTool.Poles && SelectedCableId != null && Map.CableNodes.TryGetValue(SelectedCableId, out var cn) ? cn : null;
        if (want != _hiCable)
        {
            if (_hiCable != null && GodotObject.IsInstanceValid(_hiCable)) _hiCable.MaterialOverride = _hiCablePrev;
            _hiCable = want;
            if (want != null) { _hiCablePrev = want.MaterialOverride; want.MaterialOverride = _cableHi; }
        }

        ConnectTarget = null;
        ConnectHouseTarget = null;
        var show = false;
        if (Connecting && Tool == EditorTool.Poles && ConnectFrom != null && Map.ObjectNodes.ContainsKey(ConnectFrom))
        {
            var mouse = GetViewport().GetMousePosition();
            if (!_looking && !OverUi(mouse) && !Modal)
            {
                var o = FlyCam.ProjectRayOrigin(mouse); var d = FlyCam.ProjectRayNormal(mouse);
                var under = PickObject(o, d);
                var valid = under != null && under != ConnectFrom && (IsPole(under) || IsConnector(under));
                var house = under == null ? BuildingUnder(o, d) : null;
                var mount = house is { } hh ? MountOf(hh.Normal) : null;
                if (mount != null) valid = true;
                Vector3? end = under != null && valid ? PoleTop(under) : mount != null ? house!.Value.Point
                    : RayToGround(o, d, out var g) ? g + Vector3.Up * 0.3f : null;
                if (end is { } e)
                {
                    show = true;
                    ConnectTarget = valid ? under : null;
                    ConnectHouseTarget = mount != null ? house : null;
                    var ends = (PoleTop(ConnectFrom), e);
                    if (ends != _poleLineEnds)
                    {
                        _poleLineEnds = ends;
                        _poleLine.Mesh = Strip(new List<Vector3> { ends.Item1, ends.Item2 }, new List<int> { 0, 1 }, Mesh.PrimitiveType.Lines);
                    }
                    _poleLine.MaterialOverride = valid ? _poleLineOk : _poleLineIdle;
                    if (under != null || mount != null)
                    {
                        _hoverLabel.Text = mount != null ? $"house on {house!.Value.ParcelId}: click to fix a {mount} connector here and string a cable from {ConnectFrom}"
                            : valid ? $"{under}: click to string a cable from {ConnectFrom}"
                            : under == ConnectFrom ? $"{under}: the cable starts here" : $"{under} is not a pole or a house connector";
                        _hoverLabel.Position = mouse + new Vector2(16, 20);
                        _hoverLabel.Visible = true;
                    }
                }
            }
        }
        _poleLine.Visible = show;
    }

    // ---- the page ----

    readonly List<(AssetInfo info, Button btn)> _poleButtons = new();
    Label _plNoOrigin = null!, _plCount = null!, _plSelInfo = null!, _plCableInfo = null!, _plPlanInfo = null!;
    Button _plConnect = null!, _plPlan = null!, _plDeletePole = null!, _plDeleteCable = null!, _plCycle = null!, _plBuild = null!, _plClear = null!;
    VBoxContainer _plSelBox = null!, _plSelCables = null!, _plCableBox = null!, _plPlanBox = null!;
    GridContainer _plGrid = null!;
    string _plRowsKey = "";

    void BuildPolesPage(VBoxContainer page)
    {
        _plNoOrigin = Note("This map has no origin_epsg5514: export it again (map 0.4.0+).", "Danger");
        page.AddChild(_plNoOrigin);

        var modes = new HBoxContainer();
        _plPlan = FocusBtn("Plan  (P)", TogglePlanning, "PrimaryButton");
        _plPlan.TooltipText = "Plan a line: left click where poles go, right click a house wall or roof to connect it, then Build poles places and connects everything along the roads";
        _plConnect = FocusBtn("Connect  (C)", ToggleConnecting);
        _plConnect.TooltipText = "By hand: click a pole, then the next pole or a house; each click strings a cable from the last pole";
        foreach (var b in new[] { _plPlan, _plConnect }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; modes.AddChild(b); }
        page.AddChild(modes);
        _plCount = Note("");
        page.AddChild(_plCount);

        _plPlanBox = new VBoxContainer { Visible = false };
        _plPlanInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _plPlanBox.AddChild(_plPlanInfo);
        _plBuild = FocusBtn("Build poles  (Enter)", () => BuildPlan(), "PrimaryButton");
        _plBuild.TooltipText = "Place a pole at every planned point, string the cables along the roads and connect the houses. One Ctrl+Z undoes all of it.";
        _plClear = FocusBtn("Clear plan", ClearPlan);
        _plPlanBox.AddChild(_plBuild);
        _plPlanBox.AddChild(_plClear);
        page.AddChild(_plPlanBox);

        _plGrid = new GridContainer { Columns = 3 };
        _plGrid.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        _plGrid.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        page.AddChild(_plGrid);
        foreach (var a in Map.AssetCatalog.Where(a => EditsFile.IsPoleAsset(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var info = a;
            var b = new Button
            {
                // the short name shows until the render arrives (and stays if it never does)
                Text = a.Id[EditsFile.PoleAssetPrefix.Length..].Replace('_', ' '), ToggleMode = true, FocusMode = Control.FocusModeEnum.All, ClipText = true, ExpandIcon = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = $"{a.Name}\n{a.Id}\nClick, then click on the ground to place it", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ThemeTypeVariation = "PaletteButton", CustomMinimumSize = new Vector2(0, 128), IconAlignment = HorizontalAlignment.Center,
            };
            b.Pressed += () => { if (PlacementAsset == info.Id) DisarmPlacement(); else ArmPlacement(info.Id); b.ReleaseFocus(); };
            _plGrid.AddChild(b);
            _poleButtons.Add((a, b));
        }
        if (_poleButtons.Count == 0) page.AddChild(Note("No pole assets in the asset manifest. Run make poles-manifest.", "Danger"));

        _plSelBox = new VBoxContainer { Visible = false };
        _plSelBox.AddChild(new HSeparator());
        _plSelInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _plSelBox.AddChild(_plSelInfo);
        _plSelCables = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _plSelBox.AddChild(_plSelCables);
        _plCycle = FocusBtn("Another kind of connector", () => CycleConnector());
        _plCycle.TooltipText = "Swap this house connector for the next kind made for the same surface";
        _plSelBox.AddChild(_plCycle);
        _plDeletePole = FocusBtn("Delete  (Del)", () => DeleteSelected(), "DangerButton");
        _plDeletePole.TooltipText = "Delete it and its cables (Ctrl+Z brings them back)";
        _plSelBox.AddChild(_plDeletePole);
        page.AddChild(_plSelBox);

        _plCableBox = new VBoxContainer { Visible = false };
        _plCableBox.AddChild(new HSeparator());
        _plCableInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _plCableBox.AddChild(_plCableInfo);
        _plDeleteCable = FocusBtn("Remove the cable  (Del)", () => DeleteSelectedCable(), "DangerButton");
        _plCableBox.AddChild(_plDeleteCable);
        page.AddChild(_plCableBox);
        BuildPolesWorld();
        BuildPlanWorld();
    }

    void RefreshPolesPanel()
    {
        // what the file no longer has cannot stay selected or be the start of a cable
        if (SelectedCableId != null && AllCables().All(c => c.Id != SelectedCableId)) SelectedCableId = null;
        if (ConnectFrom != null && ObjectData(ConnectFrom) == null) ConnectFrom = null;

        var hasOrigin = Map.OriginEpsg5514 != null;
        _plNoOrigin.Visible = !hasOrigin;
        foreach (var (a, b) in _poleButtons) { b.SetPressedNoSignal(a.Id == PlacementAsset); b.Disabled = !hasOrigin; }

        var poles = AllPoles();
        var cables = AllCables();
        var houses = AllObjects().Count(o => EditsFile.IsConnectorAsset(o.Asset));
        _plCount.Text = $"{poles.Count} poles, {cables.Count} cables, {houses} house connectors"
            + (cables.Count(c => c.LengthM < 0) is var stale and > 0 ? $" ({stale} with a missing end)" : "");
        _plPlan.Text = Planning ? "Stop planning  (Esc)" : "Plan  (P)";
        _plPlan.Disabled = !hasOrigin;
        _plConnect.Text = Connecting ? "Stop  (Esc)" : "Connect  (C)";
        _plConnect.Disabled = !Connecting && poles.Count == 0;

        // plan mode shows the plan alone
        _plPlanBox.Visible = Planning;
        _plGrid.Visible = _plConnect.Visible = _plCount.Visible = !Planning;
        if (Planning)
        {
            _plPlanInfo.Text = PlanSummary();
            _plBuild.Disabled = PlanPoleCount == 0;
            _plClear.Disabled = _plan.Count == 0;
        }

        var pole = !Planning && SelectedObject is { } so && EditsFile.IsWireAsset(so.Asset) ? so : null;
        var cable = Planning || SelectedCableId == null ? null : cables.FirstOrDefault(c => c.Id == SelectedCableId);
        _plSelBox.Visible = pole != null;
        _plCableBox.Visible = cable != null;
        _plCycle.Visible = pole != null && EditsFile.IsConnectorAsset(pole.Asset);
        if (cable != null)
            _plCableInfo.Text = $"{cable.Id}: {cable.From} to {cable.To}" + (cable.LengthM >= 0 ? $", {cable.LengthM:0.0} m" : " (an end is missing)");
        var key = pole == null ? "" : pole.Id + "|" + string.Join(",", cables.Where(c => c.From == pole.Id || c.To == pole.Id).Select(c => $"{c.Id}:{c.From}:{c.To}:{c.LengthM:0.0}"));
        if (pole != null) _plSelInfo.Text = $"{pole.Id}  {AssetName(pole.Asset)}";
        if (key == _plRowsKey) return;
        _plRowsKey = key;
        foreach (var c in _plSelCables.GetChildren()) { _plSelCables.RemoveChild(c); c.QueueFree(); }
        if (pole == null) return;
        foreach (var c in cables.Where(c => c.From == pole.Id || c.To == pole.Id))
        {
            var id = c.Id;
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{c.Id} to {(c.From == pole.Id ? c.To : c.From)}" + (c.LengthM >= 0 ? $"  {c.LengthM:0.0} m" : "  (missing)"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true,
            });
            var rm = FocusBtn("Remove", () => DisconnectCable(id));
            rm.TooltipText = $"Remove {c.Id} (Ctrl+Z brings it back)";
            row.AddChild(rm);
            _plSelCables.AddChild(row);
        }
    }

    public string PolesHintText()
        => Planning ? $"planning ({PlanPoleCount} poles, {PlanHouseCount} houses): LMB a pole point, RMB a house wall or roof, click a point again to remove it, Backspace removes the last, Enter builds, Esc stops"
            : Connecting
            ? (ConnectFrom == null ? "connecting: LMB on the pole to start from, Esc or RMB stops"
                : $"connecting from {ConnectFrom}: LMB on the next pole strings a cable and carries on from it, LMB on a house wall or roof connects the house, Esc or RMB stops, Ctrl+Z undo")
            : SelectedCableId != null ? $"{SelectedCableId}: Del removes the cable, Esc deselects, C connects poles"
            : SelectedObjectId != null && IsConnector(SelectedObjectId) ? "house connector: drag moves, the ring or R / Shift+R turns, Del deletes it and its cable, Esc deselects"
            : SelectedObjectId != null ? "pole: drag moves, drag the ring or R / Shift+R turns, C strings a cable from it, Del deletes it and its cables, Esc deselects"
            : "poles: P plans a line, C connects by hand, choose a pole to place it, LMB selects a pole or a cable, Esc leaves the tool";

    // for the smoke tests
    public string PolesCountText => _plCount.Text;
    public string PolesConnectText => _plConnect.Text;
    public string PolesSelectionText => _plSelBox.Visible ? _plSelInfo.Text : _plCableBox.Visible ? _plCableInfo.Text : "";
    public string PolesPlanText => _plPlanInfo.Text;
    public int PolesCableRowCount => _plSelCables.GetChildCount();
    public IReadOnlyList<string> PolePaletteIds => _poleButtons.Select(p => p.info.Id).ToList();
}

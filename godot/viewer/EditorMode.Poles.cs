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

    public bool IsPole(string objectId) => ObjectData(objectId)?.Asset.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal) == true;

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
        if (AllPoles().Count < 2) { Say("place two or more poles first, then connect them", true); return; }
        DisarmPlacement(false);
        if (GateArmed) CancelGate();
        SelectCable(null);
        Connecting = true;
        ConnectFrom = SelectedObjectId != null && IsPole(SelectedObjectId) ? SelectedObjectId : null;
        Say(ConnectFrom != null ? $"connecting from {ConnectFrom}: click the next pole (Esc stops)" : "connecting: click the pole to start from (Esc stops)");
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
    /// cable from the start and becomes the new start. Returns true when a cable was strung.
    /// </summary>
    public bool ConnectClick(string? objectId)
    {
        if (!Connecting) return false;
        if (objectId == null) { Say("click a pole (Esc stops connecting)", true); return false; }
        if (!IsPole(objectId)) { Say($"{objectId} is not a utility pole", true); return false; }
        if (ConnectFrom == null || ObjectData(ConnectFrom) == null)
        {
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
        if (joined) Say($"{from} and {objectId} are already joined; carrying on from {objectId}");
        // the line carries on from the pole just clicked, also along a stretch that is strung already
        if (ok || joined) { ConnectFrom = objectId; SelectObject(objectId); }
        _panelDirty = true;
        return ok;
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
        var o = FlyCam.ProjectRayOrigin(screen); var d = FlyCam.ProjectRayNormal(screen);
        if (Connecting) { ConnectClick(PickObject(o, d)); return; }
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

    /// <summary>Where the wires leave a pole: the middle of its line seats (8 m above its foot when it has none).</summary>
    Vector3 PoleTop(string id)
    {
        var seats = Map.SeatsOf(id).Where(s => s.Kind == "line").ToList();
        if (seats.Count > 0)
        {
            var c = System.Numerics.Vector3.Zero;
            foreach (var s in seats) c += s.Pos;
            c /= seats.Count;
            return new Vector3(c.X, c.Y, c.Z);
        }
        return Map.ObjectNodes.TryGetValue(id, out var n) ? n.GlobalPosition + Vector3.Up * 8f : Vector3.Zero;
    }

    /// <summary>The pole under the mouse that a click would string a cable to (connect mode, a start chosen); null otherwise.</summary>
    public string? ConnectTarget { get; private set; }

    void UpdatePolesTool()
    {
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
        var show = false;
        if (Connecting && Tool == EditorTool.Poles && ConnectFrom != null && Map.ObjectNodes.ContainsKey(ConnectFrom))
        {
            var mouse = GetViewport().GetMousePosition();
            if (!_looking && !OverUi(mouse) && !Modal)
            {
                var o = FlyCam.ProjectRayOrigin(mouse); var d = FlyCam.ProjectRayNormal(mouse);
                var under = PickObject(o, d);
                var valid = under != null && under != ConnectFrom && IsPole(under);
                Vector3? end = valid ? PoleTop(under!) : RayToGround(o, d, out var g) ? g + Vector3.Up * 0.3f : null;
                if (end is { } e)
                {
                    show = true;
                    ConnectTarget = valid ? under : null;
                    var ends = (PoleTop(ConnectFrom), e);
                    if (ends != _poleLineEnds)
                    {
                        _poleLineEnds = ends;
                        _poleLine.Mesh = Strip(new List<Vector3> { ends.Item1, ends.Item2 }, new List<int> { 0, 1 }, Mesh.PrimitiveType.Lines);
                    }
                    _poleLine.MaterialOverride = valid ? _poleLineOk : _poleLineIdle;
                    if (under != null)
                    {
                        _hoverLabel.Text = valid ? $"{under}: click to string a cable from {ConnectFrom}"
                            : under == ConnectFrom ? $"{under}: the cable starts here" : $"{under} is not a pole";
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
    Label _plNoOrigin = null!, _plPlaceStatus = null!, _plEmpty = null!, _plCount = null!, _plConnectState = null!;
    Label _plSelInfo = null!, _plSelNone = null!, _plCableInfo = null!;
    Button _plConnect = null!, _plDeletePole = null!, _plDeleteCable = null!;
    VBoxContainer _plSelBox = null!, _plSelCables = null!, _plCableBox = null!;
    string _plRowsKey = "";

    void BuildPolesPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Utility poles", ThemeTypeVariation = "Header" });
        page.AddChild(Note("Place poles, turn each so its crossarm stands across the line, then string cables between them. The wires are matched left to right, so they never cross."));
        _plNoOrigin = Note("This map has no origin_epsg5514, so poles cannot be placed. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(_plNoOrigin);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Place a pole", ThemeTypeVariation = "Header" });
        _plPlaceStatus = Note("");
        page.AddChild(_plPlaceStatus);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        grid.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        page.AddChild(grid);
        foreach (var a in Map.AssetCatalog.Where(a => a.Id.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal)).OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var info = a;
            var b = new Button
            {
                // the short name shows until the render arrives (and stays if it never does)
                Text = a.Id[EditsFile.PoleAssetPrefix.Length..].Replace('_', ' '), ToggleMode = true, FocusMode = Control.FocusModeEnum.All, ClipText = true, ExpandIcon = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = $"{a.Name}\n{a.Id}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ThemeTypeVariation = "PaletteButton", CustomMinimumSize = new Vector2(0, 128), IconAlignment = HorizontalAlignment.Center,
            };
            b.Pressed += () => { if (PlacementAsset == info.Id) DisarmPlacement(); else ArmPlacement(info.Id); b.ReleaseFocus(); };
            grid.AddChild(b);
            _poleButtons.Add((a, b));
        }
        _plEmpty = Note("No pole assets in the asset manifest. Run make poles-manifest.", "Danger");
        _plEmpty.Visible = _poleButtons.Count == 0;
        page.AddChild(_plEmpty);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Cables", ThemeTypeVariation = "Header" });
        _plCount = Note("");
        page.AddChild(_plCount);
        _plConnect = FocusBtn("Connect poles  (C)", ToggleConnecting, "PrimaryButton");
        _plConnect.TooltipText = "Click a pole, then the next one: each click strings a cable from the last pole, so a street is a row of clicks. Esc stops.";
        page.AddChild(_plConnect);
        _plConnectState = Note("");
        page.AddChild(_plConnectState);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Selection", ThemeTypeVariation = "Header" });
        _plSelNone = Note("Click a pole or a cable in the 3D view.");
        page.AddChild(_plSelNone);

        _plSelBox = new VBoxContainer { Visible = false };
        _plSelInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _plSelBox.AddChild(_plSelInfo);
        _plSelCables = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _plSelBox.AddChild(_plSelCables);
        _plDeletePole = FocusBtn("Delete the pole and its cables  (Del)", () => DeleteSelected(), "DangerButton");
        _plSelBox.AddChild(_plDeletePole);
        _plSelBox.AddChild(Note("Drag the pole to move it, drag the ring or press R / Shift+R to turn it. Its cables follow."));
        page.AddChild(_plSelBox);

        _plCableBox = new VBoxContainer { Visible = false };
        _plCableInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _plCableBox.AddChild(_plCableInfo);
        _plDeleteCable = FocusBtn("Remove the cable  (Del)", () => DeleteSelectedCable(), "DangerButton");
        _plCableBox.AddChild(_plDeleteCable);
        page.AddChild(_plCableBox);

        page.AddChild(new HSeparator());
        page.AddChild(Note("Esc steps back: stop connecting or placing, deselect, leave the tool. Ctrl+Z undoes every step."));
        BuildPolesWorld();
    }

    void RefreshPolesPanel()
    {
        // what the file no longer has cannot stay selected or be the start of a cable
        if (SelectedCableId != null && AllCables().All(c => c.Id != SelectedCableId)) SelectedCableId = null;
        if (ConnectFrom != null && ObjectData(ConnectFrom) == null) ConnectFrom = null;

        var hasOrigin = Map.OriginEpsg5514 != null;
        _plNoOrigin.Visible = !hasOrigin;
        foreach (var (a, b) in _poleButtons) { b.SetPressedNoSignal(a.Id == PlacementAsset); b.Disabled = !hasOrigin; }
        var placingPole = PlacementAsset != null && PlacementAsset.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal);
        _plPlaceStatus.Text = placingPole
            ? $"placing {AssetName(PlacementAsset!)}, rotation {GhostRotation:0} deg. Click to place, R / Shift+R turn it, Esc stops."
            : "Choose a pole, then click on the ground. It stays armed, so a row of poles is a row of clicks.";

        var poles = AllPoles();
        var cables = AllCables();
        _plCount.Text = poles.Count == 0
            ? "No poles placed yet. Choose one above and click on the ground."
            : $"{poles.Count} pole{(poles.Count == 1 ? "" : "s")} placed, {cables.Count} cable{(cables.Count == 1 ? "" : "s")}"
              + (cables.Count(c => c.LengthM < 0) is var stale and > 0 ? $" ({stale} with a missing pole, not drawn)" : "");
        _plConnect.Text = Connecting ? "Stop connecting  (Esc)" : "Connect poles  (C)";
        _plConnect.Disabled = !Connecting && poles.Count < 2;
        _plConnectState.Text = !Connecting
            ? (poles.Count < 2 ? "Cables hang between two placed poles: place at least two." : "Press C, click a pole, then the next one. Each click strings a cable from the last pole.")
            : ConnectFrom == null ? "Connecting: click the pole to start from." : $"Connecting from {ConnectFrom}: click the next pole. Esc stops.";

        var pole = SelectedObject is { } so && so.Asset.StartsWith(EditsFile.PoleAssetPrefix, StringComparison.Ordinal) ? so : null;
        var cable = SelectedCableId == null ? null : cables.FirstOrDefault(c => c.Id == SelectedCableId);
        _plSelBox.Visible = pole != null;
        _plCableBox.Visible = cable != null;
        _plSelNone.Visible = pole == null && cable == null;
        _plSelNone.Text = SelectedObject != null ? $"{SelectedObject.Id} is not a pole ({AssetName(SelectedObject.Asset)}). The Objects tool edits it." : "Click a pole or a cable in the 3D view.";
        if (cable != null)
            _plCableInfo.Text = $"{cable.Id}\n{cable.From} to {cable.To}" + (cable.LengthM >= 0 ? $", {cable.LengthM:0.0} m" : "\none of its poles is missing: it is not drawn");
        var key = pole == null ? "" : pole.Id + "|" + string.Join(",", cables.Where(c => c.From == pole.Id || c.To == pole.Id).Select(c => $"{c.Id}:{c.From}:{c.To}:{c.LengthM:0.0}"));
        if (pole != null)
            _plSelInfo.Text = $"{pole.Id}\n{AssetName(pole.Asset)}\nrotation {pole.Rotation:0.#} deg, on {pole.Parcel}";
        if (key == _plRowsKey) return;
        _plRowsKey = key;
        foreach (var c in _plSelCables.GetChildren()) { _plSelCables.RemoveChild(c); c.QueueFree(); }
        if (pole == null) return;
        var mine = cables.Where(c => c.From == pole.Id || c.To == pole.Id).ToList();
        if (mine.Count == 0) { _plSelCables.AddChild(Note("No cables on this pole yet. Press C and click the next pole.")); return; }
        foreach (var c in mine)
        {
            var id = c.Id;
            var other = c.From == pole.Id ? c.To : c.From;
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{c.Id} to {other}" + (c.LengthM >= 0 ? $"  {c.LengthM:0.0} m" : "  (missing)"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true,
            });
            var rm = FocusBtn("Remove", () => DisconnectCable(id));
            rm.TooltipText = $"Remove {c.Id} (Ctrl+Z brings it back)";
            row.AddChild(rm);
            _plSelCables.AddChild(row);
        }
    }

    public string PolesHintText()
        => Connecting
            ? (ConnectFrom == null ? "connecting: LMB on the pole to start from, Esc or RMB stops"
                : $"connecting from {ConnectFrom}: LMB on the next pole strings a cable and carries on from it, Esc or RMB stops, Ctrl+Z undo")
            : SelectedCableId != null ? $"{SelectedCableId}: Del removes the cable, Esc deselects, C connects poles"
            : SelectedObjectId != null ? "pole: drag moves, drag the ring or R / Shift+R turns, C strings a cable from it, Del deletes it and its cables, Esc deselects"
            : "poles: choose one to place, LMB selects a pole or a cable, C connects poles, Ctrl+P commands, Esc leaves the tool";

    // for the smoke tests
    public string PolesCountText => _plCount.Text;
    public string PolesConnectText => _plConnect.Text;
    public string PolesSelectionText => _plSelBox.Visible ? _plSelInfo.Text : _plCableBox.Visible ? _plCableInfo.Text : _plSelNone.Text;
    public int PolesCableRowCount => _plSelCables.GetChildCount();
    public IReadOnlyList<string> PolePaletteIds => _poleButtons.Select(p => p.info.Id).ToList();
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Suchohrad.MapLoader;

/// <summary>
/// Editor mode shell: a free-fly camera, parcel hover and selection, outlines (selection, hovered parcel,
/// property groups) draped on the terrain, a right-hand panel and a status line, and property group
/// operations written to edits.json (see EditsFile). The walk-mode Player is frozen and hidden meanwhile.
/// F2 toggles. The EditsWatcher regenerates whatever the saved file changes; its events show in the status line.
/// </summary>
public partial class EditorMode : Node
{
    public const float PanelWidth = 360f;
    const float StatusHeight = 58f;
    const float TopDownHeight = 120f;

    public Suchohrad.MapLoader.MapLoader Map { get; init; } = null!;
    public Player Player { get; init; } = null!;
    public EditsWatcher? Watcher { get; init; }
    public string EditsPath { get; init; } = "";

    public bool Active { get; private set; }
    public Camera3D FlyCam { get; private set; } = null!;
    public Camera3D CurrentCamera => Active ? FlyCam : Player.Camera;
    public SortedSet<string> Selection { get; } = new(StringComparer.Ordinal);
    public string? HoverId { get; private set; }
    public float Speed { get; private set; } = 40f;
    const float MinSpeed = 1f, MaxSpeed = 800f;
    static double SliderToSpeed(double v) => MinSpeed * Math.Pow(MaxSpeed / MinSpeed, v / 100.0);
    static double SpeedToSlider(double s) => 100.0 * Math.Log(s / MinSpeed) / Math.Log(MaxSpeed / MinSpeed);

    /// <summary>Set the fly speed in m/s (clamped); keeps the slider in sync.</summary>
    public void SetSpeed(float speed, bool fromSlider = false)
    {
        Speed = Mathf.Clamp(speed, MinSpeed, MaxSpeed);
        if (!fromSlider && _speedSlider != null) _speedSlider.SetValueNoSignal(SpeedToSlider(Speed));
        if (_speedLabel != null) _speedLabel.Text = $"{Speed:F0} m/s";
    }
    public string StatusMessage { get; private set; } = "";
    public Node3D OutlineRoot { get; private set; } = null!;
    public bool TopDown => _savedCam != null;
    public event Action? SelectionChanged;

    // outline kinds: node name -> segments last built (ground xz pairs)
    public const string OutlineSelected = "outline_selected", OutlineHover = "outline_hover";
    readonly Dictionary<string, MeshInstance3D> _outlines = new();
    readonly Dictionary<string, int> _segmentCounts = new();
    bool _outlinesDirty = true;
    bool _panelDirty = true;
    SortedDictionary<string, object?>? _disk;

    float _yaw, _pitch;
    bool _looking, _rmbMoved;
    (Vector3 pos, float yaw, float pitch)? _savedCam;
    (Vector3 pos, Vector3 euler, float pitch, bool input)? _savedPlayer;
    Vector3? _hoverPoint;

    CanvasLayer _ui = null!;
    PanelContainer _panel = null!, _statusBar = null!;
    HSlider _speedSlider = null!;
    Label _hudLabel = null!, _hoverLabel = null!, _statusLabel = null!, _hintLabel = null!;
    Label _selLabel = null!, _groupLabel = null!;
    Button _btnGroup = null!, _btnAdd = null!, _btnRemove = null!, _btnDissolve = null!;
    double _statusUntil;
    (DateTime, long) _seenEdits;
    double _pollT;

    public override void _Ready()
    {
        FlyCam = new Camera3D { Name = "EditorCamera", Far = 3000f, Near = 0.1f, Fov = 70f };
        AddChild(FlyCam);
        OutlineRoot = new Node3D { Name = "EditorOutlines", Visible = false };
        AddChild(OutlineRoot);
        BuildUi();
        BuildRegionWorld();
        if (Watcher != null)
        {
            Watcher.Launched += ids => Say("regenerating " + string.Join(", ", ids.Take(3)) + "...");
            Watcher.Applied += (_, ms) => Say($"applied in {ms:F0} ms");
            Watcher.Failed += m => Say("error: " + m, true);
            Watcher.InstantApplied += (_, _) => _panelDirty = true;
            Watcher.ParseFailed += m => Say("edits file is not valid JSON yet: " + m, true);
        }
        _seenEdits = Stat();
        ReloadGroups();
        SetActive(false);
    }

    // ---- mode switch ----

    public void SetActive(bool on)
    {
        if (on == Active && FlyCam.Current == on) return;
        Active = on;
        OutlineRoot.Visible = on;
        MarkerRoot.Visible = on;
        _ui.Visible = on;
        _looking = false;
        if (on)
        {
            _savedPlayer = (Player.GlobalPosition, Player.RotationDegrees, 0f, Player.InputEnabled);
            Player.InputEnabled = false;
            Player.Visible = false;
            Player.Velocity = Vector3.Zero;
            Player.SetPhysicsProcess(false);
            var e = Player.Camera.GlobalBasis.GetEuler();
            _pitch = e.X; _yaw = e.Y;
            FlyCam.GlobalPosition = Player.Camera.GlobalPosition;
            ApplyCamRotation();
            _savedCam = null;
            FlyCam.MakeCurrent();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _outlinesDirty = _panelDirty = true;
            ReloadGroups();
        }
        else
        {
            if (_savedPlayer is { } sp)
            {
                Player.GlobalPosition = sp.pos;
                Player.RotationDegrees = sp.euler;
                Player.InputEnabled = sp.input;
            }
            Player.Visible = true;
            Player.Velocity = Vector3.Zero;
            Player.SetPhysicsProcess(true);
            Player.Camera.MakeCurrent();
            Input.MouseMode = Input.MouseModeEnum.Captured;
            CancelDrag();
            CancelRegionGesture();
            DisarmPlacement(false);
            HoverId = null;
            _hoverLabel.Visible = false;
        }
    }

    /// <summary>Leave the editor and walk from the ground below the fly camera, facing where it looks.</summary>
    public void WalkFromHere()
    {
        if (!Active) return;
        var from = FlyCam.GlobalPosition;
        var ground = RayToGround(from + Vector3.Up * 2f, Vector3.Down, out var hit) ? hit : new Vector3(from.X, from.Y, from.Z);
        _savedPlayer = (ground + Vector3.Up * 1.0f, new Vector3(0, Mathf.RadToDeg(_yaw), 0), 0f, _savedPlayer?.input ?? true);
        SetActive(false);
        Say("dropped to the ground; F2 returns to the editor");
    }

    void ApplyCamRotation() => FlyCam.Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0));

    /// <summary>Place the fly camera at pos looking at target (smoke tests, screenshots).</summary>
    public void LookFrom(Vector3 pos, Vector3 target)
    {
        FlyCam.GlobalPosition = pos;
        var d = (target - pos).Normalized();
        _yaw = Mathf.Atan2(-d.X, -d.Z);
        _pitch = Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f));
        ApplyCamRotation();
    }

    /// <summary>T: snap to a top-down view above the cursor (or the screen centre); again restores the camera.</summary>
    public void ToggleTopDown()
    {
        if (_savedCam is { } s)
        {
            FlyCam.GlobalPosition = s.pos; _yaw = s.yaw; _pitch = s.pitch;
            ApplyCamRotation();
            _savedCam = null;
            return;
        }
        Vector3 ground;
        if (_hoverPoint is { } hp) ground = hp;
        else
        {
            var size = GetViewport().GetVisibleRect().Size;
            if (!RayToGround(FlyCam.ProjectRayOrigin(size / 2), FlyCam.ProjectRayNormal(size / 2), out ground))
                ground = FlyCam.GlobalPosition;
        }
        _savedCam = (FlyCam.GlobalPosition, _yaw, _pitch);
        _pitch = -Mathf.Pi / 2;
        FlyCam.GlobalPosition = new Vector3(ground.X, Map.GroundHeightOrMean(ground.X, ground.Z) + TopDownHeight, ground.Z);
        ApplyCamRotation();
    }

    // ---- picking ----

    /// <summary>
    /// Ground point under a ray: physics ray against the map colliders (terrain first of all), else a
    /// horizontal plane at the mean terrain height.
    /// </summary>
    public bool RayToGround(Vector3 origin, Vector3 dir, out Vector3 point)
    {
        var q = PhysicsRayQueryParameters3D.Create(origin, origin + dir * 3000f);
        q.CollideWithBodies = true;
        q.Exclude = new Godot.Collections.Array<Rid> { Player.GetRid() };
        var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) { point = hit["position"].AsVector3(); return true; }
        var plane = Map.MeanTerrainHeight;
        if (dir.Y < -1e-4f)
        {
            var t = (plane - origin.Y) / dir.Y;
            if (t > 0) { point = origin + dir * t; return true; }
        }
        point = default;
        return false;
    }

    /// <summary>Parcel under a ray (null: none); point is the ground point found.</summary>
    public ParcelInfo? PickRay(Vector3 origin, Vector3 dir, out Vector3 point)
        => RayToGround(origin, dir, out point) ? Map.ParcelAt(point.X, point.Z) : null;

    /// <summary>Parcel under a screen position of the main viewport.</summary>
    public ParcelInfo? PickScreen(Vector2 screen, out Vector3 point)
        => PickRay(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen), out point);

    bool OverUi(Vector2 mouse)
        => _ui.Visible && ((_panel.Visible && _panel.GetGlobalRect().HasPoint(mouse)) || _rail.GetGlobalRect().HasPoint(mouse)
            || _statusBar.GetGlobalRect().HasPoint(mouse) || (_cheat.Visible && _cheat.GetGlobalRect().HasPoint(mouse))
            || (_confirm.Visible && _confirm.GetGlobalRect().HasPoint(mouse)) || (_palette.Visible && _palette.GetGlobalRect().HasPoint(mouse)));

    // ---- selection ----

    /// <summary>Click semantics: plain click replaces the selection (empty space clears it), shift toggles one parcel.</summary>
    public void Click(string? parcelId, bool shift)
    {
        var before = string.Join(",", Selection);
        var unit = parcelId == null ? new List<string>() : GroupUnit(parcelId);
        if (shift)
        {
            if (unit.Count > 0)
            {
                // a group is one thing: toggle all of its members together
                if (unit.Any(Selection.Contains)) foreach (var m in unit) Selection.Remove(m);
                else foreach (var m in unit) Selection.Add(m);
            }
        }
        else
        {
            Selection.Clear();
            foreach (var m in unit) Selection.Add(m);
        }
        if (string.Join(",", Selection) == before) return;
        _outlinesDirty = _panelDirty = true;
        SelectionChanged?.Invoke();
    }

    /// <summary>The parcel itself, or all members of its property group (a group is shown and picked as one shape).</summary>
    List<string> GroupUnit(string parcelId)
    {
        var g = Group(parcelId);
        if (g == null) return new List<string> { parcelId };
        return Map.Parcels.Values.Where(p => p.Group == g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ---- groups ----

    public void GroupSelected()
    {
        if (Selection.Count < 2) { Say("select 2 or more parcels to group (Shift+click adds)", true); return; }
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count == 1 && Selection.All(p => Group(p) == groups[0]))
        {
            Say($"already one group: {groups[0]}", true);
            return;
        }
        if (groups.Count > 0)
        {
            var p = Selection.First(x => Group(x) != null);
            Say($"{p} is already in {Group(p)}; remove it first", true);
            return;
        }
        var sel = Selection.ToList();
        Mutate(root =>
        {
            var (ok, msg, gid) = EditsFile.CreateGroup(root, sel);
            return (ok, ok ? $"created {gid}" : msg);
        });
    }

    public void AddSelectedToGroup()
    {
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { Say(groups.Count == 0 ? "select a grouped parcel too, to say which group" : "selection spans several groups", true); return; }
        var add = Selection.Where(p => Group(p) == null).ToList();
        if (add.Count == 0) { Say("nothing to add: every selected parcel is already in a group", true); return; }
        var gid = groups[0]!;
        Mutate(root =>
        {
            var (ok, msg) = EditsFile.AddToGroup(root, gid, add);
            return (ok, msg);
        });
    }

    public void RemoveSelectedFromGroup()
    {
        var sel = Selection.Where(p => Group(p) != null).ToList();
        if (sel.Count == 0) { Say("none of the selected parcels is in a group", true); return; }
        Mutate(root => EditsFile.RemoveFromGroups(root, sel));
    }

    public void DissolveGroup()
    {
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { Say(groups.Count == 0 ? "no group in the selection" : "selection spans several groups", true); return; }
        var gid = groups[0]!;
        Mutate(root => EditsFile.Dissolve(root, gid));
    }

    string? Group(string parcel) => Map.Parcels.TryGetValue(parcel, out var p) ? p.Group : null;

    /// <summary>
    /// Reload the file from disk, apply the operation to that fresh content (another writer may have
    /// changed it), validate the result (the Python validator's rules), write it back atomically and refresh
    /// the group data. The touched `parcels.&lt;ID&gt;` / `groups.&lt;ID&gt;` entries (before and after) go on the undo
    /// stack under <paramref name="description"/> (default: the operation's message). Returns whether it was saved.
    /// </summary>
    bool Mutate(Func<SortedDictionary<string, object?>, (bool ok, string message)> op, string? description = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var root = EditsFile.Load(EditsPath);
            var before = ObjectEdits.CloneObj(root);
            var (ok, msg) = op(root);
            if (!ok) { Say(msg, true); ReloadGroups(); return false; }
            var errors = ObjectEdits.ValidateDocument(root);
            if (errors.Count > 0) { Say("not saved: " + errors[0], true); ReloadGroups(); return false; }
            var changes = DiffEntries(before, root);
            EditsFile.Save(EditsPath, root);
            SaveCount++;
            if (changes.Count > 0) PushUndo(new UndoRecord(description ?? msg, changes));
            AfterSave(sw);
            Say($"saved ({msg})");
            return true;
        }
        catch (Exception e)
        {
            Say($"not saved: {e.Message}", true);
            return false;
        }
    }

    /// <summary>Everything that follows a write: remember the file state, refresh groups, let the watcher do objects instantly.</summary>
    void AfterSave(System.Diagnostics.Stopwatch sw)
    {
        _seenEdits = Stat();
        ReloadGroups();
        var instant0 = Watcher?.InstantCount ?? 0;
        Watcher?.NotifySaved();
        LastSaveInstant = (Watcher?.InstantCount ?? 0) > instant0;
        LastSaveMs = sw.Elapsed.TotalMilliseconds;
        _panelDirty = true;
    }

    /// <summary>Wall time of the last save from reading the file to the objects being instanced (when instant), milliseconds.</summary>
    public double LastSaveMs { get; private set; }
    /// <summary>True when the last save was applied in process (objects only, no parcel-run).</summary>
    public bool LastSaveInstant { get; private set; }

    (DateTime, long) Stat()
    {
        try
        {
            var fi = new FileInfo(EditsPath);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    /// <summary>Fill ParcelInfo.Group from the edits file on disk.</summary>
    public void ReloadGroups()
    {
        Dictionary<string, string> member;
        try { _disk = EditsFile.Load(EditsPath); member = EditsFile.MembershipOf(_disk); }
        catch (Exception) { return; } // half-written or invalid: keep what we have
        foreach (var p in Map.Parcels.Values) p.Group = member.GetValueOrDefault(p.Id);
        _outlinesDirty = _panelDirty = true;
    }

    // ---- messages ----

    public void Say(string message, bool error = false)
    {
        StatusMessage = message;
        _statusUntil = Time.GetTicksMsec() / 1000.0 + (error ? 10 : 6);
        _statusLabel.Text = message;
        _statusLabel.Modulate = error ? new Color(1f, 0.55f, 0.45f) : Colors.White;
        GD.Print($"editor: {message}");
    }

    // ---- input ----

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
        {
            SetActive(!Active);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!Active) return;
        switch (e)
        {
            case InputEventMouseMotion mm when _looking:
                _rmbMoved = true;
                _yaw -= mm.Relative.X * 0.0025f;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.5698f, 1.5698f);
                ApplyCamRotation();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                _looking = true;
                _rmbMoved = false;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wu when !OverUi(wu.Position):
                SetSpeed(Speed * 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wd when !OverUi(wd.Position):
                SetSpeed(Speed / 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Tool == EditorTool.Region:
                RegionLeftPress(mb.Position, mb.CtrlPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when !_looking && Modal:
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && GateArmed:
                if (RayToGround(FlyCam.ProjectRayOrigin(mb.Position), FlyCam.ProjectRayNormal(mb.Position), out var gp)) GateBegin(gp);
                else Say("click on the ground near the fence", true);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && PlacementAsset != null:
                GetViewport().GuiReleaseFocus();
                if (RayToGround(FlyCam.ProjectRayOrigin(mb.Position), FlyCam.ProjectRayNormal(mb.Position), out var pp)) PlaceAt(pp, mb.CtrlPressed);
                else Say("click on the ground", true);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking:
                GetViewport().GuiReleaseFocus();
                LeftPress(mb.Position, mb.ShiftPressed);
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                if (HandleRegionKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Modal) break;
                if (HandleObjectKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (!k.CtrlPressed && !k.AltPressed && HandleToolKey(k.Keycode)) { GetViewport().SetInputAsHandled(); break; }
                switch (k.Keycode)
                {
                    case Key.F1: ToggleCheatSheet(); break;
                    case Key.F3: WalkFromHere(); break;
                    case Key.G: GroupSelected(); break;
                    case Key.T: ToggleTopDown(); break;
                    case Key.Enter or Key.KpEnter:
                        if (GetViewport().GuiGetFocusOwner() is (LineEdit or TextEdit)) break;
                        if (Tool == EditorTool.Group) { if (!_btnGroup.Disabled) GroupSelected(); }
                        else Apply();
                        break;
                    case Key.Escape:
                        if (_cheat.Visible) _cheat.Visible = false;
                        else if (PlacementAsset != null) DisarmPlacement();
                        else if (GateArmed) CancelGate();
                        else if (SelectedObjectId != null) SelectObject(null);
                        else if (Selection.Count > 0) Click(null, false);
                        else if (Tool != EditorTool.None) SelectTool(EditorTool.None);
                        break;
                }
                break;
        }
    }

    public override void _Input(InputEvent e)
    {
        // release is handled before the GUI so it is never swallowed
        if (Active && _looking && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right })
        {
            _looking = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            if (!_rmbMoved && PlacementAsset != null) DisarmPlacement(); // a right click (no look drag) leaves placement mode
        }
        if (Active && Dragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            EndDrag();
        if (Active && RegionDragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            EndCornerDrag();
        if (Active && GateDragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            GateEnd();
    }

    // ---- per frame ----

    public override void _Process(double delta)
    {
        if (!Active) return;
        Fly((float)delta);
        UpdateHover();
        UpdateGateDrag();
        UpdateObjectTools();
        UpdateRegionTool();
        _pollT += delta;
        if (_pollT > 0.5)
        {
            _pollT = 0;
            var st = Stat();
            if (st != _seenEdits) { _seenEdits = st; ReloadGroups(); }
        }
        if (_outlinesDirty) RebuildOutlines();
        if (_panelDirty) RefreshPanel();
        var c = FlyCam.GlobalPosition;
        _hudLabel.Text = $"EDITOR  |  {(Tool == EditorTool.None ? "no tool" : Tool.ToString())}  |  {Selection.Count} selected  |  {Speed:F0} m/s  |  x {c.X:F0}  y {-c.Z:F0}  z {c.Y:F0}" + (SelectedRegionId != null && Tool == EditorTool.Region ? $"  |  region {SelectedRegionId}" : "") + (_dirtyLabel.Visible || RegionDraftPending ? "  |  * UNSAVED" : "");
        if (Time.GetTicksMsec() / 1000.0 > _statusUntil && _statusLabel.Text != "")
            _statusLabel.Text = "";
    }

    void Fly(float dt)
    {
        if (Modal || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return; // typing is not flying
        if (Input.IsKeyPressed(Key.Ctrl)) return; // Ctrl+D, Ctrl+Z ... are editor commands, not flying
        var dir = Vector3.Zero;
        var fwd = -FlyCam.GlobalBasis.Z;
        if (Mathf.Abs(fwd.Y) > 0.98f) fwd = FlyCam.GlobalBasis.Y; // looking straight down: W goes screen-up
        var right = FlyCam.GlobalBasis.X;
        if (Input.IsKeyPressed(Key.W)) dir += fwd;
        if (Input.IsKeyPressed(Key.S)) dir -= fwd;
        if (Input.IsKeyPressed(Key.D)) dir += right;
        if (Input.IsKeyPressed(Key.A)) dir -= right;
        if (Input.IsKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) dir += Vector3.Down;
        if (Input.IsKeyPressed(Key.Space)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Shift)) dir += Vector3.Down;
        if (dir == Vector3.Zero) return;
        var speed = Speed * (Input.IsKeyPressed(Key.Alt) ? 3f : 1f);
        FlyCam.GlobalPosition += dir.Normalized() * speed * dt;
    }

    bool _hoverForced;

    /// <summary>Force the hovered parcel (smoke tests, screenshots); null returns to mouse hover.</summary>
    public void SetHover(string? id)
    {
        _hoverForced = id != null;
        if (id != HoverId) { HoverId = id; _outlinesDirty = true; }
    }

    void UpdateHover()
    {
        if (_hoverForced) return;
        var mouse = GetViewport().GetMousePosition();
        string? id = null;
        _hoverPoint = null;
        if (!_looking && !OverUi(mouse) && Tool != EditorTool.Region)
        {
            var p = PickScreen(mouse, out var point);
            if (p != null || point != default) _hoverPoint = point;
            id = p?.Id;
        }
        if (id != HoverId)
        {
            HoverId = id;
            _outlinesDirty = true;
        }
        if (id == null) { _hoverLabel.Visible = false; return; }
        var info = Map.Parcels[id];
        _hoverLabel.Text = info.Group != null
            ? $"{info.Group} ({GroupUnit(id).Count} parcels)"
            : $"{id} {info.Kind}";
        _hoverLabel.Position = mouse + new Vector2(16, 20);
        _hoverLabel.Visible = true;
    }

    // ---- outlines ----

    public int SegmentCount(string kind) => _segmentCounts.GetValueOrDefault(kind);
    public MeshInstance3D? OutlineNode(string kind) => _outlines.GetValueOrDefault(kind);

    /// <summary>Rebuild the outline meshes now (normally done on the next frame after a change).</summary>
    public void RebuildOutlines()
    {
        _outlinesDirty = false;
        var selSegs = UnitSegments(Selection.Where(Map.Parcels.ContainsKey));
        var hoverSegs = HoverId != null && !Selection.Contains(HoverId) ? UnitSegments(new[] { HoverId }) : new();
        SetOutline(OutlineHover, hoverSegs, new Color(1f, 1f, 1f, 0.55f), 0.25f, 0.30f);
        SetOutline(OutlineSelected, selSegs, new Color(1f, 0.92f, 0.1f), 0.29f, 0.55f);
    }

    /// <summary>Outline of parcels where a property group counts as one shape (outer perimeter only).</summary>
    List<(Vector2, Vector2)> UnitSegments(IEnumerable<string> ids)
    {
        var segs = new List<(Vector2, Vector2)>();
        var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            var g = Group(id);
            if (g == null) { segs.AddRange(AllEdges(Map.Parcels[id])); continue; }
            if (!seen.Add(g)) continue;
            segs.AddRange(GroupPerimeter(Map.Parcels.Values.Where(p => p.Group == g).ToList()));
        }
        return segs;
    }

    static IEnumerable<(Vector2, Vector2)> AllEdges(ParcelInfo p)
    {
        foreach (var ring in p.Rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                if (ring[i].DistanceSquaredTo(ring[j]) > 1e-8f) yield return (ring[j], ring[i]);
    }

    static (long, long) Q(Vector2 v) => ((long)Math.Round(v.X * 100.0), (long)Math.Round(v.Y * 100.0));

    /// <summary>
    /// Outer shape of a group: all member edges, minus those that occur in two members with endpoints
    /// matching to 1 cm (the shared boundaries).
    /// </summary>
    public static List<(Vector2, Vector2)> GroupPerimeter(IReadOnlyList<ParcelInfo> members)
    {
        var count = new Dictionary<((long, long), (long, long)), int>();
        var edges = new List<((long, long), (long, long), (Vector2, Vector2))>();
        foreach (var m in members)
            foreach (var (a, b) in AllEdges(m))
            {
                var qa = Q(a); var qb = Q(b);
                if (qa == qb) continue;
                var key = qa.CompareTo(qb) <= 0 ? (qa, qb) : (qb, qa);
                count[key] = count.GetValueOrDefault(key) + 1;
                edges.Add((key.Item1, key.Item2, (a, b)));
            }
        return edges.Where(e => count[(e.Item1, e.Item2)] == 1).Select(e => e.Item3).ToList();
    }

    void SetOutline(string name, List<(Vector2 a, Vector2 b)> segs, Color colour, float lift, float width)
    {
        _segmentCounts[name] = segs.Count;
        if (!_outlines.TryGetValue(name, out var mi))
        {
            mi = new MeshInstance3D
            {
                Name = name,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = colour,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
                },
            };
            OutlineRoot.AddChild(mi);
            _outlines[name] = mi;
        }
        mi.Mesh = segs.Count == 0 ? null : BuildRibbon(segs, lift, width);
    }

    // A flat ribbon of the given width per edge, split every 2 m so it follows the terrain; no collision.
    ArrayMesh BuildRibbon(List<(Vector2 a, Vector2 b)> segs, float lift, float width)
    {
        var verts = new List<Vector3>();
        var idx = new List<int>();
        var half = width / 2f;
        Vector3 P(Vector2 v, Vector2 side) { var q = v + side; return new Vector3(q.X, Map.GroundHeightOrMean(v.X, v.Y) + lift, q.Y); }
        foreach (var (a0, b0) in segs)
        {
            var dir = (b0 - a0).Normalized();
            var a = a0 - dir * half; var b = b0 + dir * half; // cover the corner joins
            var side = new Vector2(-dir.Y, dir.X) * half;
            var n = Mathf.Max(1, (int)Mathf.Ceil(a.DistanceTo(b) / 2f));
            for (var i = 0; i < n; i++)
            {
                var p = a.Lerp(b, (float)i / n); var q = a.Lerp(b, (float)(i + 1) / n);
                var k = verts.Count;
                verts.Add(P(p, side)); verts.Add(P(p, -side)); verts.Add(P(q, -side)); verts.Add(P(q, side));
                idx.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Texts shown in the panel (for the smoke test).</summary>
    public string PanelSelectionText => _selLabel.Text;
    public string PanelGroupText => _groupLabel.Text;
    public bool GroupButtonEnabled => !_btnGroup.Disabled;

    void RefreshPanel()
    {
        _panelDirty = false;
        var sel = Selection.Where(Map.Parcels.ContainsKey).Select(id => Map.Parcels[id]).ToList();
        var wholeGroup = sel.Count > 0 && sel[0].Group is { } wg && sel.All(p => p.Group == wg)
            && sel.Count == Map.Parcels.Values.Count(p => p.Group == wg) ? wg : null;
        if (sel.Count == 0) _selLabel.Text = "Nothing selected.\nClick a parcel; Shift+click adds.";
        else if (wholeGroup != null)
            _selLabel.Text = $"{wholeGroup}  (property group)\n{sel.Count} parcels\narea: {sel.Sum(p => p.AreaM2):F0} m2\nDissolve the group to edit its parts.";
        else if (sel.Count == 1)
        {
            var p = sel[0];
            _selLabel.Text = $"{p.Id}\nkind: {p.Kind}\narea: {p.AreaM2:F0} m2\ncadastral: {p.CadastralRef}\ngroup: {p.Group ?? "none"}";
        }
        else
        {
            var lines = sel.Take(12).Select(p => $"{p.Id}  {p.Kind}  {p.AreaM2:F0} m2" + (p.Group != null ? $"  [{p.Group}]" : ""));
            _selLabel.Text = $"{sel.Count} parcels selected\n" + string.Join("\n", lines) + (sel.Count > 12 ? $"\n... +{sel.Count - 12} more" : "");
        }
        var gids = sel.Select(p => p.Group).Where(g => g != null).Distinct().ToList();
        if (gids.Count == 0) _groupLabel.Text = "Selection is not in a group.";
        else
        {
            var lines = gids.Select(g => $"{g}: {string.Join(", ", Map.Parcels.Values.Where(p => p.Group == g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal))}");
            _groupLabel.Text = string.Join("\n", lines);
        }
        _groupCount.Text = sel.Count == 0 ? "Nothing marked yet" : $"{sel.Count} parcel{(sel.Count == 1 ? "" : "s")} marked";
        _btnGroup.Disabled = sel.Count < 2;
        _btnAdd.Disabled = gids.Count != 1 || sel.All(p => p.Group != null);
        _btnRemove.Disabled = gids.Count == 0;
        _btnDissolve.Disabled = gids.Count != 1;
        RefreshEditPanel();
        RefreshObjectsPanel();
        RefreshRegionsPanel();
    }
}

public partial class EditorMode
{
    /// <summary>Editor shortcuts for objects and undo; true when the key was used. Not reached while a text field has the focus.</summary>
    bool HandleObjectKey(InputEventKey k)
    {
        var mouse = GetViewport().GetMousePosition();
        var overViewport = !OverUi(mouse);
        if (k.CtrlPressed)
        {
            switch (k.Keycode)
            {
                case Key.Z: if (k.ShiftPressed) Redo(); else Undo(); return true;
                case Key.Y: Redo(); return true;
                case Key.D: DuplicateSelected(); return true;
            }
            return false;
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
}

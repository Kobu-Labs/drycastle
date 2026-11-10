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
    public const float PanelWidth = 300f;
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
    public float Speed { get; private set; } = 25f;
    public string StatusMessage { get; private set; } = "";
    public Node3D OutlineRoot { get; private set; } = null!;
    public bool TopDown => _savedCam != null;
    public event Action? SelectionChanged;

    // outline kinds: node name -> segments last built (ground xz pairs)
    public const string OutlineSelected = "outline_selected", OutlineGroup = "outline_group", OutlineHover = "outline_hover";
    readonly Dictionary<string, MeshInstance3D> _outlines = new();
    readonly Dictionary<string, int> _segmentCounts = new();
    bool _outlinesDirty = true;
    bool _panelDirty = true;

    float _yaw, _pitch;
    bool _looking;
    (Vector3 pos, float yaw, float pitch)? _savedCam;
    (Vector3 pos, Vector3 euler, float pitch, bool input)? _savedPlayer;
    Vector3? _hoverPoint;

    CanvasLayer _ui = null!;
    PanelContainer _panel = null!, _statusBar = null!;
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
        if (Watcher != null)
        {
            Watcher.Launched += ids => Say("regenerating " + string.Join(", ", ids.Take(3)) + "...");
            Watcher.Applied += (_, ms) => Say($"applied in {ms:F0} ms");
            Watcher.Failed += m => Say("error: " + m, true);
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
            HoverId = null;
            _hoverLabel.Visible = false;
        }
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
        => _ui.Visible && (_panel.GetGlobalRect().HasPoint(mouse) || _statusBar.GetGlobalRect().HasPoint(mouse));

    // ---- selection ----

    /// <summary>Click semantics: plain click replaces the selection (empty space clears it), shift toggles one parcel.</summary>
    public void Click(string? parcelId, bool shift)
    {
        var before = string.Join(",", Selection);
        if (shift)
        {
            if (parcelId != null && !Selection.Remove(parcelId)) Selection.Add(parcelId);
        }
        else
        {
            Selection.Clear();
            if (parcelId != null) Selection.Add(parcelId);
        }
        if (string.Join(",", Selection) == before) return;
        _outlinesDirty = _panelDirty = true;
        SelectionChanged?.Invoke();
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
    /// changed it), write it back atomically and refresh the group data. Returns whether it was saved.
    /// </summary>
    bool Mutate(Func<SortedDictionary<string, object?>, (bool ok, string message)> op)
    {
        try
        {
            var root = EditsFile.Load(EditsPath);
            var (ok, msg) = op(root);
            if (!ok) { Say(msg, true); ReloadGroups(); return false; }
            EditsFile.Save(EditsPath, root);
            _seenEdits = Stat();
            ReloadGroups();
            Say($"saved ({msg})");
            return true;
        }
        catch (Exception e)
        {
            Say($"not saved: {e.Message}", true);
            return false;
        }
    }

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
        try { member = EditsFile.MembershipOf(EditsFile.Load(EditsPath)); }
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
                _yaw -= mm.Relative.X * 0.0025f;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.5698f, 1.5698f);
                ApplyCamRotation();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                _looking = true;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                Speed = Mathf.Clamp(Speed * 1.25f, 1f, 800f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                Speed = Mathf.Clamp(Speed / 1.25f, 1f, 800f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking:
                var hit = PickScreen(mb.Position, out _);
                Click(hit?.Id, mb.ShiftPressed);
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                switch (k.Keycode)
                {
                    case Key.G: GroupSelected(); break;
                    case Key.T: ToggleTopDown(); break;
                    case Key.Escape: Click(null, false); break;
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
        }
    }

    // ---- per frame ----

    public override void _Process(double delta)
    {
        if (!Active) return;
        Fly((float)delta);
        UpdateHover();
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
        _hudLabel.Text = $"EDITOR   speed {Speed:F0} m/s (wheel, Shift x3)   x {c.X:F0}  y {-c.Z:F0}  z {c.Y:F0}";
        if (Time.GetTicksMsec() / 1000.0 > _statusUntil && _statusLabel.Text != "")
            _statusLabel.Text = "";
    }

    void Fly(float dt)
    {
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
        if (dir == Vector3.Zero) return;
        var speed = Speed * (Input.IsKeyPressed(Key.Shift) ? 3f : 1f);
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
        if (!_looking && !OverUi(mouse))
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
        _hoverLabel.Text = $"{id} {info.Kind}" + (info.Group != null ? $" [{info.Group}]" : "");
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
        var groups = Map.Parcels.Values.Where(p => p.Group != null).GroupBy(p => p.Group!);
        var groupSegs = new List<(Vector2, Vector2)>();
        foreach (var g in groups) groupSegs.AddRange(GroupPerimeter(g.ToList()));
        var selSegs = Selection.Where(Map.Parcels.ContainsKey).SelectMany(id => AllEdges(Map.Parcels[id])).ToList();
        var hoverSegs = HoverId != null && !Selection.Contains(HoverId) ? AllEdges(Map.Parcels[HoverId]).ToList() : new();
        SetOutline(OutlineHover, hoverSegs, new Color(1f, 1f, 1f, 0.55f), 0.25f, 0.30f);
        SetOutline(OutlineGroup, groupSegs, new Color(1f, 0.55f, 0.1f), 0.27f, 0.55f);
        SetOutline(OutlineSelected, selSegs, new Color(1f, 0.92f, 0.1f), 0.29f, 0.55f);
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

    // ---- UI ----

    void BuildUi()
    {
        _ui = new CanvasLayer { Name = "EditorUi", Layer = 10, Visible = false };
        AddChild(_ui);
        var bg = new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.86f), ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 };

        _hudLabel = Lbl(15, new Color(1, 1, 1, 0.95f), outline: true);
        _hudLabel.Position = new Vector2(12, 8);
        _ui.AddChild(_hudLabel);

        _hoverLabel = new Label { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hoverLabel.AddThemeFontSizeOverride("font_size", 16);
        _hoverLabel.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.75f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 3, ContentMarginBottom = 3 });
        _ui.AddChild(_hoverLabel);

        _panel = new PanelContainer { Name = "Panel" };
        _panel.AddThemeStyleboxOverride("panel", bg);
        _panel.AnchorLeft = 1; _panel.AnchorRight = 1; _panel.AnchorTop = 0; _panel.AnchorBottom = 1;
        _panel.OffsetLeft = -PanelWidth; _panel.OffsetRight = 0; _panel.OffsetTop = 0; _panel.OffsetBottom = 0;
        _ui.AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        box.AddChild(Header("Selection"));
        _selLabel = Lbl(15, new Color(0.95f, 0.95f, 0.95f));
        _selLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_selLabel);

        box.AddChild(new HSeparator());
        box.AddChild(Header("Group"));
        _groupLabel = Lbl(15, new Color(0.95f, 0.95f, 0.95f));
        _groupLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_groupLabel);
        _btnGroup = Btn("Group selected (G)", GroupSelected);
        _btnAdd = Btn("Add selected to group", AddSelectedToGroup);
        _btnRemove = Btn("Remove from group", RemoveSelectedFromGroup);
        _btnDissolve = Btn("Dissolve group", DissolveGroup);
        foreach (var b in new[] { _btnGroup, _btnAdd, _btnRemove, _btnDissolve }) box.AddChild(b);

        box.AddChild(new HSeparator());
        foreach (var name in new[] { "Fence", "Trees", "Objects" })
        {
            var h = Lbl(15, new Color(0.6f, 0.62f, 0.66f));
            h.Text = $"> {name}   (Editor 6/7)";
            box.AddChild(h);
        }

        _statusBar = new PanelContainer { Name = "StatusBar" };
        _statusBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.86f), ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 5, ContentMarginBottom = 5 });
        _statusBar.AnchorLeft = 0; _statusBar.AnchorRight = 1; _statusBar.AnchorTop = 1; _statusBar.AnchorBottom = 1;
        _statusBar.OffsetLeft = 0; _statusBar.OffsetRight = -PanelWidth; _statusBar.OffsetTop = -StatusHeight; _statusBar.OffsetBottom = 0;
        _statusBar.GrowVertical = Control.GrowDirection.Begin;
        _ui.AddChild(_statusBar);
        var sbox = new VBoxContainer();
        _statusBar.AddChild(sbox);
        _statusLabel = Lbl(16, Colors.White);
        _statusLabel.CustomMinimumSize = new Vector2(0, 22);
        _hintLabel = Lbl(13, new Color(0.7f, 0.74f, 0.8f));
        _hintLabel.Text = "WASD fly, Q/E down/up, RMB look, wheel speed, LMB select, Shift+LMB add, G group, T top-down, Esc clear, F2 walk";
        sbox.AddChild(_statusLabel);
        sbox.AddChild(_hintLabel);
    }

    static Label Lbl(int size, Color colour, bool outline = false)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", colour);
        if (outline)
        {
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            l.AddThemeConstantOverride("outline_size", 4);
        }
        return l;
    }

    static Label Header(string text)
    {
        var l = Lbl(17, new Color(1f, 0.92f, 0.4f));
        l.Text = text;
        return l;
    }

    static Button Btn(string text, Action onPress)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", 15);
        b.Pressed += onPress;
        return b;
    }

    /// <summary>Texts shown in the panel (for the smoke test).</summary>
    public string PanelSelectionText => _selLabel.Text;
    public string PanelGroupText => _groupLabel.Text;
    public bool GroupButtonEnabled => !_btnGroup.Disabled;

    void RefreshPanel()
    {
        _panelDirty = false;
        var sel = Selection.Where(Map.Parcels.ContainsKey).Select(id => Map.Parcels[id]).ToList();
        if (sel.Count == 0) _selLabel.Text = "Nothing selected.\nClick a parcel; Shift+click adds.";
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
        _btnGroup.Disabled = sel.Count < 2;
        _btnAdd.Disabled = gids.Count != 1 || sel.All(p => p.Group != null);
        _btnRemove.Disabled = gids.Count == 0;
        _btnDissolve.Disabled = gids.Count != 1;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Drycastle.MapLoader;

/// <summary>
/// Map viewer root. Loads the map, adds light and sky, spawns the player.
/// Options (after "--"): --map DIR, --seed N, --smoke, --screenshot PATH,
/// --view overview|oblique|street|player, --frames N, --assets DIR, --watch, --edits PATH,
/// --area slice|village, --state PATH, --edit-smoke --edit-smoke-plan FILE, --regions-smoke --regions-smoke-plan FILE,
/// --editor (editor mode, implies --watch; F2 toggles walk/editor), --editor-smoke --editor-smoke-plan FILE,
/// --panel-smoke --panel-smoke-plan FILE (Fence panel), --objects-smoke --objects-smoke-plan FILE (objects, gizmo, undo), --region-editor-smoke --region-editor-smoke-plan FILE (Region tool),
/// --editor-shot PNG [--select P-0001,P-0002] [--shot-at P-0001] [--shot-edits FILE] [--shot-collapse Group,Fence] [--shot-gate N] [--shot-tool Select|Group|Fence|Objects|View]
/// [--shot-object ID] [--shot-arm ASSET --shot-ghost x,y --shot-ghost-rot DEG] [--shot-filter TEXT] [--shot-open car,pot]
/// (editor screenshot of the main window).
/// Env: DRYCASTLE_MAP, DRYCASTLE_ASSETS, DRYCASTLE_UV (the uv executable).
/// --watch hot-reloads the edits file (see EditsWatcher) and writes editor-state.json.
/// </summary>
public partial class Viewer : Node3D
{
    readonly Drycastle.MapLoader.MapLoader _map = new();
    Player _player = null!;
    Camera3D? _shotCam;
    PackedScene _testCube = GD.Load<PackedScene>("res://test_cube.tscn");
    readonly Dictionary<string, string> _opts = new();
    readonly HashSet<string> _flags = new();
    string? _replaced;
    EditsWatcher? _watcher;
    EditorMode? _editor;

    public override void _Ready()
    {
        ParseArgs();
        var mapDir = _opts.GetValueOrDefault("map")
            ?? OS.GetEnvironment("DRYCASTLE_MAP");
        if (string.IsNullOrEmpty(mapDir))
            mapDir = ProjectSettings.GlobalizePath("res://") + "../../out/map";
        mapDir = Path.GetFullPath(mapDir);

        var repo = Path.GetFullPath(ProjectSettings.GlobalizePath("res://") + "../..");
        var assets = _opts.GetValueOrDefault("assets") ?? OS.GetEnvironment("DRYCASTLE_ASSETS");
        _map.AssetsDir = string.IsNullOrEmpty(assets) ? Path.Combine(repo, "assets") : Path.GetFullPath(assets);
        try
        {
            AddChild(_map.Root);
            _map.Load(mapDir);
        }
        catch (Exception e)
        {
            GD.PrintErr($"map load failed from '{mapDir}': {e.Message}");
            GetTree().Quit(2);
            return;
        }
        var s = _map.Stats;
        GD.Print($"map {_map.MapVersion}: chunks={s.ChunksLoaded} parcels={_map.Parcels.Count} " +
                 $"colliders={s.CollidersBuilt} spawns={_map.SpawnPoints.Count} " +
                 $"visual_triangles={s.Triangles} load_time={s.LoadSeconds:F2}s");

        AddEnvironment();
        _player = new Player { Name = "Player" };
        AddChild(_player);

        var seed = _opts.TryGetValue("seed", out var sv) && long.TryParse(sv, out var sl) ? sl : (long)Time.GetTicksUsec();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        GD.Print($"seed={seed}");
        if (_map.SpawnPoints.Count == 0) { GD.PrintErr("no spawn points"); GetTree().Quit(3); return; }
        var sp = _map.SpawnPoints[rng.RandiRange(0, _map.SpawnPoints.Count - 1)];
        _player.Position = sp.Position + Vector3.Up * 1.0f;
        _player.LookYawPitch(rng.RandfRange(0, 360), 0);

        var editorMode = _flags.Contains("editor") || _flags.Contains("editor-smoke") || _flags.Contains("panel-smoke") || _flags.Contains("objects-smoke") || _flags.Contains("region-editor-smoke") || _opts.ContainsKey("editor-shot");
        if (editorMode || _flags.Contains("watch") || _flags.Contains("edit-smoke") || _flags.Contains("regions-smoke")) StartWatcher(repo);
        if (editorMode)
        {
            _editor = new EditorMode { Name = "Editor", Map = _map, Player = _player, Watcher = _watcher, EditsPath = _watcher!.EditsPath };
            AddChild(_editor);
            // --editor starts in editor mode; the smoke and screenshot drivers switch it themselves
            if (_flags.Contains("editor") && !_flags.Contains("editor-smoke"))
            {
                // the Street View follower (docs/streetview.md) writes this file; the character follows it
                AddChild(new StreetViewFollow
                {
                    Name = "StreetViewFollow", Map = _map, Editor = _editor,
                    FilePath = Path.Combine(Path.GetDirectoryName(_watcher!.StatePath) ?? ".", "streetview-position.json"),
                });
                _editor.SetActive(true);
                // Interactive editing: don't render flat out (this pegged the GPU). Smoke/screenshot drivers stay uncapped.
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);
                Engine.MaxFps = FocusedFps;
            }
        }

        if (_flags.Contains("editor-smoke")) _ = RunEditorSmoke();
        else if (_flags.Contains("panel-smoke")) _ = RunPanelSmoke();
        else if (_flags.Contains("objects-smoke")) _ = RunObjectsSmoke();
        else if (_flags.Contains("region-editor-smoke")) _ = RunRegionEditorSmoke();
        else if (_opts.ContainsKey("editor-shot")) _ = RunEditorShot();
        else if (_flags.Contains("edit-smoke")) _ = RunEditSmoke();
        else if (_flags.Contains("regions-smoke")) _ = RunRegionsSmoke();
        else if (_flags.Contains("smoke")) _ = RunSmoke(rng);
        else if (_opts.ContainsKey("screenshot")) _ = RunScreenshot();
    }

    const int FocusedFps = 60, UnfocusedFps = 10;

    public override void _Notification(int what)
    {
        if (_editor == null || !_flags.Contains("editor") || _flags.Contains("editor-smoke")) return;
        if (what == NotificationApplicationFocusOut) Engine.MaxFps = UnfocusedFps;
        else if (what == NotificationApplicationFocusIn) Engine.MaxFps = FocusedFps;
    }

    public override void _ExitTree() => _map.FreeCaches();

    void StartWatcher(string repo)
    {
        var edits = _opts.GetValueOrDefault("edits") ?? Path.Combine(repo, "pipeline", "config", "edits.json");
        // the map folder name carries the area: "0.4.0-slice" / "0.4.0-village"
        var fromMap = _map.MapVersion.Contains('-') ? _map.MapVersion[(_map.MapVersion.LastIndexOf('-') + 1)..] : "";
        var area = _opts.GetValueOrDefault("area") ?? (fromMap is "slice" or "village" ? fromMap : "slice");
        if (area is not ("slice" or "village")) { GD.PrintErr($"--area must be slice or village, got '{area}'"); area = "slice"; }
        _watcher = new EditsWatcher
        {
            Name = "EditsWatcher",
            EditsPath = Path.GetFullPath(edits),
            Area = area,
            PipelineDir = Path.Combine(repo, "pipeline"),
            StatePath = Path.GetFullPath(_opts.GetValueOrDefault("state") ?? Path.Combine(repo, "out", "editor-state.json")),
            Map = _map,
            CameraProvider = () =>
            {
                var cam = _editor?.CurrentCamera ?? _player.Camera;
                return (cam.GlobalPosition, -cam.GlobalBasis.Z);
            },
            SelectionProvider = () => _editor?.Selection ?? (IReadOnlyCollection<string>)Array.Empty<string>(),
        };
        AddChild(_watcher);
    }

    void ParseArgs()
    {
        var a = OS.GetCmdlineUserArgs();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) _opts[k] = a[++i];
            else _flags.Add(k);
        }
    }

    void AddEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 250f,
            LightEnergy = 1.1f,
            RotationDegrees = new Vector3(-50, -35, 0),
        };
        AddChild(sun);
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.30f, 0.52f, 0.85f),
                SkyHorizonColor = new Color(0.70f, 0.82f, 0.93f),
                GroundHorizonColor = new Color(0.70f, 0.82f, 0.93f),
                GroundBottomColor = new Color(0.70f, 0.82f, 0.93f),
            }
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.9f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            }
        });
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && _shotCam == null && _editor?.Active != true)
            ToggleReplacement(_player.GlobalPosition);
    }

    void ToggleReplacement(Vector3 near)
    {
        if (_replaced != null)
        {
            _map.RestoreParcel(_replaced);
            GD.Print($"restored {_replaced}");
            _replaced = null;
            return;
        }
        var id = _map.NearestParcel(near, modelsOnly: true);
        if (id == null) return;
        _map.ReplaceParcel(id, _testCube);
        _replaced = id;
        GD.Print($"replaced {id} at {_map.AnchorOnTerrain(id)}");
    }

    async Task Physics(int n)
    {
        for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    async Task RunSmoke(RandomNumberGenerator rng)
    {
        _player.InputEnabled = false;
        var ok = true;
        await Physics(3);
        var spawns = _map.SpawnPoints;
        for (var n = 0; n < 3; n++)
        {
            var sp = spawns[rng.RandiRange(0, spawns.Count - 1)];
            _player.Velocity = Vector3.Zero;
            _player.GlobalPosition = sp.Position + Vector3.Up * 2.0f;
            for (var i = 0; i < 240 && !(i > 5 && _player.IsOnFloor()); i++) await Physics(1);
            var dy = _player.GlobalPosition.Y - sp.Position.Y;
            await Physics(30); // must stay put, not sink
            var dy2 = _player.GlobalPosition.Y - sp.Position.Y;
            var pass = _player.IsOnFloor() && Mathf.Abs(dy2) < 2.0f && Mathf.Abs(dy2 - dy) < 0.2f;
            ok &= pass;
            GD.Print($"{(pass ? "PASS" : "FAIL")} spawn {sp.Id} ({sp.Kind}): on_floor={_player.IsOnFloor()} " +
                     $"feet_vs_spawn_y={dy2:F2} m");
        }

        // meadow clutter: the streaming builds tiles around a focus (no-op for a map without meadow.bin)
        if (_map.MeadowLayers.Count > 0)
        {
            foreach (var sp in spawns.Where(s => s.Kind is "other" or "forest").Take(2))
            {
                for (var i = 0; i < 30; i++) { _map.SetClutterFocus(sp.Position); await Physics(1); }
                var (tiles, live) = _map.MeadowLive;
                var (multi, merged, mergedVerts) = _map.MeadowNodes;
                var pass = tiles > 0 && multi + merged > 0 && (merged == 0 || mergedVerts > 0);
                ok &= pass;
                GD.Print($"{(pass ? "PASS" : "FAIL")} meadow at {sp.Id} ({sp.Kind}): tiles={tiles} instances={live} multimesh nodes={multi} merged meshes={merged} ({mergedVerts} vertices)");
            }
        }

        // parcel replacement
        var id = _map.Parcels.Values.Where(p => p.HasModel && p.BuildingIds.Count > 0)
            .Select(p => p.Id).OrderBy(x => x).FirstOrDefault();
        if (id == null) { GD.Print("FAIL replace: no parcel with a model"); ok = false; }
        else
        {
            var visual = FindVisual(id);
            var before = visual?.Visible == true;
            var inst = _map.ReplaceParcel(id, _testCube);
            await Physics(2);
            var hidden = visual?.Visible == false;
            var shapesOff = ShapesDisabled(id, true);
            var anchor = _map.AnchorOnTerrain(id);
            var placed = inst.IsInsideTree() && inst.GlobalPosition.DistanceTo(anchor) < 0.01f;
            _map.RestoreParcel(id);
            await Physics(2);
            var shown = visual?.Visible == true;
            var shapesOn = ShapesDisabled(id, false);
            var freed = !IsInstanceValid(inst) || inst.IsQueuedForDeletion();
            var pass = before && hidden && shapesOff && placed && shown && shapesOn && freed;
            ok &= pass;
            GD.Print($"{(pass ? "PASS" : "FAIL")} replace/restore {id}: visual_before={before} hidden={hidden} " +
                     $"collision_off={shapesOff} placed_at_anchor={placed} visual_back={shown} " +
                     $"collision_on={shapesOn} instance_freed={freed}");
        }
        // per-parcel props nodes: visible visuals, enabled colliders, and a fence blocks the player
        var pv = _map.PropsVisuals;
        var ps = _map.PropsShapes;
        var propsOk = pv.Count > 0 && pv.Values.All(v => v.Visible)
            && ps.Values.All(l => l.All(c => !c.Disabled));
        ok &= propsOk;
        GD.Print($"{(propsOk ? "PASS" : "FAIL")} parcel props: visuals={pv.Count} collider_parcels={ps.Count} " +
                 $"all_visible_and_enabled={propsOk}");
        if (ps.Count > 0) ok &= FenceBlocks(ps.Keys.OrderBy(x => x).First());
        GD.Print(ok ? "SMOKE PASS" : "SMOKE FAIL");
        GetTree().Quit(ok ? 0 : 1);
    }

    // Walk the player at a fence collider's mesh from outside and check it is stopped.
    bool FenceBlocks(string id)
    {
        var cs = _map.PropsShapes[id][0];
        var faces = ((ConcavePolygonShape3D)cs.Shape).GetFaces();
        // first triangle with a roughly vertical face: take its centroid, step 1.5 m along the normal
        for (var i = 0; i + 2 < faces.Length; i += 3)
        {
            var n = (faces[i + 1] - faces[i]).Cross(faces[i + 2] - faces[i]).Normalized();
            if (Mathf.Abs(n.Y) > 0.1f) continue;
            var c = (faces[i] + faces[i + 1] + faces[i + 2]) / 3f;
            var from = c + n * 1.5f; from.Y = c.Y;
            var space = GetViewport().World3D.DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(from, c - n * 0.5f);
            q.CollideWithBodies = true;
            var hit = space.IntersectRay(q);
            var pass = hit.Count > 0;
            GD.Print($"{(pass ? "PASS" : "FAIL")} fence collision {id}: ray_hits_fence_collider={pass}");
            return pass;
        }
        GD.Print($"FAIL fence collision {id}: no vertical face");
        return false;
    }

    Node3D? FindVisual(string id) => _map.Root.FindChild($"parcel_{id}_visual", true, false) as Node3D;

    bool ShapesDisabled(string id, bool expected)
    {
        var col = _map.Root.FindChild($"parcel_{id}_collision", true, false);
        var cs = col?.FindChild("Shape", true, false) as CollisionShape3D;
        return cs != null && cs.Disabled == expected;
    }

    async Task RunScreenshot()
    {
        _player.InputEnabled = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        await Physics(3);
        var view = _opts.GetValueOrDefault("view", "player");
        // Render into an own 1600x900 viewport so the image does not depend on the window size.
        var vp = new SubViewport
        {
            Size = new Vector2I(1600, 900),
            World3D = GetViewport().World3D,
            Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(vp);
        PlaceShotCamera(view, vp);
        var frames = _opts.TryGetValue("frames", out var f) && int.TryParse(f, out var fi) ? fi : 90;
        var t0 = Time.GetTicksMsec();
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var avgFps = frames * 1000.0 / Math.Max(1, Time.GetTicksMsec() - t0);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = _opts["screenshot"];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var err = vp.GetTexture().GetImage().SavePng(path);
        var prims = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
        var calls = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        GD.Print($"screenshot {view}: {(err == Error.Ok ? "saved" : "FAILED " + err)} gpu='{RenderingServer.GetVideoAdapterName()}' " +
                 $"avg_fps={avgFps:F0} primitives={prims} draw_calls={calls}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    void PlaceShotCamera(string view, SubViewport vp)
    {
        // Village centre: mean anchor of parcels that have buildings.
        var withB = _map.Parcels.Values.Where(p => p.HasModel && p.BuildingIds.Count > 0).ToList();
        var c = new Vector3(
            withB.Average(p => p.AnchorXZ.X), 0, withB.Average(p => p.AnchorXZ.Z));
        c.Y = _map.GroundHeight(c.X, c.Z) is var h && !float.IsNaN(h) ? h : _player.GlobalPosition.Y;
        Vector3 pos, look = c + Vector3.Up * 3f;
        switch (view)
        {
            case "overview": pos = c + new Vector3(0, 140, 140); break;
            case "oblique": pos = c + new Vector3(-110, 45, 60); break;
            case "street":
                var road = _map.SpawnPoints.Where(s => s.Kind == "road")
                    .OrderBy(s => new Vector2(s.Position.X - c.X, s.Position.Z - c.Z).LengthSquared()).First();
                pos = road.Position + Vector3.Up * 1.65f;
                look = new Vector3(c.X, pos.Y, c.Z);
                break;
            case "player": pos = _player.Camera.GlobalPosition; look = pos + -_player.Camera.GlobalBasis.Z; break;
            default: throw new ArgumentException($"unknown view {view}");
        }
        _shotCam = new Camera3D { Far = 3000f, Fov = view == "street" ? 75f : 60f };
        vp.AddChild(_shotCam);
        _shotCam.GlobalPosition = pos;
        _shotCam.LookAt(look, Vector3.Up);
    }
}

using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The intel screen on the wall of the base (the military hangar's floor, or the command post of a bunker as the fallback; ADR 0017):
/// a steel frame and a quad that shows <see cref="IntelScreenView"/>, which is drawn into a <see cref="SubViewport"/>. The wall comes
/// from the first placed object of intel.json's <c>base_asset</c> (<see cref="IntelRoom.Place"/>: its position, turn and scale in
/// objects.json, never the model) or else from bunkers.json (<see cref="IntelAnchor"/>). The events on it are the board the server
/// sends (<see cref="Board"/>, filled by the network client); with several, the screen shows each for a few seconds in turn.
///
/// Client only, and costless away from the wall: further than <see cref="IntelFile.ViewRangeM"/> from the camera the quad is hidden
/// and the viewport does not render. Within range the view is rewritten about once a second and re-rendered for a few frames (the
/// container layout settles a frame or two after a text change), then the texture simply stays. Silent on headless runs.
/// </summary>
public partial class IntelScreen : Node3D
{
    /// <summary>The screen of this client (null before the map is loaded, on a map without an intel room and on headless runs), for the network layer to feed.</summary>
    public static IntelScreen? Current { get; private set; }

    /// <summary>The events announced, as the server last said; times are on this client's clock (<c>Time.GetTicksMsec() / 1000</c>).</summary>
    public IntelBoard Board { get; } = new();

    readonly IntelAnchor _anchor;
    readonly IntelFile _cfg;
    readonly Drycastle.MapLoader.MapLoader _loader;
    MapBase? _mapBase;
    SubViewport? _vp;
    IntelScreenView? _view;
    Node3D? _rig;
    bool _near;
    int _renders;
    int _shownVersion = -1;
    double _nextRefresh;

    const string DataPath = "res://data/intel.json";

    IntelScreen(IntelAnchor anchor, IntelFile cfg, Drycastle.MapLoader.MapLoader loader)
    {
        _anchor = anchor;
        _cfg = cfg;
        _loader = loader;
        Name = "IntelScreen";
    }

    /// <summary>intel.json. A broken file fails the run, like the other data files.</summary>
    public static IntelFile LoadConfig()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"intel.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        return IntelFile.Parse(json);
    }

    /// <summary>
    /// The screen of this map's base, or null when the map has none: no object of the base asset is placed and the fallback bunker
    /// (intel.json) has no wall reserved for it in bunkers.json (a map exported before the intel room). Then there is no intel room,
    /// and the announcement still reaches everyone.
    /// </summary>
    public static IntelScreen? Create(Shelters bunkers, Drycastle.MapLoader.MapLoader map)
    {
        var cfg = LoadConfig();
        var anchor = FindAnchor(cfg, bunkers, map);
        if (anchor == null)
        {
            GD.Print($"intel: no object of '{cfg.BaseAsset}' is placed and bunker '{cfg.BaseBunker}' has no wall for an intel screen in this map (export the map again); no intel room, announcements still work");
            return null;
        }
        GD.Print($"intel: the screen hangs on {anchor.HostId} at ({anchor.At.X:F1}, {-anchor.At.Y:F1}) local metres");
        return new IntelScreen(anchor, cfg, map);
    }

    /// <summary>The first placed object of the base asset carries the room; without one, the base bunker's reserved wall; else none.</summary>
    static IntelAnchor? FindAnchor(IntelFile cfg, Shelters bunkers, Drycastle.MapLoader.MapLoader map)
    {
        if (cfg.BaseAsset != "" && cfg.Room is { } room && map.FirstObjectOfAsset(cfg.BaseAsset) is { } o)
            return IntelRoom.Place(o.Id, new System.Numerics.Vector2(o.Position.X, o.Position.Z), o.Position.Y, o.YawDeg, o.Scale, room);
        return cfg.BaseBunker == "" ? null : bunkers.IntelScreens.FirstOrDefault(a => a.HostId == cfg.BaseBunker);
    }

    /// <summary>
    /// Opens the parts of the base object that intel.json names (the small door of the hangar's entrance end), so players can walk
    /// in: the closed prefab is solid all round. Both roles do it, as both build the object's collision. Nothing happens when the
    /// map has no such object or the object has no such part (a placeholder box).
    /// </summary>
    public static void OpenEntrance(Drycastle.MapLoader.MapLoader map)
    {
        var cfg = LoadConfig();
        if (cfg.BaseAsset == "" || cfg.Room is not { OpenParts.Count: > 0 } room || map.FirstObjectOfAsset(cfg.BaseAsset) is not { } o) return;
        var n = map.OpenObjectParts(o.Id, room.OpenParts);
        GD.Print($"intel: opened {n} of {room.OpenParts.Count} entrance part(s) of {o.Id}");
    }

    /// <summary>The grid reference of the base on the tactical map sheet ("F7"), or null when there is no map to say it on. The announcement names it.</summary>
    public string? BaseReference => Base is { } b ? TacticalMapMath.ReferenceOf(_anchor.At, b.Origin.X, b.SouthZ, b.SideMetres) : null;

    /// <summary>The shared map base (the minimap's and the tactical map's), taken on first use: only a loaded map can make one, and it is remembered for the map's lifetime, so it is not asked for while loading.</summary>
    MapBase? Base => _mapBase ??= MapBase.For(_loader);

    public override void _Ready()
    {
        Current = this;
        if (DisplayServer.GetName() == "headless") { SetProcess(false); return; }
        var width = Math.Max(64, (int)MathF.Round(_anchor.Width * IntelScreenView.PxPerMetre));
        var height = Math.Max(64, (int)MathF.Round(_anchor.Height * IntelScreenView.PxPerMetre));
        _vp = new SubViewport { Size = new Vector2I(width, height), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, TransparentBg = false, Disable3D = true, GuiDisableInput = true };
        AddChild(_vp);
        _view = new IntelScreenView(width);
        _vp.AddChild(_view);

        // local +Z is the way the screen faces, so the frame stands 6 cm out of the wall and the glass 2 mm in front of it
        var facing = new Vector3(_anchor.Facing.X, 0f, _anchor.Facing.Y);
        var centre = new Vector3(_anchor.At.X, (_anchor.Y0 + _anchor.Y1) / 2f, _anchor.At.Y);
        _rig = new Node3D { Name = "Rig", Transform = new Transform3D(Basis.LookingAt(-facing, Vector3.Up), centre), Visible = false };
        AddChild(_rig);
        const float frame = 0.07f, depth = 0.06f;
        _rig.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(_anchor.Width + 2 * frame, _anchor.Height + 2 * frame, depth) },
            Position = new Vector3(0, 0, depth / 2),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.115f, 0.10f), Roughness = 0.75f, Metallic = 0.4f },
        });
        if (_anchor.HostId.StartsWith("o-"))
        {
            // in a placed object (the hangar: an arched vault, no flat wall) the board stands free on two posts that run into the floor
            const float post = 0.09f, below = 1.4f;
            foreach (var side in new[] { -1f, 1f })
                _rig.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(post, below, post) },
                    Position = new Vector3(side * (_anchor.Width / 2f - post), -(_anchor.Height / 2f + frame + below / 2f), depth / 2),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.115f, 0.10f), Roughness = 0.75f, Metallic = 0.4f },
                });
        }
        _rig.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(_anchor.Width, _anchor.Height) },
            Position = new Vector3(0, 0, depth + 0.002f),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, // a lit screen, not lit by the lamp
                AlbedoTexture = _vp.GetTexture(),
                AlbedoTextureForceSrgb = true, // a viewport's texture is already in display colours; without this the screen looks washed out
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            },
        });
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    public override void _Process(double delta)
    {
        if (_vp == null || _view == null || _rig == null) return;
        var cam = GetViewport().GetCamera3D();
        var near = cam != null && cam.GlobalPosition.DistanceTo(_rig.GlobalPosition) <= _cfg.ViewRangeM;
        if (near != _near)
        {
            _near = near;
            _rig.Visible = near;
            _nextRefresh = 0;
            if (!near) _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }
        if (!near) return;

        var now = Time.GetTicksMsec() / 1000.0;
        if (now >= _nextRefresh || Board.Version != _shownVersion)
        {
            var events = Board.Events;
            var featured = events.Count == 0 ? 0 : (int)(Math.Floor(now / _cfg.RotateSeconds) % events.Count);
            _view.Show(events, featured, now, Base, Board.Version);
            _shownVersion = Board.Version;
            _nextRefresh = Math.Floor(now) + 1.0; // on the second, so the countdown does not skip a number
            _renders = 3;
        }
        if (_renders > 0)
        {
            _renders--;
            _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
    }
}

using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The intel screen on the wall of the base's command post (bunker, third floor down; ADR 0015): a steel frame and a quad that
/// shows <see cref="IntelScreenView"/>, which is drawn into a <see cref="SubViewport"/>. The wall comes from bunkers.json (the
/// pipeline reserves it, <see cref="IntelAnchor"/>), the bunker from intel.json. The events on it are the board the server sends
/// (<see cref="Board"/>, filled by the network client); with several, the screen shows each for a few seconds in turn.
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
    readonly MapBase? _mapBase;
    SubViewport? _vp;
    IntelScreenView? _view;
    Node3D? _rig;
    bool _near;
    int _renders;
    int _shownVersion = -1;
    double _nextRefresh;

    const string DataPath = "res://data/intel.json";

    IntelScreen(IntelAnchor anchor, IntelFile cfg, MapBase? mapBase)
    {
        _anchor = anchor;
        _cfg = cfg;
        _mapBase = mapBase;
        Name = "IntelScreen";
    }

    /// <summary>
    /// The screen of this map's base, or null when the map has none: the base bunker (intel.json) has no wall reserved for it in
    /// bunkers.json (a map exported before the intel room). Then there is no intel room, and the announcement still reaches everyone.
    /// A broken intel.json fails the run, like the other data files.
    /// </summary>
    public static IntelScreen? Create(Shelters bunkers, Drycastle.MapLoader.MapLoader map)
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"intel.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        var cfg = IntelFile.Parse(json);
        var anchor = bunkers.IntelScreens.FirstOrDefault(a => a.BunkerId == cfg.BaseBunker);
        if (anchor == null)
        {
            GD.Print($"intel: bunker '{cfg.BaseBunker}' has no wall for an intel screen in this map (export the map again); no intel room, announcements still work");
            return null;
        }
        return new IntelScreen(anchor, cfg, MapBase.For(map));
    }

    /// <summary>The grid reference of the base on the tactical map sheet ("F7"), or null when there is no map to say it on. The announcement names it.</summary>
    public string? BaseReference => _mapBase is { } b ? TacticalMapMath.ReferenceOf(_anchor.At, b.Origin.X, b.SouthZ, b.SideMetres) : null;

    public override void _Ready()
    {
        Current = this;
        if (DisplayServer.GetName() == "headless") { SetProcess(false); return; }
        var width = Math.Max(64, (int)MathF.Round(_anchor.Width * IntelScreenView.PxPerMetre));
        var height = Math.Max(64, (int)MathF.Round(_anchor.Height * IntelScreenView.PxPerMetre));
        _vp = new SubViewport { Size = new Vector2I(width, height), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, TransparentBg = false, GuiDisableInput = true };
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
            _view.Show(events, featured, now, _mapBase, Board.Version);
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

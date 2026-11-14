using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// The tactical map: a full-screen, north-up map sheet of the whole map, opened with the map key (M). It frees the mouse (the
/// shell asks <see cref="Hud.TacticalMapOpen"/>) and the legs keep working, but the view does not turn and nothing fires. Left
/// click, or Enter / the pad's A at the keyboard cursor (arrows, the D-pad), sets the player's waypoint; clicking it again or
/// right-clicking clears it. It is built from three parts:
/// - the sheet (<see cref="SheetLayer"/>): the ground's own colours (the terrain mesh, top-down) with roads, a labelled grid, the
///   frame and its tick marks. Godot keeps its draw commands, so it is redrawn only when the layout or the map changes;
/// - the markers (<see cref="MarkerLayer"/>): the player's arrow, vehicles, friends, UAV pings and the waypoint from the shared
///   <see cref="MapMarkers"/> (the data the minimap shows), redrawn each frame while the map is open; it takes the clicks;
/// - one panel on the right: the compass rose and the current grid reference. Nothing else (no legend, scale or readouts).
/// While closed it is invisible and does not process. The sheet's maths is in <see cref="TacticalMapMath"/> and <see cref="TacticalMapLayout"/>.
/// </summary>
public sealed partial class TacticalMap : Control
{
    const int Gutter = ShooterTheme.MapGutter;
    const float PanelGap = 16f, PanelMin = 260f, PanelMax = 360f;
    const int MinTextureSide = 512, MaxTextureSide = 2048;

    readonly MapMarkers _m;
    MapBase? _base;
    ImageTexture? _tex;
    int _texSide;
    bool _open, _layoutDirty = true;
    float _alpha;
    Vector2 _laidOutFor;
    /// <summary>Side of the square map in HUD units, HUD units per metre, the grid cell in metres and the cells per side.</summary>
    float _side, _k, _cell;
    int _cells;
    string[] _colLabels = Array.Empty<string>(), _rowLabels = Array.Empty<string>();

    readonly SheetLayer _sheet;
    readonly MarkerLayer _markers;
    readonly PanelContainer _right;
    readonly Label _gridValue;
    readonly CompassRose _rose = new();
    readonly Func<bool> _typing;
    /// <summary>The keyboard / pad cursor on the sheet (layer pixels), shown only after it was moved and until the mouse moves.</summary>
    Vector2 _cursor;
    bool _cursorShown;

    // what the readouts show now, so a label is written only when its value changes
    (int Column, int Row) _shownCell = (-1, -1);
    int _shownHeading = -1;
    bool _shownNone;

    public bool IsOpen => _open;

    /// <summary>The map; <paramref name="typing"/> says the chat box has the keyboard (Enter is then the chat's).</summary>
    public TacticalMap(MapMarkers markers, Func<bool> typing)
    {
        _m = markers;
        _typing = typing;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        SetProcess(false);
        SetProcessInput(false);

        _sheet = new SheetLayer(this) { MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
        _markers = new MarkerLayer(this) { MouseFilter = MouseFilterEnum.Stop };
        AddChild(_sheet);
        AddChild(_markers);

        // right: the compass rose and the current grid reference
        _right = Panel();
        var r = Column(_right);
        var roseRow = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        roseRow.AddChild(_rose);
        r.AddChild(roseRow);
        r.AddChild(Gap());
        r.AddChild(Text(Strings.T("map.grid"), "HudSmall"));
        _gridValue = Text("", "HudBig");
        r.AddChild(_gridValue);
        AddChild(_right);
    }

    /// <summary>Sandbox: the map also teleports (middle click). Shown as a second line of the hint; read when the map opens.</summary>
    public bool TeleportHint;

    /// <summary>Sandbox: called with the ground point (x, z) of a middle click on the sheet. Set by the shell; ignored unless <see cref="TeleportHint"/>.</summary>
    public Action<Vector2>? TeleportTo;

    /// <summary>The ground point (x, z) under the mouse, or null when the mouse is off the sheet or there is no map data.</summary>
    public Vector2? GroundAtMouse()
    {
        if (!_open || _base is not { } b || _side <= 0) return null;
        var f = (_sheet.GetLocalMousePosition() - new Vector2(Gutter, Gutter)) / _side;
        if (f.X < 0 || f.X > 1 || f.Y < 0 || f.Y > 1) return null;
        return new Vector2(b.Origin.X + f.X * b.SideMetres, b.Origin.Y + f.Y * b.SideMetres);
    }

    // ---- open and close ----

    /// <summary>Shows the map over <paramref name="map"/> (null: no map data, the sheet says so).</summary>
    public void Open(MapBase? map)
    {
        if (!ReferenceEquals(map, _base)) { _base = map; _tex = null; _texSide = 0; }
        _rose.SetLabels(Strings.T("map.dir.0"), Strings.T("map.dir.2"), Strings.T("map.dir.4"), Strings.T("map.dir.6"));
        _shownCell = (-1, -1); _shownHeading = -1; _shownNone = false;
        _cursorShown = false;
        _layoutDirty = true;
        _open = true;
        Visible = true;
        SetProcess(true);
        SetProcessInput(true);
        if (GameSettings.Current.ReduceMotion) SetAlpha(1f);
    }

    /// <summary>Starts hiding the map (a short fade, none with reduce motion). It stops processing once it is gone.</summary>
    public void Close()
    {
        _open = false;
        SetProcessInput(false);
        if (GameSettings.Current.ReduceMotion) FinishClose();
    }

    void SetAlpha(float a)
    {
        _alpha = a;
        Modulate = new Color(1, 1, 1, a);
    }

    void FinishClose()
    {
        SetAlpha(0f);
        Visible = false;
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        var target = _open ? 1f : 0f;
        if (!Mathf.IsEqualApprox(_alpha, target)) SetAlpha(Mathf.MoveToward(_alpha, target, (float)delta / ShooterTheme.MapFadeSeconds));
        if (!_open)
        {
            if (_alpha <= 0f) FinishClose();
            return; // fading out: nothing else changes
        }
        if (_layoutDirty || Size != _laidOutFor) Layout();
        UpdateReadouts();
        _markers.QueueRedraw();
    }

    // ---- the waypoint: click, or the keyboard / pad cursor ----

    /// <summary>How close (layer pixels) a click must be to the waypoint to take it away instead of moving it.</summary>
    const float WaypointHitPx = 16f;

    /// <summary>Sets or clears the waypoint at a layer position (a click, or the cursor + Enter). Ignored outside the map square.</summary>
    void PlaceAt(Vector2 layerPos)
    {
        if (_base is not { } b || _side <= 0) return;
        var f = (layerPos - new Vector2(Gutter, Gutter)) / _side;
        if (f.X < 0f || f.Y < 0f || f.X > 1f || f.Y > 1f) return;
        var g = TacticalMapMath.FromSheet(new System.Numerics.Vector2(f.X, f.Y), b.Origin.X, b.SouthZ, b.SideMetres);
        _m.Waypoint.Click(g, WaypointHitPx / Math.Max(_k, 1e-4f));
    }

    /// <summary>Keyboard and pad: arrows or the D-pad move a cursor over the sheet, Enter or A places the waypoint (or takes it away under the cursor), Delete clears it. The chat box keeps its own Enter.</summary>
    public override void _Input(InputEvent e)
    {
        if (!_open || _base == null || _side <= 0 || _typing()) return;
        var step = _cell * _k * 0.5f;
        var move = Vector2.Zero;
        switch (e)
        {
            case InputEventMouseMotion: _cursorShown = false; return;
            case InputEventKey { Pressed: true } k:
                switch (k.Keycode)
                {
                    case Key.Left: move = Vector2.Left; break;
                    case Key.Right: move = Vector2.Right; break;
                    case Key.Up: move = Vector2.Up; break;
                    case Key.Down: move = Vector2.Down; break;
                    case Key.Enter or Key.KpEnter when !k.Echo: Accept(); return;
                    case Key.Delete when !k.Echo: _m.Waypoint.Clear(); GetViewport().SetInputAsHandled(); return;
                }
                break;
            case InputEventJoypadButton { Pressed: true } j:
                switch (j.ButtonIndex)
                {
                    case JoyButton.DpadLeft: move = Vector2.Left; break;
                    case JoyButton.DpadRight: move = Vector2.Right; break;
                    case JoyButton.DpadUp: move = Vector2.Up; break;
                    case JoyButton.DpadDown: move = Vector2.Down; break;
                    case JoyButton.A: Accept(); return;
                    case JoyButton.X: _m.Waypoint.Clear(); GetViewport().SetInputAsHandled(); return;
                }
                break;
        }
        if (move == Vector2.Zero) return;
        if (!_cursorShown) StartCursor();
        var lo = new Vector2(Gutter, Gutter);
        _cursor = (_cursor + move * step).Clamp(lo, lo + new Vector2(_side, _side));
        GetViewport().SetInputAsHandled();

        void Accept()
        {
            if (!_cursorShown) { StartCursor(); GetViewport().SetInputAsHandled(); return; } // the first press only shows where it is
            PlaceAt(_cursor);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Shows the cursor, at the waypoint if one is set, else at the player, else in the middle.</summary>
    void StartCursor()
    {
        _cursorShown = true;
        var f = new Vector2(0.5f, 0.5f);
        if (_base is { } b)
        {
            var at = _m.Waypoint.At ?? (_m.HasPlayer ? new System.Numerics.Vector2(_m.Player.X, _m.Player.Y) : (System.Numerics.Vector2?)null);
            if (at is { } a) { var t = TacticalMapMath.ToSheet(a, b.Origin.X, b.SouthZ, b.SideMetres); f = new Vector2(Math.Clamp(t.X, 0f, 1f), Math.Clamp(t.Y, 0f, 1f)); }
        }
        _cursor = new Vector2(Gutter, Gutter) + f * _side;
    }

    // ---- layout ----

    void Layout()
    {
        _layoutDirty = false;
        _laidOutFor = Size;
        _side = TacticalMapLayout.Side(Size.X, Size.Y, ShooterTheme.Margin, Gutter, PanelMin, PanelGap);
        var o = TacticalMapLayout.Origin(Size.X, Size.Y, _side);
        var origin = new Vector2(o.X, o.Y);
        var frame = new Rect2(origin - new Vector2(Gutter, Gutter), new Vector2(_side + 2 * Gutter, _side + 2 * Gutter));
        foreach (var layer in new Control[] { _sheet, _markers })
        {
            layer.Position = frame.Position;
            layer.Size = frame.Size;
        }
        var panelW = Math.Clamp(frame.Position.X - PanelGap - ShooterTheme.Margin, PanelMin, PanelMax);
        _right.Position = new Vector2(frame.End.X + PanelGap, frame.Position.Y);
        _right.Size = new Vector2(panelW, 0);

        if (_base is { } b)
        {
            _k = _side / b.SideMetres;
            _cell = TacticalMapMath.CellSize(b.SideMetres);
            var cells = TacticalMapMath.Cells(b.SideMetres, _cell);
            if (cells != _cells || _colLabels.Length != cells)
            {
                _cells = cells;
                _colLabels = new string[cells];
                _rowLabels = new string[cells];
                for (var i = 0; i < cells; i++) { _colLabels[i] = TacticalMapMath.ColumnLabel(i); _rowLabels[i] = TacticalMapMath.RowLabel(i); }
            }
            var wanted = MinimapView.TextureSide(b.SideMetres, _k * Math.Max(0.01f, GetGlobalTransformWithCanvas().Scale.X), MinTextureSide, MaxTextureSide);
            if (_tex == null || MinimapView.NeedsRebuild(_texSide, wanted)) { _texSide = wanted; _tex = b.Texture(wanted); }
        }
        else
        {
            _k = 0; _cell = 0; _cells = 0;
        }
        _sheet.QueueRedraw();
    }

    // ---- readouts ----

    /// <summary>The compass heading and the grid reference. Each is written only when its value changes.</summary>
    void UpdateReadouts()
    {
        var has = _m.HasPlayer;
        var heading = has ? TacticalMapMath.HeadingDegrees(_m.Yaw) : -1;
        if (heading != _shownHeading)
        {
            _shownHeading = heading;
            _rose.SetHeading(heading);
        }
        if (_base is not { } b || !has)
        {
            if (!_shownNone)
            {
                _shownNone = true;
                _shownCell = (-1, -1);
                Set(_gridValue, Strings.T("map.none"));
            }
            return;
        }
        _shownNone = false;
        var map = TacticalMapMath.ToMap(new System.Numerics.Vector2(_m.Player.X, _m.Player.Y), b.Origin.X, b.SouthZ);
        var cell = TacticalMapMath.CellOf(map, b.SideMetres, _cell);
        if (cell != _shownCell) { _shownCell = cell; Set(_gridValue, TacticalMapMath.Reference(cell.Column, cell.Row)); }
    }

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

    // ---- building blocks ----

    static PanelContainer Panel() => new() { ThemeTypeVariation = "HudPanel", MouseFilter = MouseFilterEnum.Ignore };

    static VBoxContainer Column(PanelContainer p)
    {
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        p.AddChild(v);
        return v;
    }

    static Label Text(string text, string variation) =>
        new() { Text = text, ThemeTypeVariation = variation, MouseFilter = MouseFilterEnum.Ignore, ClipText = true };

    static Control Gap() => new() { CustomMinimumSize = new Vector2(0, ShooterTheme.Space), MouseFilter = MouseFilterEnum.Ignore };

    // ---- the sheet: terrain, roads, grid, frame, labels (drawn once per layout) ----

    sealed partial class SheetLayer : Control
    {
        readonly TacticalMap _o;
        public SheetLayer(TacticalMap owner) => _o = owner;

        public override void _Draw()
        {
            var side = _o._side;
            if (side <= 0) return;
            var font = GetThemeDefaultFont();
            const int fs = ShooterTheme.FontSmall;
            var map = new Rect2(Gutter, Gutter, side, side);
            DrawRect(new Rect2(Vector2.Zero, Size), ShooterTheme.MapPaper);
            if (_o._base is not { } b)
            {
                DrawRect(map, ShooterTheme.MapGround);
                DrawCentred(font, Strings.T("map.unavailable"), map.GetCenter() + new Vector2(0, -8), side, ShooterTheme.FontHead, ShooterTheme.Text);
                DrawCentred(font, Strings.T("map.unavailable.note"), map.GetCenter() + new Vector2(0, 24), side, fs, ShooterTheme.Dim);
                DrawRect(map, ShooterTheme.MapFrame, false, 2f);
                return;
            }
            if (_o._tex != null) DrawTextureRect(_o._tex, map, false);
            else DrawRect(map, ShooterTheme.MapGround);
            DrawRect(map, ShooterTheme.MapWash);
            DrawRoads(b, map);
            DrawGrid(map);
            DrawFrame(font, fs, map);
        }

        /// <summary>Roads as anti-aliased polylines: all the dark casings first, then the fills, so junctions read cleanly.</summary>
        void DrawRoads(MapBase b, Rect2 map)
        {
            var k = _o._k;
            var roads = b.Roads;
            for (var i = 0; i < roads.Count; i++)
            {
                var road = roads[i];
                for (var p = 0; p < road.Ground.Length; p++) road.Screen[p] = map.Position + (road.Ground[p] - b.Origin) * k;
                DrawPolyline(road.Screen, ShooterTheme.MapRoadCasing, Math.Max(1.5f, road.WidthM * k) + 2f, true);
            }
            for (var i = 0; i < roads.Count; i++)
                DrawPolyline(roads[i].Screen, roads[i].Colour, Math.Max(1.5f, roads[i].WidthM * k), true);
        }

        /// <summary>Square grid lines at every cell, from the south-west corner (the last column and row may be cut short by the map's edge).</summary>
        void DrawGrid(Rect2 map)
        {
            var step = _o._cell * _o._k;
            for (var i = 1; i < _o._cells; i++)
            {
                DrawLine(new Vector2(map.Position.X + i * step, map.Position.Y), new Vector2(map.Position.X + i * step, map.End.Y), ShooterTheme.MapGrid, ShooterTheme.Border);
                DrawLine(new Vector2(map.Position.X, map.End.Y - i * step), new Vector2(map.End.X, map.End.Y - i * step), ShooterTheme.MapGrid, ShooterTheme.Border);
            }
        }

        /// <summary>The neat line, the tick marks at every grid line, and the column letters and row numbers on all four edges.</summary>
        void DrawFrame(Font font, int fs, Rect2 map)
        {
            var k = _o._k;
            var cell = _o._cell;
            var extent = _o._base!.SideMetres;
            var textDrop = (font.GetAscent(fs) - font.GetDescent(fs)) / 2f;
            for (var i = 0; i <= _o._cells; i++)
            {
                var at = Math.Min(i * cell, extent) * k;
                var x = map.Position.X + at;
                var y = map.End.Y - at;
                DrawLine(new Vector2(x, Gutter - 6), new Vector2(x, Gutter), ShooterTheme.MapFrame, ShooterTheme.Border);
                DrawLine(new Vector2(x, map.End.Y), new Vector2(x, map.End.Y + 6), ShooterTheme.MapFrame, ShooterTheme.Border);
                DrawLine(new Vector2(Gutter - 6, y), new Vector2(Gutter, y), ShooterTheme.MapFrame, ShooterTheme.Border);
                DrawLine(new Vector2(map.End.X, y), new Vector2(map.End.X + 6, y), ShooterTheme.MapFrame, ShooterTheme.Border);
            }
            for (var i = 0; i < _o._cells; i++)
            {
                var from = i * cell;
                var to = Math.Min((i + 1) * cell, extent);
                var mid = (from + to) / 2f * k;
                var span = (to - from) * k;
                var cx = map.Position.X + from * k;
                var cy = map.End.Y - mid + textDrop;
                var col = _o._colLabels[i];
                var row = _o._rowLabels[i];
                var topBase = Gutter / 2f + textDrop;
                DrawString(font, new Vector2(cx, topBase), col, HorizontalAlignment.Center, span, fs, ShooterTheme.Text);
                DrawString(font, new Vector2(cx, map.End.Y + Gutter / 2f + textDrop), col, HorizontalAlignment.Center, span, fs, ShooterTheme.Text);
                DrawString(font, new Vector2(0, cy), row, HorizontalAlignment.Center, Gutter - 4, fs, ShooterTheme.Text);
                DrawString(font, new Vector2(map.End.X + 4, cy), row, HorizontalAlignment.Center, Gutter - 4, fs, ShooterTheme.Text);
            }
            DrawRect(map, ShooterTheme.MapFrame, false, 2f);
            DrawRect(new Rect2(0.5f, 0.5f, Size.X - 1, Size.Y - 1), ShooterTheme.MapFrame, false, ShooterTheme.Border);
        }

        void DrawCentred(Font font, string text, Vector2 centre, float width, int size, Color colour) =>
            DrawString(font, new Vector2(centre.X - width / 2f, centre.Y), text, HorizontalAlignment.Center, width, size, colour);
    }

    // ---- the markers (drawn each frame while open) ----

    sealed partial class MarkerLayer : Control
    {
        readonly TacticalMap _o;
        public MarkerLayer(TacticalMap owner) => _o = owner;

        /// <summary>Layer position of a ground point (x, z), kept inside the map so a far dot sits on its edge.</summary>
        Vector2 At(System.Numerics.Vector2 ground, MapBase b)
        {
            var p = new Vector2(Gutter, Gutter) + (new Vector2(ground.X, ground.Y) - b.Origin) * _o._k;
            return new Vector2(Math.Clamp(p.X, Gutter + 8, Gutter + _o._side - 8), Math.Clamp(p.Y, Gutter + 8, Gutter + _o._side - 8));
        }

        readonly List<GunfireBlip> _blipScratch = new();

        public override void _GuiInput(InputEvent e)
        {
            if (!_o._open || e is not InputEventMouseButton { Pressed: true } mb) return;
            if (mb.ButtonIndex == MouseButton.Left) _o.PlaceAt(mb.Position);
            else if (mb.ButtonIndex == MouseButton.Right) _o._m.Waypoint.Clear();
            else if (mb.ButtonIndex == MouseButton.Middle && _o.TeleportHint)
            {
                // this layer stops the mouse, so the click never reaches the shell's input; hand it over here
                if (_o.GroundAtMouse() is { } g) _o.TeleportTo?.Invoke(g);
            }
            else return;
            AcceptEvent();
        }

        /// <summary>A plain crosshair (outlined, so it reads on any ground) for the keyboard / pad cursor.</summary>
        void DrawCursor(Vector2 p)
        {
            const float r = 14f, gap = 4f;
            foreach (var d in new[] { Vector2.Left, Vector2.Right, Vector2.Up, Vector2.Down })
            {
                DrawLine(p + d * gap, p + d * r, ShooterTheme.Outline, 5f);
                DrawLine(p + d * gap, p + d * r, ShooterTheme.Waypoint, 2f);
            }
        }

        public override void _Draw()
        {
            if (_o._base is not { } b || !_o._m.HasPlayer || _o._side <= 0) return;
            var m = _o._m;
            for (var i = 0; i < m.Vehicles.Count; i++) MapGlyphs.Vehicle(this, At(m.Vehicles[i], b));
            if (m.TeamColours)
                for (var i = 0; i < m.Friends.Count; i++) MapGlyphs.Friend(this, At(m.Friends[i], b));
            var pings = m.Pings.Positions(Time.GetTicksMsec() / 1000.0); // empty once the UAV's pings expire, as on the minimap
            for (var i = 0; i < pings.Count; i++) MapGlyphs.Ping(this, At(pings[i], b), m.TeamColours);
            if (m.Blips is { } blips)
            {
                blips.Collect(Time.GetTicksMsec() / 1000.0, _blipScratch);
                var still = GameSettings.Current.ReduceMotion;
                foreach (var bl in _blipScratch) MapGlyphs.Blip(this, At(bl.Position, b), bl, still, 1.3f);
            }
            if (m.Waypoint.At is { } wp) MapGlyphs.Waypoint(this, At(wp, b), 1.3f);
            var me = At(new System.Numerics.Vector2(m.Player.X, m.Player.Y), b);
            MapGlyphs.Player(this, me, TacticalMapMath.HeadingRadians(m.Yaw));
            if (_o._cursorShown) DrawCursor(_o._cursor);
        }
    }

    // ---- the compass rose ----

    /// <summary>A north-up compass rose in the margin with a marker on its ring at the player's heading. Redrawn only when the heading changes.</summary>
    sealed partial class CompassRose : Control
    {
        const float Ring = 50f, LabelRadius = 72f;
        int _heading = -1;
        string _n = "N", _e = "E", _s = "S", _w = "W";

        public CompassRose()
        {
            CustomMinimumSize = new Vector2(176, 176);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public void SetLabels(string n, string e, string s, string w)
        {
            if (n == _n && e == _e && s == _s && w == _w) return;
            _n = n; _e = e; _s = s; _w = w;
            QueueRedraw();
        }

        public void SetHeading(int degrees)
        {
            if (degrees == _heading) return;
            _heading = degrees;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = Size / 2f;
            var font = GetThemeDefaultFont();
            const int fs = ShooterTheme.FontSmall;
            DrawArc(c, Ring, 0f, Mathf.Tau, 64, ShooterTheme.MapFrame, 2f, true);
            for (var i = 0; i < 16; i++) // ticks every 22.5 degrees, longer at the cardinal and intercardinal points
            {
                var a = i * Mathf.Tau / 16f;
                var dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                var len = i % 4 == 0 ? 12f : i % 2 == 0 ? 8f : 4f;
                DrawLine(c + dir * (Ring - len), c + dir * Ring, ShooterTheme.MapFrame, ShooterTheme.Border);
            }
            // the needle: a solid north half, an outlined south half
            DrawColoredPolygon(new[] { c + new Vector2(0, -Ring + 6), c + new Vector2(-8, 0), c + new Vector2(8, 0) }, ShooterTheme.Text);
            DrawPolyline(new[] { c + new Vector2(0, Ring - 6), c + new Vector2(-8, 0), c + new Vector2(8, 0), c + new Vector2(0, Ring - 6) }, ShooterTheme.Dim, ShooterTheme.Border, true);
            var drop = (font.GetAscent(fs) - font.GetDescent(fs)) / 2f;
            DrawLabel(font, fs, _n, c + new Vector2(0, -LabelRadius), drop, ShooterTheme.Text);
            DrawLabel(font, fs, _e, c + new Vector2(LabelRadius, 0), drop, ShooterTheme.Dim);
            DrawLabel(font, fs, _s, c + new Vector2(0, LabelRadius), drop, ShooterTheme.Dim);
            DrawLabel(font, fs, _w, c + new Vector2(-LabelRadius, 0), drop, ShooterTheme.Dim);
            if (_heading >= 0)
            {
                // a pointer outside the ring, tip on it, at the player's heading
                DrawSetTransform(c, _heading * Mathf.Pi / 180f, Vector2.One);
                DrawColoredPolygon(new[] { new Vector2(0, -Ring - 1), new Vector2(-7, -Ring - 14), new Vector2(7, -Ring - 14) }, ShooterTheme.Accent);
                DrawSetTransform(Vector2.Zero);
            }
        }

        void DrawLabel(Font font, int fs, string text, Vector2 at, float drop, Color colour) =>
            DrawString(font, new Vector2(at.X - 20f, at.Y + drop), text, HorizontalAlignment.Center, 40f, fs, colour);
    }

}

/// <summary>
/// The tactical map's marker shapes, drawn by the map and the minimap alike. Shapes differ as well as colours (never colour
/// only): the player is an arrow in a ring, vehicles are squares, friends are rings, enemy pings are discs. These are the
/// minimap's shapes, a little larger for the big sheet.
/// </summary>
static class MapGlyphs
{
    static readonly Vector2[] Arrow = { new(0, -13), new(10, 11), new(0, 5), new(-10, 11) };

    public static void Vehicle(CanvasItem c, Vector2 p)
    {
        c.DrawRect(new Rect2(p - new Vector2(7, 7), new Vector2(14, 14)), ShooterTheme.Outline);
        c.DrawRect(new Rect2(p - new Vector2(5, 5), new Vector2(10, 10)), ShooterTheme.Warn);
    }

    /// <summary>A ring, not a disc: its shape differs from the filled enemy pings.</summary>
    public static void Friend(CanvasItem c, Vector2 p)
    {
        c.DrawCircle(p, 10f, ShooterTheme.Outline);
        c.DrawCircle(p, 8f, ShooterTheme.Friend);
        c.DrawCircle(p, 4f, ShooterTheme.Outline);
    }

    public static void Ping(CanvasItem c, Vector2 p, bool teamColours)
    {
        c.DrawCircle(p, 9f, ShooterTheme.Outline);
        c.DrawCircle(p, 7f, teamColours ? ShooterTheme.Enemy : ShooterTheme.Danger);
    }

    /// <summary>
    /// A gunfire blip: a hollow diamond that swells and fades (a ring-like outline, so it is not the UAV's filled disc or the friends' ring;
    /// never colour only). Reduce-motion: fixed size, full opacity until it ends.
    /// </summary>
    public static void Blip(CanvasItem c, Vector2 p, GunfireBlip blip, bool reduceMotion, float scale)
    {
        var a = blip.Alpha(reduceMotion);
        var r = (5f + 5f * blip.Growth(reduceMotion)) * scale;
        var pts = new[] { p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0), p + new Vector2(0, -r) };
        var shell = ShooterTheme.Outline; shell.A *= a;
        var ink = ShooterTheme.Enemy; ink.A = a;
        c.DrawPolyline(pts, shell, 4f * scale, true);
        c.DrawPolyline(pts, ink, 2f * scale, true);
    }

    /// <summary>The waypoint: a downward triangle whose tip is the point, in the waypoint green (a shape no other marker has; never colour only).</summary>
    public static void Waypoint(CanvasItem c, Vector2 p, float scale)
    {
        var tri = new[] { p + new Vector2(-9, -15) * scale, p + new Vector2(9, -15) * scale, p };
        c.DrawColoredPolygon(new[] { tri[0] + new Vector2(-2, -2), tri[1] + new Vector2(2, -2), tri[2] + new Vector2(0, 3) }, ShooterTheme.Outline);
        c.DrawColoredPolygon(tri, ShooterTheme.Waypoint);
    }

    /// <summary>The player: an arrow pointing along the view, <paramref name="rotation"/> radians clockwise from up (north on the sheet), in a ring so nothing else on the map looks like it.</summary>
    public static void Player(CanvasItem c, Vector2 p, float rotation)
    {
        c.DrawArc(p, 20f, 0f, Mathf.Tau, 32, ShooterTheme.Outline, 4f, true);
        c.DrawArc(p, 20f, 0f, Mathf.Tau, 32, ShooterTheme.Accent, 2f, true);
        c.DrawSetTransform(p, rotation, new Vector2(1.3f, 1.3f));
        c.DrawColoredPolygon(Arrow, ShooterTheme.Outline);
        c.DrawSetTransform(p, rotation, Vector2.One);
        c.DrawColoredPolygon(Arrow, ShooterTheme.Text);
        c.DrawSetTransform(Vector2.Zero);
    }
}

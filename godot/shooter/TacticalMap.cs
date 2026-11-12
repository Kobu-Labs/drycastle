using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// The tactical map: a full-screen, north-up map sheet of the whole map, opened with the map key (M). It is view only: no
/// panning, zooming or waypoints, and the mouse and the player's controls stay as they are. It is built from three parts:
/// - the sheet (<see cref="SheetLayer"/>): terrain, parcels and roads from the shared <see cref="MapBase"/>, a labelled grid, the
///   frame and its tick marks. Godot keeps its draw commands, so it is redrawn only when the layout or the map changes;
/// - the markers (<see cref="MarkerLayer"/>): the player's arrow, vehicles, friends and UAV pings from the shared
///   <see cref="MapMarkers"/> (the data the minimap shows), redrawn each frame while the map is open;
/// - the margin furniture: a left panel (title, compass rose with the heading, grid reference, position) and a right panel
///   (legend, scale bar, how to close).
/// While closed it is invisible and does not process. The sheet's maths (cell size, references, heading, scale bar, layout)
/// is in <see cref="TacticalMapMath"/> and <see cref="TacticalMapLayout"/>.
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
    readonly PanelContainer _left, _right;
    readonly Label _subtitle, _gridValue, _positionValue, _headingValue, _hint;
    readonly CompassRose _rose = new();
    readonly ScaleBar _scale = new();
    readonly MapSwatch[] _swatches;

    // what the readouts show now, so a label is written only when its value changes
    (int Column, int Row) _shownCell = (-1, -1);
    int _shownE = -1, _shownN = -1, _shownHeading = -1;
    bool _shownNone;
    string _positionFormat = "", _headingFormat = "";
    readonly string[] _dir = new string[8];

    public bool IsOpen => _open;

    public TacticalMap(MapMarkers markers)
    {
        _m = markers;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        SetProcess(false);

        _sheet = new SheetLayer(this) { MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
        _markers = new MarkerLayer(this) { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_sheet);
        AddChild(_markers);

        // left: title, compass rose with the heading, grid reference and position
        _left = Panel();
        var l = Column(_left);
        l.AddChild(Text(Strings.T("map.title"), "HudHead"));
        _subtitle = Text("", "HudSmall");
        l.AddChild(_subtitle);
        l.AddChild(Gap());
        var roseRow = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        roseRow.AddChild(_rose);
        l.AddChild(roseRow);
        l.AddChild(Gap());
        l.AddChild(Text(Strings.T("map.heading"), "HudSmall"));
        _headingValue = Text("", "HudBig");
        l.AddChild(_headingValue);
        l.AddChild(Text(Strings.T("map.grid"), "HudSmall"));
        _gridValue = Text("", "HudBig");
        l.AddChild(_gridValue);
        l.AddChild(Text(Strings.T("map.position"), "HudSmall"));
        _positionValue = Text("", "HudDim");
        l.AddChild(_positionValue);
        l.AddChild(Text(Strings.T("map.position.note"), "HudSmall"));
        AddChild(_left);

        // right: legend, scale bar, how to close
        _right = Panel();
        var r = Column(_right);
        r.AddChild(Text(Strings.T("map.legend"), "HudSmall"));
        _swatches = new[]
        {
            new MapSwatch(MapSwatch.Kind.Player), new MapSwatch(MapSwatch.Kind.Vehicle),
            new MapSwatch(MapSwatch.Kind.Friend), new MapSwatch(MapSwatch.Kind.Ping, _m),
        };
        var names = new[] { "map.legend.you", "map.legend.vehicle", "map.legend.friend", "map.legend.enemy" };
        for (var i = 0; i < _swatches.Length; i++)
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", ShooterTheme.Space);
            row.AddChild(_swatches[i]);
            var label = Text(Strings.T(names[i]), "HudDim");
            label.ClipText = true;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            r.AddChild(row);
        }
        r.AddChild(Gap());
        r.AddChild(Text(Strings.T("map.scale"), "HudSmall"));
        r.AddChild(_scale);
        r.AddChild(Gap());
        _hint = Text("", "HudSmall");
        r.AddChild(_hint);
        AddChild(_right);
    }

    // ---- open and close ----

    /// <summary>Shows the map over <paramref name="map"/> (null: no map data, the sheet says so). Reads the key names and strings once.</summary>
    public void Open(MapBase? map)
    {
        if (!ReferenceEquals(map, _base)) { _base = map; _tex = null; _texSide = 0; }
        for (var i = 0; i < 8; i++) _dir[i] = Strings.T("map.dir." + i);
        _positionFormat = Strings.T("map.position.value");
        _headingFormat = Strings.T("map.heading.value");
        _rose.SetLabels(_dir[0], _dir[2], _dir[4], _dir[6]);
        Set(_hint, Strings.F("map.hint", InputBindings.Name("map")));
        _shownCell = (-1, -1); _shownE = _shownN = _shownHeading = -1; _shownNone = false;
        _layoutDirty = true;
        _open = true;
        Visible = true;
        SetProcess(true);
        if (GameSettings.Current.ReduceMotion) SetAlpha(1f);
        foreach (var s in _swatches) s.QueueRedraw();
    }

    /// <summary>Starts hiding the map (a short fade, none with reduce motion). It stops processing once it is gone.</summary>
    public void Close()
    {
        _open = false;
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
        _left.Position = new Vector2(frame.Position.X - PanelGap - panelW, frame.Position.Y);
        _left.Size = new Vector2(panelW, 0);
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
            Set(_subtitle, Strings.F("map.subtitle", _cell));
            var wanted = MinimapView.TextureSide(b.SideMetres, _k * Math.Max(0.01f, GetGlobalTransformWithCanvas().Scale.X), MinTextureSide, MaxTextureSide);
            if (_tex == null || MinimapView.NeedsRebuild(_texSide, wanted)) { _texSide = wanted; _tex = b.Texture(wanted); }
            _scale.Configure(_k, panelW - 2 * ShooterTheme.Space - 72f);
        }
        else
        {
            _k = 0; _cell = 0; _cells = 0;
            Set(_subtitle, Strings.T("map.none"));
            _scale.Configure(0f, 0f);
        }
        _sheet.QueueRedraw();
    }

    // ---- readouts ----

    /// <summary>Grid reference, position and heading. Each label is written only when its value changes.</summary>
    void UpdateReadouts()
    {
        var has = _m.HasPlayer;
        var heading = has ? TacticalMapMath.HeadingDegrees(_m.Yaw) : -1;
        if (heading != _shownHeading)
        {
            _shownHeading = heading;
            Set(_headingValue, has ? string.Format(_headingFormat, heading, _dir[TacticalMapMath.Octant(heading)]) : Strings.T("map.none"));
            _rose.SetHeading(heading);
        }
        if (_base is not { } b || !has)
        {
            if (!_shownNone)
            {
                _shownNone = true;
                _shownCell = (-1, -1); _shownE = _shownN = -1;
                Set(_gridValue, Strings.T("map.none"));
                Set(_positionValue, Strings.T("map.none"));
            }
            return;
        }
        _shownNone = false;
        var map = TacticalMapMath.ToMap(new System.Numerics.Vector2(_m.Player.X, _m.Player.Y), b.Origin.X, b.SouthZ);
        var cell = TacticalMapMath.CellOf(map, b.SideMetres, _cell);
        if (cell != _shownCell) { _shownCell = cell; Set(_gridValue, TacticalMapMath.Reference(cell.Column, cell.Row)); }
        var e = Math.Clamp((int)MathF.Floor(map.X), 0, (int)b.SideMetres);
        var n = Math.Clamp((int)MathF.Floor(map.Y), 0, (int)b.SideMetres);
        if (e != _shownE || n != _shownN) { _shownE = e; _shownN = n; Set(_positionValue, string.Format(_positionFormat, e, n)); }
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

        public override void _Draw()
        {
            if (_o._base is not { } b || !_o._m.HasPlayer || _o._side <= 0) return;
            var m = _o._m;
            for (var i = 0; i < m.Vehicles.Count; i++) MapGlyphs.Vehicle(this, At(m.Vehicles[i], b));
            if (m.TeamColours)
                for (var i = 0; i < m.Friends.Count; i++) MapGlyphs.Friend(this, At(m.Friends[i], b));
            var pings = m.Pings.Positions(Time.GetTicksMsec() / 1000.0); // empty once the UAV's pings expire, as on the minimap
            for (var i = 0; i < pings.Count; i++) MapGlyphs.Ping(this, At(pings[i], b), m.TeamColours);
            var me = At(new System.Numerics.Vector2(m.Player.X, m.Player.Y), b);
            MapGlyphs.Player(this, me, TacticalMapMath.HeadingRadians(m.Yaw));
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

    // ---- the scale bar ----

    /// <summary>A two-segment scale bar of a round length in metres (see <see cref="TacticalMapMath.ScaleBarMetres"/>), with its labels. Configured on layout, drawn once.</summary>
    sealed partial class ScaleBar : Control
    {
        float _px;
        string _zero = "0", _mid = "", _end = "";

        public ScaleBar()
        {
            CustomMinimumSize = new Vector2(0, 48);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public void Configure(float pxPerMetre, float maxPx)
        {
            var metres = TacticalMapMath.ScaleBarMetres(pxPerMetre, maxPx);
            _px = metres * pxPerMetre;
            var fmt = Strings.T("map.scale.m");
            _mid = metres > 0 ? Num(metres / 2f) : "";
            _end = metres > 0 ? string.Format(fmt, Num(metres)) : "";
            QueueRedraw();
        }

        static string Num(float v) => v.ToString(v < 1f ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture);

        public override void _Draw()
        {
            if (_px <= 0f) return;
            var font = GetThemeDefaultFont();
            const int fs = ShooterTheme.FontSmall;
            var x0 = 4f;
            const float y = 8f, h = 8f;
            DrawRect(new Rect2(x0, y, _px / 2f, h), ShooterTheme.Text);
            DrawRect(new Rect2(x0 + _px / 2f, y, _px / 2f, h), ShooterTheme.Outline);
            DrawRect(new Rect2(x0, y, _px, h), ShooterTheme.Text, false, ShooterTheme.Border);
            var labelY = y + h + 4f + font.GetAscent(fs);
            DrawString(font, new Vector2(x0 - 30f, labelY), _zero, HorizontalAlignment.Center, 60f, fs, ShooterTheme.Dim);
            DrawString(font, new Vector2(x0 + _px / 2f - 30f, labelY), _mid, HorizontalAlignment.Center, 60f, fs, ShooterTheme.Dim);
            DrawString(font, new Vector2(x0 + _px - 30f, labelY), _end, HorizontalAlignment.Center, 60f, fs, ShooterTheme.Dim);
        }
    }

    // ---- the legend swatches: the same glyphs as the markers, so the legend always matches ----

    sealed partial class MapSwatch : Control
    {
        public enum Kind { Player, Vehicle, Friend, Ping }

        readonly Kind _kind;
        readonly MapMarkers? _m;

        public MapSwatch(Kind kind, MapMarkers? markers = null)
        {
            _kind = kind;
            _m = markers;
            CustomMinimumSize = new Vector2(32, 32);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var c = Size / 2f;
            switch (_kind)
            {
                case Kind.Player: MapGlyphs.Player(this, c, 0f); break;
                case Kind.Vehicle: MapGlyphs.Vehicle(this, c); break;
                case Kind.Friend: MapGlyphs.Friend(this, c); break;
                default: MapGlyphs.Ping(this, c, _m?.TeamColours ?? false); break;
            }
        }
    }
}

/// <summary>
/// The tactical map's marker shapes, drawn by the map and its legend alike. Shapes differ as well as colours (never colour
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
